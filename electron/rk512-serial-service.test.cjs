const test = require("node:test");
const assert = require("node:assert/strict");
const { createRk512SerialService, parseAddress } = require("./rk512-serial-service.cjs");

function makeService(overrides = {}) {
  const calls = [];
  const transactions = [];
  const service = createRk512SerialService({
    getSerialStatus: () => ({ isOpen: true }),
    request: async (command, payload) => {
      calls.push({ command, payload });
      if (command === "rk512_build_read") return { frame: [0x02, 0x01, 0, 1, 0, 1, 0, 0, 0, 0, 0x10, 0x03, 0x13] };
      return { error: 0, func: 1, count: 2, db: 1, offset: 0, data: [0x12, 0x34, 0x56, 0x78] };
    },
    transact: async (args) => {
      transactions.push(args);
      if (transactions.length === 1) return { tx: args.request, rx: [0x10], elapsedMs: 1 };
      if (transactions.length === 2) return { tx: args.request, rx: [0x02, 0x00, 0x10, 0x03, 0x13], elapsedMs: 2 };
      return { tx: args.request, rx: [0x10], elapsedMs: 1 };
    },
    ...overrides,
  });
  return { service, calls, transactions };
}

test("executes 3964R handshake, RK512 read, and link release", async () => {
  const { service, calls, transactions } = makeService();
  const result = await service.read({ address: "DB1.W0", count: 2 });
  assert.equal(result.ok, true);
  assert.equal(result.protocol, "siemens-rk512-serial");
  assert.deepEqual(result.data, [0x12, 0x34, 0x56, 0x78]);
  assert.deepEqual(calls.map((call) => call.command), ["rk512_build_read", "rk512_parse_response"]);
  assert.deepEqual(transactions.map((tx) => tx.request), [[0x02], calls.length ? transactions[1].request : [], [0x10]]);
  assert.equal(transactions[0].framing, "rk512");
});

test("parses point-to-point address forms and validates inputs", () => {
  assert.deepEqual(parseAddress("DB3.W20"), { area: "DB", db: 3, offset: 20 });
  assert.deepEqual(parseAddress("M100"), { area: "M", db: 0, offset: 100 });
  assert.throws(() => parseAddress("DB1.X0"), /地址格式/);
});

test("fails closed when serial is unavailable or response metadata mismatches", async () => {
  const { service } = makeService({ getSerialStatus: () => ({ isOpen: false }) });
  await assert.rejects(service.read({ address: "DB1.W0" }), (error) => error.code === "SERIAL_NOT_OPEN");
  const { service: mismatch } = makeService({
    request: async (command) => command === "rk512_build_read"
      ? { frame: [0x02, 0x01, 0, 1, 0, 1, 0, 0, 0, 0, 0x10, 0x03, 0x13] }
      : { error: 0, func: 3, count: 1, db: 9, offset: 10, data: [] },
  });
  await assert.rejects(mismatch.read({ address: "DB1.W0" }), (error) => error.code === "RK512_RESPONSE_MISMATCH");
  const lengthMismatch = makeService({
    request: async (command) => command === "rk512_build_read"
      ? { frame: [0x02, 0x01, 0, 1, 0, 1, 0, 0, 0, 0, 0x10, 0x03, 0x13] }
      : { error: 0, func: 1, count: 2, db: 1, offset: 0, data: [0x12, 0x34] },
  });
  await assert.rejects(lengthMismatch.service.read({ address: "DB1.W0", count: 2 }), (error) => error.code === "RK512_LENGTH_MISMATCH");
});
