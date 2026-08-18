const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const { SerialService, comparePortNames, validateConfig } = require("./serial-service.cjs");

function validConfig(overrides = {}) {
  return {
    portName: "COM3",
    baudRate: 9600,
    dataBits: 8,
    parity: "none",
    stopBits: "1",
    flowControl: "none",
    readTimeoutMs: 1000,
    writeTimeoutMs: 1000,
    dtrMode: "preserve",
    rtsMode: "preserve",
    ...overrides,
  };
}

test("accepts the safe default Windows serial configuration", () => {
  assert.deepEqual(validateConfig(validConfig()), validConfig());
});

test("sorts COM ports by their numeric suffix", () => {
  const ports = ["COM10", "COM2", "COM1"];
  assert.deepEqual(ports.sort(comparePortNames), ["COM1", "COM2", "COM10"]);
});

test("rejects unsafe or unsupported values", () => {
  assert.throws(() => validateConfig(validConfig({ portName: "COM0" })), /有效的 Windows COM/);
  assert.throws(() => validateConfig(validConfig({ baudRate: 0 })), /波特率/);
  assert.throws(() => validateConfig(validConfig({ parity: "mark" })), /只支持/);
  assert.throws(() => validateConfig(validConfig({ stopBits: "1.5" })), /只支持/);
});

test("does not allow manual RTS while hardware flow control owns the line", () => {
  assert.throws(
    () => validateConfig(validConfig({ flowControl: "rts-cts", rtsMode: "high" })),
    /RTS 必须由驱动管理/,
  );
});

class FakeSerialPort extends EventEmitter {
  static instances = [];
  static responseChunks = [];
  static responseDelayMs = 0;
  static writeDelayMs = 0;
  static flushNeverCompletes = false;

  static async list() {
    return [
      { path: "COM10", manufacturer: "Beta" },
      { path: "COM2", manufacturer: "Alpha", serialNumber: "42" },
    ];
  }

  constructor(options) {
    super();
    this.options = options;
    this.isOpen = false;
    this.lineState = null;
    this.writes = [];
    this.flushCount = 0;
    this.responseChunks = FakeSerialPort.responseChunks.map((chunk) => Buffer.from(chunk));
    this.responseDelayMs = FakeSerialPort.responseDelayMs;
    this.writeDelayMs = FakeSerialPort.writeDelayMs;
    this.flushNeverCompletes = FakeSerialPort.flushNeverCompletes;
    FakeSerialPort.instances.push(this);
  }

  open(done) {
    this.isOpen = true;
    done(null);
  }

  set(lineState, done) {
    this.lineState = lineState;
    done(null);
  }

  close(done) {
    this.isOpen = false;
    done(null);
  }

  flush(done) {
    this.flushCount += 1;
    if (this.flushNeverCompletes) return;
    done(null);
  }

  write(bytes, done) {
    this.writes.push(Buffer.from(bytes));
    setTimeout(() => done(null), this.writeDelayMs);
    this.responseChunks.forEach((chunk, index) => {
      setTimeout(() => this.emit("data", chunk), this.responseDelayMs * (index + 1));
    });
  }

  drain(done) {
    done(null);
  }
}

test("lists ports and completes an injected open/set/close lifecycle", async () => {
  FakeSerialPort.instances.length = 0;
  FakeSerialPort.responseChunks = [];
  FakeSerialPort.responseDelayMs = 0;
  FakeSerialPort.writeDelayMs = 0;
  FakeSerialPort.flushNeverCompletes = false;
  const service = new SerialService({ SerialPortImpl: FakeSerialPort });

  const ports = await service.listPorts();
  assert.deepEqual(ports.map((port) => port.name), ["COM2", "COM10"]);
  assert.equal(ports[0].displayName, "COM2 · Alpha · 42");

  const config = validConfig({ dtrMode: "high", rtsMode: "low" });
  assert.deepEqual(await service.open(config), { isOpen: true, config });
  const port = FakeSerialPort.instances[0];
  assert.deepEqual(port.options, {
    path: "COM3",
    baudRate: 9600,
    dataBits: 8,
    parity: "none",
    stopBits: 1,
    rtscts: false,
    xon: false,
    xoff: false,
    autoOpen: false,
  });
  assert.deepEqual(port.lineState, { dtr: true, rts: false });

  assert.deepEqual(await service.close(), { isOpen: false, config: null });
  assert.equal(port.isOpen, false);
});

