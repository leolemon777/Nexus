const test = require("node:test");
const assert = require("node:assert/strict");
const { createPpiSerialService } = require("./ppi-serial-service.cjs");

function makeHarness({ firstRx = [0xE5], secondRx = [0x68, 0x03, 0x03, 0x68, 0x00, 0x02, 0x08, 0x0A, 0x16] } = {}) {
  const requests = [];
  const transactions = [];
  const service = createPpiSerialService({
    getSerialStatus: () => ({ isOpen: true, config: { portName: "COM7", baudRate: 9600, parity: "none" } }),
    request: async (command, payload) => {
      requests.push({ command, payload });
      if (command === "ppi_build_read") return { frame: [0x68, 0x0A, 0x0A, 0x68, 0x02, 0x00, 0x6C, 0x32, 0x01, 0x16, 0x16] };
      if (command === "ppi_build_sa_confirm") return { frame: [0x10, 0x02, 0x00, 0x5C, 0x5E, 0x16] };
      if (command === "ppi_parse_read_response") return { destination: 0, source: 2, functionCode: 0x08, items: [{ returnCode: 0xFF, data: [0x12, 0x34] }] };
      throw new Error(`unexpected command ${command}`);
    },
    transact: async (args) => {
      transactions.push(args);
      const rx = transactions.length === 1 ? firstRx : secondRx;
      return { tx: args.request, rx, elapsedMs: 3 };
    },
  });
  return { service, requests, transactions };
}

test("executes the PPI native serial read double handshake as read-only", async () => {
  const { service, requests, transactions } = makeHarness();
  const result = await service.read({ station: 2, master: 0, address: "VB100", count: 2, timeoutMs: 200 });

  assert.equal(result.ok, true);
  assert.equal(result.protocol, "siemens-ppi-serial");
  assert.deepEqual(result.serialWarnings, ["当前校验不是常见的偶校验(8E1)"]);
  assert.deepEqual(result.items[0].data, [0x12, 0x34]);
  assert.deepEqual(requests.map((entry) => entry.command), [
    "ppi_build_read",
    "ppi_build_sa_confirm",
    "ppi_parse_read_response",
  ]);
  assert.equal(transactions.length, 2);
  assert.equal(transactions[0].framing, "ppi");
  assert.deepEqual(result.first.rx, [0xE5]);
  assert.deepEqual(transactions[1].request, [0x10, 0x02, 0x00, 0x5C, 0x5E, 0x16]);
});

test("fails closed when the shared COM port is not open", async () => {
  const service = createPpiSerialService({
    getSerialStatus: () => ({ isOpen: false }),
    request: async () => ({ frame: [] }),
    transact: async () => ({ rx: [] }),
  });
  await assert.rejects(service.read({ address: "VB100" }), (error) => error.code === "SERIAL_NOT_OPEN");
});

test("rejects a non-E5 first response and never sends the confirmation", async () => {
  const { service, transactions } = makeHarness({ firstRx: [0x15, 0x01] });
  await assert.rejects(
    service.read({ address: "VB100", retries: 0 }),
    (error) => error.code === "PPI_RETRY_EXHAUSTED" && error.details.last.code === "PPI_NAK" && error.details.last.details.rx[0] === 0x15,
  );
  assert.equal(transactions.length, 1);
});

test("retries a PPI NAK and reports the successful attempt count", async () => {
  const requests = [];
  const transactions = [];
  let firstAttempt = true;
  const service = createPpiSerialService({
    getSerialStatus: () => ({ isOpen: true }),
    request: async (command) => {
      requests.push(command);
      if (command === "ppi_build_read") return { frame: [0x68, 0x0A, 0x0A, 0x68, 0x02, 0x00, 0x6C, 0x32, 0x01, 0x16, 0x16] };
      if (command === "ppi_build_sa_confirm") return { frame: [0x10, 0x02, 0x00, 0x5C, 0x5E, 0x16] };
      return { destination: 0, source: 2, functionCode: 0x08, items: [{ returnCode: 0xFF, data: [0x12] }] };
    },
    transact: async (args) => {
      transactions.push(args);
      if (firstAttempt) {
        firstAttempt = false;
        return { tx: args.request, rx: [0x15], elapsedMs: 1 };
      }
      return transactions.length === 2
        ? { tx: args.request, rx: [0xE5], elapsedMs: 1 }
        : { tx: args.request, rx: [0x68, 0x03, 0x03, 0x68, 0x00, 0x02, 0x08, 0x0A, 0x16], elapsedMs: 1 };
    },
  });
  const result = await service.read({ address: "VB100", retries: 1 });
  assert.equal(result.attempts, 2);
  assert.deepEqual(result.items[0].data, [0x12]);
  assert.equal(transactions.length, 3);
});

test("validates station, count and address before touching the serial transport", async () => {
  const { service, transactions } = makeHarness();
  await assert.rejects(service.read({ station: 127, address: "VB100" }), (error) => error.code === "PPI_PARAM_INVALID");
  await assert.rejects(service.read({ address: "", count: 1 }), (error) => error.code === "PPI_PARAM_INVALID");
  assert.equal(transactions.length, 0);
});
