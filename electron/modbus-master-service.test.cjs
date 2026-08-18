const test = require("node:test");
const assert = require("node:assert/strict");

const {
  readHoldingRegistersOnce,
  readInputRegistersOnce,
} = require("./modbus-master-service.cjs");

function createHarness({ parsed, transaction, buildError, transactionError, parseError } = {}) {
  const calls = [];
  const built = {
    adu: [1, 3, 0, 0, 0, 2, 196, 11],
    expectedResponseLength: 9,
    exceptionResponseLength: 5,
  };
  const io = transaction ?? {
    tx: built.adu,
    rx: [1, 3, 4, 0, 1, 0, 2, 42, 50],
    elapsedMs: 12,
  };
  const rustCore = {
    async buildReadHoldingRegisters(args) {
      calls.push({ step: "build", args });
      if (buildError) throw buildError;
      return built;
    },
    async parseReadHoldingRegisters(args) {
      calls.push({ step: "parse", args });
      if (parseError) throw parseError;
      return parsed ?? { status: "ok", exceptionCode: null, registers: [1, 2] };
    },
  };
  const serialService = {
    getStatus() {
      return { isOpen: true, config: { readTimeoutMs: 1000 } };
    },
    async transact(args) {
      calls.push({ step: "transact", args });
      if (transactionError) throw transactionError;
      return io;
    },
  };
  const ensureRustCore = async () => {
    calls.push({ step: "ensure" });
    return rustCore;
  };
  return { calls, dependencies: { rustCore, serialService, ensureRustCore }, built, io };
}

test("orchestrates Rust build, exclusive serial I/O, and Rust parse in order", async () => {
  const harness = createHarness();
  const result = await readHoldingRegistersOnce(harness.dependencies, {
    unitId: 1,
    startAddress: 0,
    quantity: 2,
  });

  assert.deepEqual(harness.calls.map((call) => call.step), ["ensure", "build", "transact", "parse"]);
  assert.equal(harness.calls[2].args.timeoutMs, 1000);
  assert.deepEqual(harness.calls[3].args.response, harness.io.rx);
  assert.deepEqual(result, {
    ok: true,
    tx: harness.io.tx,
    rx: harness.io.rx,
    elapsedMs: 12,
    crcValid: true,
    registers: [1, 2],
    error: null,
  });
});

test("returns a structured Modbus exception while preserving the raw frames", async () => {
  const harness = createHarness({
    parsed: {
      status: "exception",
      exceptionCode: 2,
      exceptionName: "非法数据地址",
      registers: [],
    },
    transaction: {
      tx: [1, 3, 0, 0, 0, 125, 132, 42],
      rx: [1, 0x83, 2, 0xC0, 0xF1],
      elapsedMs: 4,
    },
  });
  const result = await readHoldingRegistersOnce(harness.dependencies, {
    unitId: 1,
    startAddress: 0,
    quantity: 125,
    timeoutMs: 250,
  });

  assert.equal(result.ok, false);
  assert.equal(result.crcValid, true);
  assert.equal(result.error.code, "MODBUS_EXCEPTION");
  assert.equal(result.error.details.exceptionCode, 2);
  assert.deepEqual(result.rx, [1, 0x83, 2, 0xC0, 0xF1]);
});

test("keeps TX and RX diagnostics when Rust rejects a bad CRC", async () => {
  const crcError = Object.assign(new Error("CRC 不匹配"), {
    code: "CRC_MISMATCH",
    details: { expected: 123, received: 456 },
  });
  const harness = createHarness({ parseError: crcError });
  const result = await readHoldingRegistersOnce(harness.dependencies, {
    unitId: 1,
    startAddress: 0,
    quantity: 2,
    timeoutMs: 500,
  });

  assert.equal(result.ok, false);
  assert.equal(result.crcValid, false);
  assert.equal(result.error.code, "CRC_MISMATCH");
  assert.deepEqual(result.tx, harness.io.tx);
  assert.deepEqual(result.rx, harness.io.rx);
});

test("keeps the built request and partial response on a serial timeout", async () => {
  const timeout = Object.assign(new Error("等待响应超时"), {
    code: "SERIAL_RESPONSE_TIMEOUT",
    details: { timeoutMs: 50, rx: [1, 3] },
  });
  const harness = createHarness({ transactionError: timeout });
  const result = await readHoldingRegistersOnce(harness.dependencies, {
    unitId: 1,
    startAddress: 0,
    quantity: 2,
    timeoutMs: 50,
  });

  assert.equal(result.ok, false);
  assert.equal(result.error.code, "SERIAL_RESPONSE_TIMEOUT");
  assert.deepEqual(result.tx, harness.built.adu);
  assert.deepEqual(result.rx, [1, 3]);
});

test("uses the live Rust core returned by recovery during the same read", async () => {
  const staleRustCore = {
    async buildReadHoldingRegisters() {
      throw new Error("stale client must not be used");
    },
  };
  const liveRustCore = {
    async buildReadHoldingRegisters() {
      return {
        adu: [1, 3, 0, 0, 0, 1, 132, 10],
        expectedResponseLength: 7,
        exceptionResponseLength: 5,
      };
    },
    async parseReadHoldingRegisters() {
      return { status: "ok", registers: [42] };
    },
  };
  const serialService = {
    getStatus: () => ({ isOpen: true, config: { readTimeoutMs: 1000 } }),
    transact: async ({ request }) => ({
      tx: request,
      rx: [1, 3, 2, 0, 42, 57, 155],
      elapsedMs: 3,
    }),
  };

  const result = await readHoldingRegistersOnce(
    {
      rustCore: staleRustCore,
      serialService,
      ensureRustCore: async () => liveRustCore,
    },
    { unitId: 1, startAddress: 0, quantity: 1, timeoutMs: 100 },
  );
  assert.equal(result.ok, true);
  assert.deepEqual(result.registers, [42]);
});

test("orchestrates an FC04 input-register transaction through its typed Rust commands", async () => {
  const calls = [];
  const built = {
    adu: [1, 4, 0, 0, 0, 2, 113, 203],
    expectedResponseLength: 9,
    exceptionResponseLength: 5,
  };
  const rustCore = {
    async buildReadInputRegisters(args) {
      calls.push({ step: "build-input", args });
      return built;
    },
    async parseReadInputRegisters(args) {
      calls.push({ step: "parse-input", args });
      return { status: "ok", registers: [42, 65_534] };
    },
  };
  const serialService = {
    getStatus: () => ({ isOpen: true, config: { readTimeoutMs: 1000 } }),
    async transact(args) {
      calls.push({ step: "transact", args });
      return {
        tx: args.request,
        rx: [1, 4, 4, 0, 42, 255, 254, 90, 116],
        elapsedMs: 6,
      };
    },
  };

  const result = await readInputRegistersOnce(
    {
      rustCore,
      serialService,
      ensureRustCore: async () => {
        calls.push({ step: "ensure" });
        return rustCore;
      },
    },
    { unitId: 1, startAddress: 0, quantity: 2, timeoutMs: 250 },
  );

  assert.deepEqual(calls.map((call) => call.step), [
    "ensure",
    "build-input",
    "transact",
    "parse-input",
  ]);
  assert.equal(calls[2].args.timeoutMs, 250);
  assert.deepEqual(result.registers, [42, 65_534]);
  assert.equal(result.ok, true);
});
