const test = require("node:test");
const assert = require("node:assert/strict");
const { createDeltaModbusService } = require("./delta-modbus-service.cjs");

function profileRequest(command, payload) {
  assert.equal(command, "delta_parse_address");
  const series = String(payload.series).toLowerCase();
  const address = String(payload.address).trim().toUpperCase();
  const match = /^([A-Z]+)([0-9]+)(?:\.([0-9]+))?$/.exec(address);
  if (!match) return { ok: false, error: { code: "DELTA_PARAM_INVALID", message: "bad address" } };
  const prefix = match[1];
  const word = Number(match[2]);
  const bit = match[3] == null ? null : Number(match[3]);
  const isDvpIo = series === "dvp" && ["X", "Y"].includes(prefix);
  const isBit = bit != null || ["S", "X", "Y", "T", "C", "M", "SM", "HC"].includes(prefix);
  let modbusAddress;
  let readFunction;
  let writeFunction = null;
  let area = prefix;
  if (series === "dvp") {
    const logical = isDvpIo ? Number.parseInt(String(word), 8) : word;
    if (prefix === "D") { modbusAddress = logical < 4096 ? 0x1000 + logical : 0x9000 + logical - 4096; readFunction = 3; writeFunction = 6; }
    else if (prefix === "M") { modbusAddress = logical < 1536 ? 0x0800 + logical : 0xB000 + logical - 1536; readFunction = 1; writeFunction = 5; }
    else if (prefix === "X") { modbusAddress = 0x0400 + logical; readFunction = 2; }
    else if (prefix === "Y") { modbusAddress = 0x0500 + logical; readFunction = 1; writeFunction = 5; }
    else { modbusAddress = word; readFunction = 1; writeFunction = 5; }
  } else {
    const logicalBit = bit == null ? word : word * 16 + bit;
    if (prefix === "D") { modbusAddress = word; readFunction = 3; writeFunction = 6; }
    else if (prefix === "X") { modbusAddress = bit == null ? 0x8000 + word : 0x6000 + logicalBit; readFunction = bit == null ? 4 : 2; }
    else if (prefix === "Y") { modbusAddress = 0xA000 + logicalBit; readFunction = bit == null ? 3 : 1; writeFunction = bit == null ? 6 : 5; }
    else { modbusAddress = word; readFunction = 1; writeFunction = 5; }
  }
  return { ok: true, series: series.toUpperCase(), canonical: address, area, modbusAddress, readFunction, writeFunction, isBit, readOnly: writeFunction == null };
}

test("Delta planner splits DVP D and M discontinuities", async () => {
  const service = createDeltaModbusService({ request: profileRequest });
  const d = await service.planRange({ series: "dvp", address: "D4095", quantity: 2 });
  assert.deepEqual(d.segments.map(({ logicalStart, modbusStart, quantity }) => ({ logicalStart, modbusStart, quantity })), [
    { logicalStart: "D4095", modbusStart: 0x1fff, quantity: 1 },
    { logicalStart: "D4096", modbusStart: 0x9000, quantity: 1 },
  ]);
  const m = await service.planRange({ series: "dvp", address: "M1535", quantity: 2 });
  assert.deepEqual(m.segments.map(({ logicalStart, modbusStart, quantity }) => ({ logicalStart, modbusStart, quantity })), [
    { logicalStart: "M1535", modbusStart: 0x0dff, quantity: 1 },
    { logicalStart: "M1536", modbusStart: 0xb000, quantity: 1 },
  ]);
});

test("Delta planner advances DVP X octal addresses", async () => {
  const service = createDeltaModbusService({ request: profileRequest });
  const plan = await service.planRange({ series: "dvp", address: "X17", quantity: 2 });
  assert.equal(plan.segments.length, 1);
  assert.equal(plan.segments[0].logicalStart, "X17");
  assert.equal(plan.segments[0].modbusStart, 0x040f);
  assert.equal(plan.segments[0].quantity, 2);
});

test("Delta read is readonly, concatenates segment results, and records model/firmware", async () => {
  const calls = [];
  const service = createDeltaModbusService({
    request: profileRequest,
    readers: {
      readHoldingRegisters: async (args) => {
        calls.push({ kind: "holding", args });
        return { ok: true, registers: Array.from({ length: args.quantity }, (_, index) => args.startAddress + index), tx: [1], rx: [2], elapsedMs: 3 };
      },
    },
  });
  const result = await service.read({ series: "dvp", address: "D4095", quantity: 2, unitId: 7, transport: "rtu", model: "DVP-ES2", firmware: "2.10" });
  assert.equal(result.ok, true);
  assert.equal(result.readOnly, true);
  assert.deepEqual(result.values, [0x1fff, 0x9000]);
  assert.equal(result.segments.length, 2);
  assert.equal(result.deviceRecord.model, "DVP-ES2");
  assert.equal(result.deviceRecord.firmware, "2.10");
  assert.deepEqual(calls.map(({ kind, args }) => ({ kind, unitId: args.unitId, startAddress: args.startAddress, quantity: args.quantity })), [
    { kind: "holding", unitId: 7, startAddress: 0x1fff, quantity: 1 },
    { kind: "holding", unitId: 7, startAddress: 0x9000, quantity: 1 },
  ]);
});

test("Delta read returns completed segments on a later read failure", async () => {
  let count = 0;
  const service = createDeltaModbusService({
    request: profileRequest,
    readers: {
      readHoldingRegisters: async (args) => {
        count += 1;
        if (count === 2) return { ok: false, registers: [], error: { code: "TIMEOUT", message: "simulated timeout" } };
        return { ok: true, registers: [args.startAddress], tx: [], rx: [] };
      },
    },
  });
  const result = await service.read({ series: "dvp", address: "D4095", quantity: 2 });
  assert.equal(result.ok, false);
  assert.equal(result.readOnly, true);
  assert.equal(result.completedSegments.length, 1);
  assert.equal(result.failedSegment.logicalStart, "D4096");
  assert.equal(result.error.code, "TIMEOUT");
});
