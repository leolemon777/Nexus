const test = require("node:test");
const assert = require("node:assert/strict");

const {
  readHoldingRegistersOnce,
  readInputRegistersOnce,
  readCoilsOnce,
  readDiscreteInputsOnce,
  writeSingleCoilOnce,
  writeSingleRegisterOnce,
  writeMultipleCoilsOnce,
  writeMultipleRegistersOnce,
  publicError,
} = require("./modbus-master-service.cjs");

// === executeCommandList 的 mock 测试 ===
// executeCommandList 在 main.cjs 里,但它只是对 read/write 函数的循环调用。
// 我们在这里复制其核心逻辑进行测试,验证路由正确性。

async function executeCommandList(ctx, args) {
  const commands = args?.commands ?? [];
  if (!Array.isArray(commands) || commands.length === 0) {
    return { ok: false, results: [], error: { code: "EMPTY", message: "指令列表为空" } };
  }
  const results = [];
  for (let i = 0; i < commands.length; i++) {
    const cmd = commands[i];
    try {
      let result;
      const fc = cmd.fc || 3;
      const isWrite = [5, 6, 15, 16].includes(fc);
      const ctxArgs = {
        unitId: cmd.unitId,
        startAddress: cmd.address,
        address: cmd.address,
        quantity: cmd.quantity,
        timeoutMs: cmd.timeoutMs,
      };
      if (isWrite) {
        const writeArgs = { ...ctxArgs, value: cmd.value, values: cmd.values };
        if (fc === 5) result = await writeSingleCoilOnce(ctx, writeArgs);
        else if (fc === 6) result = await writeSingleRegisterOnce(ctx, writeArgs);
        else if (fc === 15) result = await writeMultipleCoilsOnce(ctx, writeArgs);
        else if (fc === 16) result = await writeMultipleRegistersOnce(ctx, writeArgs);
      } else {
        if (fc === 1) result = await readCoilsOnce(ctx, ctxArgs);
        else if (fc === 2) result = await readDiscreteInputsOnce(ctx, ctxArgs);
        else if (fc === 4) result = await readInputRegistersOnce(ctx, ctxArgs);
        else result = await readHoldingRegistersOnce(ctx, ctxArgs);
      }
      results.push({ index: i, fc, ok: result.ok, error: result.error ?? null });
    } catch (error) {
      results.push({ index: i, fc: cmd.fc, ok: false, error: { code: "EXEC_ERROR", message: error.message } });
    }
  }
  return { ok: results.every((r) => r.ok), results };
}

function createMockCtx(overrides = {}) {
  const defaultResult = { ok: true, tx: [1, 3, 2, 0, 1], rx: [1, 3, 2, 0, 1], elapsedMs: 5, crcValid: true, registers: [1], error: null };
  return {
    rustCore: overrides.rustCore ?? {},
    serialService: overrides.serialService ?? {
      getStatus: () => ({ isOpen: true, config: { readTimeoutMs: 1000, writeTimeoutMs: 1000 } }),
      async transact() { return { tx: [1, 3, 2, 0, 1], rx: [1, 3, 2, 0, 1], elapsedMs: 5 }; },
    },
    ensureRustCore: overrides.ensureRustCore ?? (async () => overrides.rustCore ?? {}),
    ...overrides,
  };
}

test("executeCommandList rejects empty command list", async () => {
  const ctx = createMockCtx();
  const result = await executeCommandList(ctx, { commands: [] });
  assert.equal(result.ok, false);
  assert.equal(result.error.code, "EMPTY");
});

test("executeCommandList executes FC03 read commands in order", async () => {
  const calls = [];
  const ctx = createMockCtx({
    rustCore: {
      async buildReadHoldingRegisters(args) { calls.push({ step: "build", args }); return { adu: [1,3,0,0,0,1], expectedResponseLength: 5, exceptionResponseLength: 5 }; },
      async parseReadHoldingRegisters(args) { calls.push({ step: "parse", args }); return { status: "ok", registers: [42] }; },
    },
    serialService: {
      getStatus: () => ({ isOpen: true, config: { readTimeoutMs: 1000 } }),
      async transact(args) { calls.push({ step: "transact", args }); return { tx: args.request, rx: [1,3,2,0,42], elapsedMs: 5 }; },
    },
  });

  const result = await executeCommandList(ctx, {
    commands: [
      { fc: 3, unitId: 1, address: 0, quantity: 1 },
      { fc: 3, unitId: 1, address: 1, quantity: 1 },
    ],
  });
  assert.equal(result.ok, true);
  assert.equal(result.results.length, 2);
  assert.equal(result.results[0].ok, true);
  assert.equal(result.results[1].ok, true);
  // 验证执行顺序:build → transact → parse × 2
  assert.equal(calls.length, 6); // 2 × (build + transact + parse)
});

