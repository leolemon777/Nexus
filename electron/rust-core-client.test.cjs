const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const { PassThrough } = require("node:stream");
const {
  COMMANDS,
  MAX_PAYLOAD_DEPTH,
  MAX_PAYLOAD_NODES,
  PROTOCOL_VERSION,
  RustCoreClient,
  RustCoreRemoteError,
} = require("./rust-core-client.cjs");

class FakeChild extends EventEmitter {
  constructor() {
    super();
    this.stdin = new PassThrough();
    this.stdout = new PassThrough();
    this.stderr = new PassThrough();
    this.killed = false;
    this.requests = [];
    this.requestWaiters = [];
    this.inputBuffer = "";
    this.stdin.on("data", (chunk) => this._captureInput(chunk));
  }

  _captureInput(chunk) {
    this.inputBuffer += chunk.toString("utf8");
    while (true) {
      const newlineIndex = this.inputBuffer.indexOf("\n");
      if (newlineIndex < 0) return;
      const line = this.inputBuffer.slice(0, newlineIndex);
      this.inputBuffer = this.inputBuffer.slice(newlineIndex + 1);
      const request = JSON.parse(line);
      const waiter = this.requestWaiters.shift();
      if (waiter) waiter(request);
      else this.requests.push(request);
    }
  }

  nextRequest() {
    if (this.requests.length) return Promise.resolve(this.requests.shift());
    return new Promise((resolve) => this.requestWaiters.push(resolve));
  }

  respond(request, { result = null, error = null, ok = error == null } = {}, chunks = 1) {
    const frame = `${JSON.stringify({
      protocolVersion: PROTOCOL_VERSION,
      requestId: request.requestId,
      ok,
      result,
      error,
    })}\n`;
    if (chunks <= 1) {
      this.stdout.write(frame);
      return;
    }
    const splitAt = Math.max(1, Math.floor(frame.length / chunks));
    for (let offset = 0; offset < frame.length; offset += splitAt) {
      this.stdout.write(frame.slice(offset, offset + splitAt));
    }
  }

  kill(signal = "SIGTERM") {
    this.killed = true;
    this.killSignal = signal;
    return true;
  }

  close(code = 0, signal = null) {
    this.emit("close", code, signal);
  }
}

function createHarness(options = {}) {
  const calls = [];
  const children = [];
  const spawnImpl = (binaryPath, args, spawnOptions) => {
    const child = new FakeChild();
    calls.push({ binaryPath, args, spawnOptions });
    children.push(child);
    return child;
  };
  const logs = [];
  const logger = {
    error: (message) => logs.push({ level: "error", message }),
    warn: (message) => logs.push({ level: "warn", message }),
  };
  const client = new RustCoreClient({
    binaryPath: "C:\\fake\\nexus-rust-core.exe",
    spawnImpl,
    requestTimeoutMs: 100,
    shutdownGraceMs: 50,
    logger,
    ...options,
  });
  return { calls, children, client, logs };
}

async function startClient(harness, helloResult = { protocolVersion: PROTOCOL_VERSION }) {
  const startPromise = harness.client.start();
  const child = harness.children[0];
  const hello = await child.nextRequest();
  child.respond(hello, { result: helloResult }, 3);
  await startPromise;
  return child;
}

test("spawns the injected binary and completes a fragmented hello handshake", async () => {
  const harness = createHarness();
  const child = await startClient(harness, { protocolVersion: 1, core: "nexus-rust-core" });

  assert.equal(harness.calls.length, 1);
  assert.deepEqual(harness.calls[0], {
    binaryPath: "C:\\fake\\nexus-rust-core.exe",
    args: [],
    spawnOptions: {
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    },
  });
  assert.equal(harness.client.state, "ready");
  assert.equal(harness.client.pending.size, 0);

  child.stderr.write("password=hunter2 token=secret-token\n");
  assert.deepEqual(harness.logs, [{
    level: "error",
    message: "[rust-core] password=[REDACTED] token=[REDACTED]",
  }]);
  assert.ok(!harness.logs[0].message.includes("hunter2"));
  assert.ok(!harness.logs[0].message.includes("secret-token"));
});

test("bounds an unterminated stderr line", async () => {
  const harness = createHarness({ maxStderrBufferBytes: 32 });
  const child = await startClient(harness);

  child.stderr.write("x".repeat(80));
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(Buffer.byteLength(harness.client.stderrBuffer), 32);
  assert.match(harness.logs.at(-1).message, /truncated to 32 bytes/);
});

