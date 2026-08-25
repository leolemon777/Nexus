const assert = require("node:assert/strict");
const dgram = require("node:dgram");
const path = require("node:path");
const test = require("node:test");
const { EventEmitter } = require("node:events");
const bacnet = require("bacstack");

const {
  COMMANDS,
  RustCoreClient,
} = require("../electron/rust-core-client.cjs");

const SILENT_LOGGER = Object.freeze({
  debug() {},
  info() {},
  warn() {},
  error() {},
});

const BROADCAST_MARKER = "__last_bacnet_peer__";

class SourceAwareUdpTransport extends EventEmitter {
  constructor() {
    super();
    this.server = dgram.createSocket({ type: "udp4" });
    this.lastPeer = null;
    this.ready = new Promise((resolve, reject) => {
      const onError = (error) => reject(error);
      this.server.once("error", onError);
      this.server.bind(0, "127.0.0.1", () => {
        this.server.off("error", onError);
        resolve(this.server.address());
      });
    });
    this.server.on("message", (message, remote) => {
      this.lastPeer = { address: remote.address, port: remote.port };
      this.emit("message", message, `${remote.address}:${remote.port}`);
    });
    this.server.on("error", (error) => this.emit("error", error));
  }

  getBroadcastAddress() {
    return BROADCAST_MARKER;
  }

  getMaxPayload() {
    return 1482;
  }

  send(buffer, offset, receiver) {
    const destination = receiver === BROADCAST_MARKER
      ? this.lastPeer
      : this.parseReceiver(receiver);
    assert.ok(destination, "BACstack transport has no destination");
    this.server.send(buffer, 0, offset, destination.port, destination.address);
  }

  parseReceiver(receiver) {
    const match = /^(\d+\.\d+\.\d+\.\d+):(\d+)$/.exec(receiver);
    assert.ok(match, `BACstack receiver must include source port: ${receiver}`);
    return { address: match[1], port: Number(match[2]) };
  }

  open() {}

  close() {
    if (this.closed || !this.server) return;
    this.closed = true;
    if (this.server.address()) this.server.close();
  }
}

test("Rust BACnet/IP read-only client interoperates with bacstack", { timeout: 30_000 }, async (t) => {
  const transport = new SourceAwareUdpTransport();
  const address = await transport.ready;
  assert.equal(address.address, "127.0.0.1");
  assert.ok(address.port > 0);

  const stack = new bacnet({
    transport,
    apduTimeout: 2_000,
  });
  const stackEvents = [];

  stack.on("whoIs", (request) => {
    stackEvents.push(`whoIs:${request.lowLimit ?? "*"}:${request.highLimit ?? "*"}`);
    stack.iAmResponse(1001, bacnet.enum.Segmentation.NO_SEGMENTATION, 42);
  });
  stack.on("readProperty", ({ address: receiver, invokeId, request }) => {
    stackEvents.push(`readProperty:${request.objectId.type}:${request.objectId.instance}:${request.property.id}`);
    stack.readPropertyResponse(receiver, invokeId, request.objectId, request.property, [
      { value: 123.45, type: bacnet.enum.ApplicationTags.REAL },
    ]);
  });

  const binaryPath = process.env.NEXUS_RUST_CORE_PATH
    ?? path.resolve(__dirname, "..", "rust-core", "target", "debug", "nexus-rust-core.exe");
  const core = new RustCoreClient({
    binaryPath,
    requestTimeoutMs: 10_000,
    shutdownGraceMs: 2_000,
    logger: SILENT_LOGGER,
  });
  let connectionOpen = false;

  t.after(async () => {
    if (connectionOpen && core.state === "ready") {
      await core.request(COMMANDS.CLOSE_CONNECTION, { connectionId: "bacnet-bacstack" }).catch(() => {});
    }
    if (core.state === "ready") await core.shutdown().catch(() => {});
    stack.close();
    transport.close();
  });

  await core.start();
  const opened = await core.request(COMMANDS.OPEN_BACNET_IP_CONNECTION, {
    connectionId: "bacnet-bacstack",
    host: "127.0.0.1",
    port: address.port,
  });
  connectionOpen = true;
  assert.equal(opened.udpConnected, true);
  assert.equal(opened.readOnly, true);
  assert.equal(opened.controlsEnabled, false);
  assert.match(opened.l2Evidence, /bacstack 0\.0\.1-beta\.14 independent stack interoperability passed/);

  const discovered = await core.request(COMMANDS.BACNET_IP_WHOIS, {
    connectionId: "bacnet-bacstack",
    timeoutMs: 300,
  });
  assert.equal(discovered.responseCount, 1);
  assert.equal(discovered.responses[0].serviceName, "i-am");
  assert.equal(discovered.responses[0].iAm.deviceInstance, 1001);
  assert.equal(discovered.responses[0].iAm.segmentation, "none");
  assert.equal(discovered.responses[0].iAm.vendorId, 42);

  const read = await core.request(COMMANDS.BACNET_IP_READ_PROPERTY_LIVE, {
    connectionId: "bacnet-bacstack",
    objectType: 0,
    objectInstance: 1001,
    propertyIdentifier: 85,
    timeoutMs: 500,
  });
  assert.equal(read.request.invokeId, 1);
  assert.equal(read.ack.invokeId, 1);
  assert.equal(read.ack.objectType, 0);
  assert.equal(read.ack.objectInstance, 1001);
  assert.equal(read.ack.propertyIdentifier, 85);
  assert.equal(read.ack.value.kind, "real");
  const real = read.ack.value.real;
  assert.ok(Math.abs(real - 123.45) < 0.0001, `unexpected REAL value ${real}`);

  await core.request(COMMANDS.CLOSE_CONNECTION, { connectionId: "bacnet-bacstack" });
  connectionOpen = false;
  await core.shutdown();

  assert.deepEqual(stackEvents, [
    "whoIs:*:*",
    "readProperty:0:1001:85",
  ]);
});
