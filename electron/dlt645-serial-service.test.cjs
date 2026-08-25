"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { createDlt645SerialService } = require("./dlt645-serial-service.cjs");

const read2007 = [
  0xFE, 0xFE, 0xFE, 0xFE, 0x68, 0x12, 0x90, 0x78, 0x56, 0x34,
  0x12, 0x68, 0x11, 0x04, 0x33, 0x33, 0x34, 0x33, 0x68, 0x16,
];
const response2007 = [
  0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0x91, 0x08,
  0x33, 0x33, 0x34, 0x33, 0xAB, 0x89, 0x67, 0x45, 0xCC, 0x16,
];
const read1997 = [
  0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0x01, 0x02,
  0x43, 0xC3, 0x8F, 0x16,
];

function openStatus(overrides = {}) {
  return {
    isOpen: true,
    config: {
      portName: "COM7",
      baudRate: 2400,
      dataBits: 8,
      parity: "even",
      stopBits: "1",
      ...overrides,
    },
  };
}

test("DL/T 645 shared COM service executes a 2007 read-only meter transaction", async () => {
  const commands = [];
  const service = createDlt645SerialService({
    getSerialStatus: () => openStatus(),
    request: async (command, payload) => {
      commands.push({ command, payload });
      if (command === "dlt645_build_read_request") return { frame: read2007, frameHex: "FE FE" };
      assert.equal(command, "dlt645_parse_read_response");
      assert.deepEqual(payload.frame, response2007);
      return {
        response: {
          version: "2007",
          address: "123456789012",
          dataId: "00010000",
          payload: [0x78, 0x56, 0x34, 0x12],
          knownValue: { kind: "energy", value: { decimal: "123456.78" }, unit: "kWh" },
          exception: null,
          hasFollowFrame: false,
          readOnly: true,
        },
      };
    },
    transact: async (args) => {
      assert.equal(args.framing, "dlt645");
      assert.equal(args.timeoutMs, 1500);
      return { tx: args.request, rx: response2007, elapsedMs: 6 };
    },
  });

  const result = await service.read({
    version: "2007",
    address: "123456789012",
    dataId: "00010000",
    model: "DTSU-TEST",
    serialNumber: "METER-001",
  });
  assert.equal(result.ok, true);
  assert.equal(result.readOnly, true);
  assert.equal(result.response.knownValue.value.decimal, "123456.78");
  assert.equal(result.deviceRecord.model, "DTSU-TEST");
  assert.deepEqual(commands.map(({ command }) => command), [
    "dlt645_build_read_request",
    "dlt645_parse_read_response",
  ]);
  assert.equal("write" in service, false);
  assert.equal("control" in service, false);
});

test("DL/T 645 shared COM service keeps the 1997 request boundary separate", async () => {
  const service = createDlt645SerialService({
    getSerialStatus: () => openStatus(),
    request: async (command, payload) => {
      if (command === "dlt645_build_read_request") {
        assert.deepEqual(payload, {
          version: "1997",
          address: "123456789012",
          dataId: "9010",
          preambleCount: 0,
        });
        return { frame: read1997 };
      }
      return {
        response: {
          version: "1997",
          address: "123456789012",
          dataId: "9010",
          payload: [0, 0, 1, 0],
          hasFollowFrame: false,
          exception: null,
        },
      };
    },
    transact: async ({ request }) => ({ tx: request, rx: [0x68], elapsedMs: 2 }),
  });
  const result = await service.read({
    version: "1997",
    address: "123456789012",
    dataId: "9010",
    preambleCount: 0,
  });
  assert.equal(result.protocol, "dlt645-1997-serial");
  assert.equal(result.dataId, "9010");
});

test("DL/T 645 shared COM service retries transport timeout", async () => {
  let attempts = 0;
  const service = createDlt645SerialService({
    getSerialStatus: () => openStatus({ baudRate: 9600, parity: "none" }),
    request: async (command) => command === "dlt645_build_read_request"
      ? { frame: read2007 }
      : { response: { hasFollowFrame: false, exception: null } },
    transact: async ({ request }) => {
      attempts += 1;
      if (attempts === 1) {
        const error = new Error("timeout");
        error.code = "SERIAL_RESPONSE_TIMEOUT";
        throw error;
      }
      return { tx: request, rx: response2007, elapsedMs: 9 };
    },
  });
  const result = await service.read({ address: "123456789012", dataId: "00010000", retries: 1 });
  assert.equal(result.attempt, 2);
  assert.equal(attempts, 2);
  assert.equal(result.serialWarnings.length, 2);
});

test("DL/T 645 shared COM service fails closed for closed port, broadcast, and follow frames", async () => {
  let requests = 0;
  const closed = createDlt645SerialService({
    getSerialStatus: () => ({ isOpen: false }),
    request: async () => { requests += 1; return {}; },
    transact: async () => ({ rx: [] }),
  });
  await assert.rejects(() => closed.read({ address: "123456789012" }), (error) => error.code === "SERIAL_NOT_OPEN");
  await assert.rejects(() => closed.read({ address: "999999999999" }), (error) => error.code === "DLT645_BROADCAST_READ_FORBIDDEN");
  assert.equal(requests, 0);

  const follow = createDlt645SerialService({
    getSerialStatus: () => openStatus(),
    request: async (command) => command === "dlt645_build_read_request"
      ? { frame: read2007 }
      : { response: { hasFollowFrame: true, control: 0xB1 } },
    transact: async ({ request }) => ({ tx: request, rx: response2007 }),
  });
  await assert.rejects(
    () => follow.read({ address: "123456789012", retries: 3 }),
    (error) => error.code === "DLT645_FOLLOW_UNSUPPORTED",
  );
});
