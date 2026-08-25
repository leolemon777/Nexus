const test = require("node:test");
const assert = require("node:assert/strict");
const { createPanasonicSerialService } = require("./panasonic-serial-service.cjs");

const requestFrame = Array.from(Buffer.from("%01#RDD001000010055\r", "ascii"));
const responseFrame = Array.from(Buffer.from("%01$RD640014\r", "ascii"));

function openStatus() {
  return {
    isOpen: true,
    config: { portName: "COM7", baudRate: 9600, dataBits: 8, parity: "odd", stopBits: "1" },
  };
}

test("Panasonic shared COM service executes a read-only RD transaction", async () => {
  const calls = [];
  const service = createPanasonicSerialService({
    getSerialStatus: () => openStatus(),
    request: async (command, payload) => {
      calls.push({ command, payload });
      if (command === "panasonic_build_read") return { frame: requestFrame, text: "%01#RDD001000010055\r" };
      assert.equal(command, "panasonic_parse_response");
      assert.deepEqual(payload.response, responseFrame);
      return { command: "RD", payload: "6400" };
    },
    transact: async (args) => {
      assert.equal(args.framing, "mewtocol");
      assert.equal(args.timeoutMs, 1500);
      return { tx: args.request, rx: responseFrame, elapsedMs: 4 };
    },
  });

  const result = await service.read({ station: 1, address: "DT100", wordCount: 1, model: "FP0R-C32", serialNumber: "FP-001" });
  assert.equal(result.ok, true);
  assert.equal(result.protocol, "panasonic-mewtocol-com-serial");
  assert.equal(result.readOnly, true);
  assert.equal(result.payload, "6400");
  assert.equal(result.deviceRecord.model, "FP0R-C32");
  assert.equal(result.deviceRecord.serialNumber, "FP-001");
  assert.deepEqual(calls.map((call) => call.command), ["panasonic_build_read", "panasonic_parse_response"]);
});

test("Panasonic shared COM service retries a timeout and supports RCS contact reads", async () => {
  let attempts = 0;
  const service = createPanasonicSerialService({
    getSerialStatus: () => openStatus(),
    request: async (command) => {
      if (command === "panasonic_build_read_contact") return { frame: Array.from(Buffer.from("%01#RCSX001F6A\r", "ascii")), text: "%01#RCSX001F6A\r" };
      return { command: "RCS", payload: "1" };
    },
    transact: async ({ request }) => {
      attempts += 1;
      if (attempts === 1) {
        const error = new Error("timeout");
        error.code = "SERIAL_RESPONSE_TIMEOUT";
        throw error;
      }
      return { tx: request, rx: Array.from(Buffer.from("%01$RCS1A\r", "ascii")), elapsedMs: 8 };
    },
  });

  const result = await service.readContact({ station: 1, address: "X1F", retries: 1 });
  assert.equal(attempts, 2);
  assert.equal(result.contact, true);
  assert.equal(result.payload, "1");
  assert.equal(result.attempt, 2);
});

test("Panasonic shared COM service fails closed when the shared port is not open", async () => {
  let requests = 0;
  const service = createPanasonicSerialService({
    getSerialStatus: () => ({ isOpen: false }),
    request: async () => { requests += 1; return {}; },
    transact: async () => ({ rx: [] }),
  });
  await assert.rejects(() => service.read({}), (error) => error.code === "SERIAL_NOT_OPEN");
  assert.equal(requests, 0);
});