test("collects a fragmented FC03 response inside one exclusive transaction", async () => {
  FakeSerialPort.instances.length = 0;
  FakeSerialPort.responseChunks = [
    [1, 3],
    [4, 0, 1],
    [0, 2, 42, 50],
  ];
  FakeSerialPort.responseDelayMs = 2;
  const service = new SerialService({ SerialPortImpl: FakeSerialPort });
  await service.open(validConfig());

  const request = [1, 3, 0, 0, 0, 2, 196, 11];
  const transaction = await service.transact({
    request,
    expectedResponseLength: 9,
    exceptionResponseLength: 5,
    timeoutMs: 100,
  });

  assert.deepEqual(transaction.tx, request);
  assert.deepEqual(transaction.rx, [1, 3, 4, 0, 1, 0, 2, 42, 50]);
  assert.ok(transaction.elapsedMs >= 0);
  const port = FakeSerialPort.instances[0];
  assert.equal(port.flushCount, 1);
  assert.deepEqual([...port.writes[0]], request);
  assert.equal(port.listenerCount("data"), 0);
});

test("finishes a five-byte exception response without waiting for the normal length", async () => {
  FakeSerialPort.instances.length = 0;
  FakeSerialPort.responseChunks = [[1, 0x83, 2, 0xC0, 0xF1]];
  FakeSerialPort.responseDelayMs = 1;
  const service = new SerialService({ SerialPortImpl: FakeSerialPort });
  await service.open(validConfig());

  const transaction = await service.transact({
    request: [1, 3, 0, 0, 0, 125, 132, 42],
    expectedResponseLength: 255,
    exceptionResponseLength: 5,
    timeoutMs: 100,
  });
  assert.deepEqual(transaction.rx, [1, 0x83, 2, 0xC0, 0xF1]);
});

test("rejects concurrent transactions and cleans listeners after timeout", async () => {
  FakeSerialPort.instances.length = 0;
  FakeSerialPort.responseChunks = [[1, 3, 2, 0, 1, 121, 132]];
  FakeSerialPort.responseDelayMs = 20;
  const service = new SerialService({ SerialPortImpl: FakeSerialPort });
  await service.open(validConfig());
  const args = {
    request: [1, 3, 0, 0, 0, 1, 132, 10],
    expectedResponseLength: 7,
    exceptionResponseLength: 5,
    timeoutMs: 100,
  };

  const first = service.transact(args);
  await assert.rejects(service.transact(args), (error) => error.code === "SERIAL_BUSY");
  await first;

  FakeSerialPort.instances[0].responseChunks = [];
  await assert.rejects(
    service.transact({ ...args, timeoutMs: 10 }),
    (error) => error.code === "SERIAL_RESPONSE_TIMEOUT" && error.details.rx.length === 0,
  );
  assert.equal(FakeSerialPort.instances[0].listenerCount("data"), 0);
  assert.equal(service.transactionActive, false);
});

test("handles an early collector rejection while the write callback is still pending", async () => {
  FakeSerialPort.instances.length = 0;
  FakeSerialPort.responseChunks = [[1, 3, 2, 0, 1, 121, 132, 0]];
  FakeSerialPort.responseDelayMs = 0;
  FakeSerialPort.writeDelayMs = 20;
  const service = new SerialService({ SerialPortImpl: FakeSerialPort });
  await service.open(validConfig());

  await assert.rejects(
    service.transact({
      request: [1, 3, 0, 0, 0, 1, 132, 10],
      expectedResponseLength: 7,
      exceptionResponseLength: 5,
      timeoutMs: 100,
    }),
    (error) => error.code === "SERIAL_FRAME_TOO_LONG",
  );
  assert.equal(FakeSerialPort.instances[0].listenerCount("data"), 0);
  assert.equal(service.transactionActive, false);
  FakeSerialPort.writeDelayMs = 0;
});

test("bounds a serial driver that never completes flush", async () => {
  FakeSerialPort.instances.length = 0;
  FakeSerialPort.responseChunks = [];
  FakeSerialPort.responseDelayMs = 0;
  FakeSerialPort.writeDelayMs = 0;
  FakeSerialPort.flushNeverCompletes = true;
  const service = new SerialService({ SerialPortImpl: FakeSerialPort });
  await service.open(validConfig({ writeTimeoutMs: 10 }));

  await assert.rejects(
    service.transact({
      request: [1, 3, 0, 0, 0, 1, 132, 10],
      expectedResponseLength: 7,
      exceptionResponseLength: 5,
      timeoutMs: 100,
    }),
    (error) => error.code === "SERIAL_FLUSH_TIMEOUT",
  );
  assert.equal(service.transactionActive, false);
  FakeSerialPort.flushNeverCompletes = false;
});