test("uses unique requestIds and resolves validate_serial_config responses", async () => {
  const harness = createHarness();
  const child = await startClient(harness);
  const config = { portName: "COM3", baudRate: 9600 };

  const validationPromise = harness.client.validateSerialConfig(config);
  const request = await child.nextRequest();
  assert.deepEqual(request, {
    protocolVersion: PROTOCOL_VERSION,
    requestId: "2",
    command: COMMANDS.VALIDATE_SERIAL_CONFIG,
    payload: { config },
  });

  child.respond(request, { result: { valid: true, config } }, 4);
  assert.deepEqual(await validationPromise, { valid: true, config });
  assert.equal(harness.client.pending.size, 0);
});

test("validates sidecar command names and payload structure before stdin is touched", async () => {
  const harness = createHarness();
  await startClient(harness);

  await assert.rejects(() => harness.client.request("run_shell", { command: "dir" }), (error) => {
    assert.equal(error.code, "UNKNOWN_COMMAND");
    return true;
  });
  await assert.rejects(() => harness.client.request(COMMANDS.VALIDATE_SERIAL_CONFIG, null), (error) => {
    assert.equal(error.code, "INVALID_PAYLOAD");
    assert.match(error.message, /must be an object/);
    return true;
  });
  await assert.rejects(() => harness.client.request(COMMANDS.VALIDATE_SERIAL_CONFIG, [1, 2]), (error) => {
    assert.equal(error.code, "INVALID_PAYLOAD");
    return true;
  });
  const protoPayload = JSON.parse('{"config":{"nested":{"__proto__":{"polluted":true}}}}');
  await assert.rejects(() => harness.client.request(COMMANDS.VALIDATE_SERIAL_CONFIG, protoPayload), (error) => {
    assert.equal(error.code, "INVALID_PAYLOAD");
    assert.match(error.message, /unsafe key/);
    return true;
  });

  const deepPayload = { config: buildDeep(MAX_PAYLOAD_DEPTH) };
  await assert.rejects(() => harness.client.request(COMMANDS.VALIDATE_SERIAL_CONFIG, deepPayload), (error) => {
    assert.equal(error.code, "INVALID_PAYLOAD");
    assert.match(error.message, new RegExp(`depth ${MAX_PAYLOAD_DEPTH}`));
    return true;
  });

  const requestPromise = harness.client.validateSerialConfig({ portName: "COM3" });
  const request = await harness.children[0].nextRequest();
  harness.children[0].respond(request, { result: { valid: true } });
  await requestPromise;
  assert.equal("polluted" in {}, false);

  function buildDeep(depth) {
    let value = { leaf: true };
    for (let index = 0; index < depth; index += 1) value = { value };
    return value;
  }
});

test("sends typed FC03 build and parse requests to the Rust core", async () => {
  const harness = createHarness();
  const child = await startClient(harness);

  const buildPromise = harness.client.buildReadHoldingRegisters({
    unitId: 1,
    startAddress: 0,
    quantity: 2,
  });
  const buildRequest = await child.nextRequest();
  assert.deepEqual(buildRequest, {
    protocolVersion: PROTOCOL_VERSION,
    requestId: "2",
    command: COMMANDS.BUILD_READ_HOLDING_REGISTERS,
    payload: { unitId: 1, startAddress: 0, quantity: 2 },
  });
  const built = {
    adu: [1, 3, 0, 0, 0, 2, 196, 11],
    expectedResponseLength: 9,
    exceptionResponseLength: 5,
  };
  child.respond(buildRequest, { result: built });
  assert.deepEqual(await buildPromise, built);

  const response = [1, 3, 4, 0, 1, 0, 2, 42, 50];
  const parsePromise = harness.client.parseReadHoldingRegisters({
    response,
    unitId: 1,
    quantity: 2,
  });
  const parseRequest = await child.nextRequest();
  assert.equal(parseRequest.command, COMMANDS.PARSE_READ_HOLDING_REGISTERS);
  assert.deepEqual(parseRequest.payload, { response, unitId: 1, quantity: 2 });
  child.respond(parseRequest, { result: { status: "ok", registers: [1, 2] } });
  assert.deepEqual(await parsePromise, { status: "ok", registers: [1, 2] });
});