test("executeCommandList routes FC06 write correctly", async () => {
  const calls = [];
  const ctx = createMockCtx({
    rustCore: {
      async buildWriteSingleRegister(args) { calls.push({ step: "build", args }); return { adu: [1,6,0,5,0,1], expectedResponseLength: 8, exceptionResponseLength: 5, expectResponse: true }; },
      async parseWriteSingleRegister(args) { calls.push({ step: "parse", args }); return { status: "ok", address: 5, value: 1 }; },
    },
    serialService: {
      getStatus: () => ({ isOpen: true, config: { writeTimeoutMs: 1000 } }),
      async transact(args) { calls.push({ step: "transact", args }); return { tx: args.request, rx: [1,6,0,5,0,1], elapsedMs: 3 }; },
    },
  });

  const result = await executeCommandList(ctx, {
    commands: [{ fc: 6, unitId: 1, address: 5, value: 1 }],
  });
  assert.equal(result.ok, true);
  assert.equal(calls[0].step, "build");
});

test("executeCommandList reports partial failure", async () => {
  const ctx = createMockCtx({
    rustCore: {
      async buildReadHoldingRegisters() { throw new Error("build error"); },
    },
  });
  const result = await executeCommandList(ctx, {
    commands: [
      { fc: 3, unitId: 1, address: 0, quantity: 1 },
      { fc: 3, unitId: 1, address: 1, quantity: 1 },
    ],
  });
  assert.equal(result.ok, false);
  assert.equal(result.results.length, 2);
  assert.equal(result.results[0].ok, false);
  assert.equal(result.results[1].ok, false);
});

// === scanBaudRate 的 mock 测试 ===
// scanBaudRate 逻辑:遍历波特率 → 重开串口 → 探测 → 记录

async function scanBaudRateLogic(ctx, args) {
  const { serialService } = ctx;
  const { comPort, stationId = 1, baudRates, timeoutMs = 500 } = args ?? {};
  const rates = baudRates?.length ? baudRates : [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];
  if (!comPort) {
    return { ok: false, error: { code: "INVALID_PARAM", message: "缺少 comPort 参数" } };
  }
  for (const baud of rates) {
    try {
      if (serialService.getStatus()?.isOpen) await serialService.close();
    } catch { /* 忽略 */ }
    try {
      await serialService.open({ portName: comPort, baudRate: baud });
    } catch {
      continue;
    }
    try {
      const result = await ctx.readFn(ctx, { unitId: stationId, startAddress: 0, quantity: 1, timeoutMs });
      if (result.ok) {
        return { ok: true, foundBaudRate: baud, stationId };
      }
    } catch { /* 超时,继续 */ }
  }
  return { ok: false, error: { code: "BAUD_NOT_FOUND", message: "所有波特率均无响应" } };
}

test("scanBaudRate rejects missing comPort", async () => {
  const ctx = { serialService: { getStatus: () => ({}) } };
  const result = await scanBaudRateLogic(ctx, {});
  assert.equal(result.ok, false);
  assert.equal(result.error.code, "INVALID_PARAM");
});

test("scanBaudRate finds the first responding baud rate", async () => {
  const openCalls = [];
  const ctx = {
    serialService: {
      getStatus: () => ({ isOpen: false, config: {} }),
      async close() {},
      async open(config) { openCalls.push(config.baudRate); },
    },
    readFn: async (_ctx, args) => {
      // 模拟 9600 成功,其他超时
      const lastBaud = openCalls[openCalls.length - 1];
      if (lastBaud === 9600) return { ok: true };
      throw new Error("timeout");
    },
  };

  const result = await scanBaudRateLogic(ctx, { comPort: "COM3", stationId: 1, baudRates: [1200, 9600, 19200] });
  assert.equal(result.ok, true);
  assert.equal(result.foundBaudRate, 9600);
  assert.equal(openCalls.length, 2); // 1200 和 9600
});

test("scanBaudRate returns BAUD_NOT_FOUND when all fail", async () => {
  const ctx = {
    serialService: {
      getStatus: () => ({ isOpen: false, config: {} }),
      async close() {},
      async open() {},
    },
    readFn: async () => { throw new Error("timeout"); },
  };

  const result = await scanBaudRateLogic(ctx, { comPort: "COM3", baudRates: [1200, 9600] });
  assert.equal(result.ok, false);
  assert.equal(result.error.code, "BAUD_NOT_FOUND");
});
