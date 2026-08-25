const test = require("node:test");
const assert = require("node:assert/strict");
const { createUssSerialService } = require("./uss-serial-service.cjs");

function harness(overrides = {}) {
  const calls = [];
  const service = createUssSerialService({
    getSerialStatus: () => ({ isOpen: true }),
    request: async (command, payload) => {
      calls.push({ command, payload });
      if (command === "uss_build_request") return { frame: [0x02, 0x09, 0x01, 0x12, 0xBC, 0, 0, 0, 0, 0, 0, 0] };
      return { station: 1, pkeAkCode: 0x10, pkeAk: "0x10", pkeAkMessage: "应答:参数值(16位)", pzd: [0, 6, 0, 0] };
    },
    // 真实 BCC = XOR(STX..PZD) = 0x2B；服务会把这帧交给 Rust 校验器。
    transact: async (args) => ({ tx: args.request, rx: [0x02, 0x09, 0x81, 0x1B, 0xBC, 0, 0, 0, 6, 0, 0, 0x2B], elapsedMs: 4 }),
    ...overrides,
  });
  return { service, calls };
}

test("performs a USS parameter read without exposing write/control operations", async () => {
  const { service, calls } = harness();
  const result = await service.read({ station: 1, param: 700, pzdBytes: 4 });
  assert.equal(result.ok, true);
  assert.equal(result.protocol, "siemens-uss-serial");
  assert.deepEqual(result.pzd, [0, 6, 0, 0]);
  assert.deepEqual(calls.map((call) => call.command), ["uss_build_request", "uss_parse_response"]);
  assert.equal(calls[0].payload.param, 700);
  assert.deepEqual(calls[0].payload.pzd, [0, 0, 0, 0]);
});

test("fails closed when COM is not open or the station is invalid", async () => {
  const { service } = harness({ getSerialStatus: () => ({ isOpen: false }) });
  await assert.rejects(service.read(), (error) => error.code === "SERIAL_NOT_OPEN");
  const { service: invalid } = harness();
  await assert.rejects(invalid.read({ station: 31 }), (error) => error.code === "USS_PARAM_INVALID");
  await assert.rejects(invalid.read({ param: 4096 }), (error) => error.code === "USS_PARAM_INVALID");
});

test("rejects a response from another USS station or a device error AK", async () => {
  const { service } = harness({
    request: async (command) => command === "uss_build_request"
      ? { frame: [0x02, 0x09, 0x01, 0x12, 0xBC, 0, 0, 0, 0, 0, 0, 0] }
      : { station: 2, pkeAkCode: 0x04, pkeAk: "0x4", pkeAkMessage: "任务拒绝", pzd: [] },
  });
  await assert.rejects(service.read({ station: 1 }), (error) => error.code === "USS_STATION_CONFLICT");
  const { service: deviceError } = harness({
    request: async (command) => command === "uss_build_request"
      ? { frame: [0x02, 0x09, 0x01, 0x12, 0xBC, 0, 0, 0, 0, 0, 0, 0] }
      : { station: 1, pkeAkCode: 0x04, pkeAk: "0x4", pkeAkMessage: "任务拒绝", pzd: [] },
  });
  await assert.rejects(deviceError.read({ station: 1 }), (error) => error.code === "USS_DEVICE_ERROR");
});