test("sends typed FC04 build and parse requests to the Rust core", async () => {
  const harness = createHarness();
  const child = await startClient(harness);

  const buildPromise = harness.client.buildReadInputRegisters({
    unitId: 1,
    startAddress: 0,
    quantity: 2,
  });
  const buildRequest = await child.nextRequest();
  assert.deepEqual(buildRequest, {
    protocolVersion: PROTOCOL_VERSION,
    requestId: "2",
    command: COMMANDS.BUILD_READ_INPUT_REGISTERS,
    payload: { unitId: 1, startAddress: 0, quantity: 2 },
  });
  const built = {
    adu: [1, 4, 0, 0, 0, 2, 113, 203],
    expectedResponseLength: 9,
    exceptionResponseLength: 5,
  };
  child.respond(buildRequest, { result: built });
  assert.deepEqual(await buildPromise, built);

  const response = [1, 4, 4, 0, 42, 255, 254, 90, 116];
  const parsePromise = harness.client.parseReadInputRegisters({
    response,
    unitId: 1,
    quantity: 2,
  });
  const parseRequest = await child.nextRequest();
  assert.equal(parseRequest.command, COMMANDS.PARSE_READ_INPUT_REGISTERS);
  assert.deepEqual(parseRequest.payload, { response, unitId: 1, quantity: 2 });
  child.respond(parseRequest, { result: { status: "ok", registers: [42, 65_534] } });
  assert.deepEqual(await parsePromise, { status: "ok", registers: [42, 65_534] });
});

test("maps a complete failure envelope to RustCoreRemoteError", async () => {
  const harness = createHarness();
  const child = await startClient(harness);
  const requestPromise = harness.client.validateSerialConfig({ portName: "" });
  const request = await child.nextRequest();

  child.respond(request, {
    ok: false,
    result: null,
    error: {
      code: "INVALID_SERIAL_CONFIG",
      message: "portName is required",
      details: { field: "portName" },
    },
  });

  await assert.rejects(requestPromise, (error) => {
    assert.ok(error instanceof RustCoreRemoteError);
    assert.equal(error.code, "INVALID_SERIAL_CONFIG");
    assert.equal(error.requestId, request.requestId);
    assert.equal(error.command, COMMANDS.VALIDATE_SERIAL_CONFIG);
    assert.deepEqual(error.details, { field: "portName" });
    return true;
  });
  assert.equal(harness.client.pending.size, 0);
});

test("removes a poll subscription when start_poll_stream fails", async () => {
  const harness = createHarness();
  const child = await startClient(harness);
  const startPromise = harness.client.startPollStream({
    streamId: "poll-failed",
    connectionId: "missing",
    fc: 3,
    startAddress: 0,
    quantity: 1,
    intervalMs: 1000,
  }, () => {}, () => {});
  const request = await child.nextRequest();
  assert.equal(harness.client.subscriptions.has("poll-failed"), true);
  child.respond(request, {
    ok: false,
    error: { code: "CONNECTION_NOT_FOUND", message: "connection missing" },
  });

  await assert.rejects(startPromise, (error) => error.code === "CONNECTION_NOT_FOUND");
  assert.equal(harness.client.subscriptions.has("poll-failed"), false);
});

test("resolves stream start and stop acknowledgements that also carry streamId", async () => {
  const harness = createHarness();
  const child = await startClient(harness);
  const dataEvents = [];
  const startPromise = harness.client.startPollStream({
    streamId: "poll-live",
    connectionId: "connection",
    fc: 3,
    startAddress: 100,
    quantity: 2,
    intervalMs: 50,
  }, (data) => dataEvents.push(data), () => {});
  const startRequest = await child.nextRequest();

  child.stdout.write(`${JSON.stringify({
    protocolVersion: PROTOCOL_VERSION,
    requestId: startRequest.requestId,
    streamId: "poll-live",
    streamEnd: false,
    ok: true,
    result: { streamId: "poll-live", started: true, intervalMs: 50 },
    error: null,
  })}\n`);
  assert.deepEqual(await startPromise, {
    streamId: "poll-live",
    started: true,
    intervalMs: 50,
  });

  child.stdout.write(`${JSON.stringify({
    protocolVersion: PROTOCOL_VERSION,
    requestId: null,
    streamId: "poll-live",
    streamEnd: false,
    ok: true,
    result: { streamId: "poll-live", registers: [0x1234, 0xabcd] },
    error: null,
  })}\n`);
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(dataEvents, [{ streamId: "poll-live", registers: [0x1234, 0xabcd] }]);

  const stopPromise = harness.client.stopPollStream({ streamId: "poll-live" });
  const stopRequest = await child.nextRequest();
  child.stdout.write(`${JSON.stringify({
    protocolVersion: PROTOCOL_VERSION,
    requestId: stopRequest.requestId,
    streamId: "poll-live",
    streamEnd: true,
    ok: true,
    result: { streamId: "poll-live", stopped: true },
    error: null,
  })}\n`);
  assert.deepEqual(await stopPromise, { streamId: "poll-live", stopped: true });
  assert.equal(harness.client.subscriptions.has("poll-live"), false);
});

test("times out one request, removes it from pending, and ignores its late response", async () => {
  const harness = createHarness({ requestTimeoutMs: 25 });
  const child = await startClient(harness);
  const requestPromise = harness.client.validateSerialConfig({ portName: "COM3" });
  const request = await child.nextRequest();

  await assert.rejects(requestPromise, (error) => error.code === "REQUEST_TIMEOUT");
  assert.equal(harness.client.pending.size, 0);

  child.respond(request, { result: { valid: true } });
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(harness.client.state, "ready");
  assert.match(harness.logs.at(-1).message, /unknown requestId/);
});

test("rejects every pending request when the sidecar crashes", async () => {
  const harness = createHarness();
  const child = await startClient(harness);
  const first = harness.client.validateSerialConfig({ portName: "COM3" });
  const second = harness.client.hello();
  await child.nextRequest();
  await child.nextRequest();

  child.close(7, null);

  await assert.rejects(first, (error) => error.code === "PROCESS_CRASHED");
  await assert.rejects(second, (error) => error.code === "PROCESS_CRASHED");
  assert.equal(harness.client.pending.size, 0);
  assert.equal(harness.client.state, "failed");
});

test("handles a broken Rust core stdin pipe without an unhandled error event", async () => {
  const harness = createHarness();
  const child = await startClient(harness);
  const requestPromise = harness.client.validateSerialConfig({ portName: "COM3" });
  await child.nextRequest();

  const brokenPipe = Object.assign(new Error("write EPIPE"), { code: "EPIPE" });
  child.stdin.emit("error", brokenPipe);

  await assert.rejects(requestPromise, (error) => error.code === "STDIN_ERROR");
  assert.equal(harness.client.state, "failed");
  assert.equal(child.killed, true);
});

test("terminates the sidecar when an unterminated stdout line exceeds the byte limit", async () => {
  const harness = createHarness({ maxLineBytes: 128 });
  const child = await startClient(harness);
  const requestPromise = harness.client.validateSerialConfig({ portName: "COM3" });
  await child.nextRequest();

  child.stdout.write(Buffer.alloc(129, 0x78));

  await assert.rejects(requestPromise, (error) => error.code === "LINE_TOO_LONG");
  assert.equal(harness.client.state, "failed");
  assert.equal(child.killed, true);
  assert.equal(harness.client.pending.size, 0);
});

test("treats non-JSON stdout as a protocol failure instead of a log line", async () => {
  const harness = createHarness();
  const child = await startClient(harness);
  const requestPromise = harness.client.validateSerialConfig({ portName: "COM3" });
  await child.nextRequest();

  child.stdout.write("not-json\n");

  await assert.rejects(requestPromise, (error) => error.code === "INVALID_JSON");
  assert.equal(child.killed, true);
  assert.equal(harness.logs.length, 0);
});

test("sends shutdown, waits for process close, and is idempotent", async () => {
  const harness = createHarness();
  const child = await startClient(harness);
  const shutdownPromise = harness.client.shutdown();
  const request = await child.nextRequest();
  assert.equal(request.command, COMMANDS.SHUTDOWN);
  assert.deepEqual(request.payload, {});

  child.respond(request, { result: { accepted: true } });
  child.close(0, null);

  assert.deepEqual(await shutdownPromise, { accepted: true });
  assert.equal(harness.client.state, "stopped");
  assert.deepEqual(await harness.client.shutdown(), { accepted: true });
  assert.equal(harness.client.pending.size, 0);
});
