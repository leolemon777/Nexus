const assert = require("node:assert/strict");
const net = require("node:net");
const path = require("node:path");
const test = require("node:test");

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

function listen(server) {
  return new Promise((resolve, reject) => {
    const onError = (error) => reject(error);
    server.once("error", onError);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", onError);
      resolve(server.address());
    });
  });
}

function closeServer(server) {
  if (!server?.listening) return Promise.resolve();
  return new Promise((resolve) => server.close(() => resolve()));
}

function closeBroker(broker) {
  if (!broker || broker.closed) return Promise.resolve();
  return new Promise((resolve) => broker.close(resolve));
}

function waitForMqttConnect(client) {
  return new Promise((resolve, reject) => {
    const onConnect = () => {
      client.off("error", onError);
      resolve();
    };
    const onError = (error) => {
      client.off("connect", onConnect);
      reject(error);
    };
    client.once("connect", onConnect);
    client.once("error", onError);
  });
}

function closeMqttClient(client) {
  if (!client || client.disconnected) return Promise.resolve();
  return new Promise((resolve) => client.end(true, {}, resolve));
}

test("Rust MQTT read-only session interoperates with Aedes and fails closed on auth/ACL", { timeout: 30_000 }, async (t) => {
  const [{ Aedes }, mqttModule] = await Promise.all([import("aedes"), import("mqtt")]);
  const mqttConnect = mqttModule.connect ?? mqttModule.default?.connect;
  assert.equal(typeof mqttConnect, "function");

  const brokerEvents = [];
  const broker = await Aedes.createBroker({
    authenticate(client, username, _password, done) {
      if (client.id === "nexus-auth-required" && !username) {
        const error = new Error("credentials required");
        error.returnCode = 4;
        done(error, false);
        return;
      }
      done(null, true);
    },
    authorizeSubscribe(_client, subscription, done) {
      if (subscription.topic === "nexus/forbidden/#") {
        done(null, null);
        return;
      }
      done(null, subscription);
    },
  });
  broker.on("clientReady", (client) => brokerEvents.push(`ready:${client.id}`));
  broker.on("subscribe", (subscriptions, client) => {
    brokerEvents.push(`subscribe:${client.id}:${subscriptions.map((item) => item.topic).join(",")}`);
  });
  broker.on("ping", (_packet, client) => brokerEvents.push(`ping:${client.id}`));

  const server = net.createServer(broker.handle);
  const address = await listen(server);
  assert.equal(address.address, "127.0.0.1");

  const binaryPath = process.env.NEXUS_RUST_CORE_PATH
    ?? path.resolve(__dirname, "..", "rust-core", "target", "debug", "nexus-rust-core.exe");
  const core = new RustCoreClient({
    binaryPath,
    requestTimeoutMs: 10_000,
    shutdownGraceMs: 2_000,
    logger: SILENT_LOGGER,
  });
  let publisher;
  let primaryOpen = false;

  t.after(async () => {
    await closeMqttClient(publisher).catch(() => {});
    if (primaryOpen && core.state === "ready") {
      await core.request(COMMANDS.CLOSE_CONNECTION, { connectionId: "mqtt-aedes" }).catch(() => {});
    }
    if (core.state === "ready") await core.shutdown().catch(() => {});
    await closeServer(server).catch(() => {});
    await closeBroker(broker).catch(() => {});
  });

  await core.start();
  const opened = await core.request(COMMANDS.OPEN_MQTT_CONNECTION, {
    connectionId: "mqtt-aedes",
    host: "127.0.0.1",
    port: address.port,
    clientId: "nexus-aedes-readonly",
    keepAlive: 5,
    cleanSession: true,
  });
  primaryOpen = true;
  assert.equal(opened.transport, "tcp");
  assert.equal(opened.returnCode, 0);
  assert.equal(opened.readOnly, true);

  const subscribed = await core.request(COMMANDS.MQTT_SUBSCRIBE, {
    connectionId: "mqtt-aedes",
    topicFilter: "nexus/e2e/temperature",
    qos: 0,
  });
  assert.equal(subscribed.returnCode, 0);
  assert.equal(subscribed.topicFilter, "nexus/e2e/temperature");

  publisher = mqttConnect(`mqtt://127.0.0.1:${address.port}`, {
    clientId: "nexus-external-publisher",
    clean: true,
    protocolVersion: 4,
    reconnectPeriod: 0,
  });
  await waitForMqttConnect(publisher);
  const payload = JSON.stringify({ value: 23.75, unit: "C", quality: "good" });
  await publisher.publishAsync("nexus/e2e/temperature", payload, { qos: 0, retain: false });

  const received = await core.request(COMMANDS.MQTT_READ_PUBLISH, {
    connectionId: "mqtt-aedes",
  });
  assert.equal(received.topic, "nexus/e2e/temperature");
  assert.equal(received.payloadUtf8, payload);
  assert.equal(received.qos, 0);
  assert.equal(received.retain, false);
  assert.equal(received.readOnly, true);

  const ping = await core.request(COMMANDS.MQTT_PING, { connectionId: "mqtt-aedes" });
  assert.equal(ping.ping, true);
  assert.equal(ping.requestHex, "C0 00");
  assert.equal(ping.responseHex, "D0 00");

  await assert.rejects(
    core.request(COMMANDS.MQTT_SUBSCRIBE, {
      connectionId: "mqtt-aedes",
      topicFilter: "nexus/forbidden/#",
      qos: 0,
    }),
    (error) => error?.code === "MQTT_SUBACK_REJECTED",
  );

  await assert.rejects(
    core.request(COMMANDS.OPEN_MQTT_CONNECTION, {
      connectionId: "mqtt-auth-rejected",
      host: "127.0.0.1",
      port: address.port,
      clientId: "nexus-auth-required",
      keepAlive: 5,
      cleanSession: true,
    }),
    (error) => error?.code === "MQTT_CONNACK_REJECTED" && error?.details?.returnCode === 4,
  );

  assert.ok(brokerEvents.includes("ready:nexus-aedes-readonly"));
  assert.ok(brokerEvents.includes("subscribe:nexus-aedes-readonly:nexus/e2e/temperature"));
  assert.ok(brokerEvents.includes("ping:nexus-aedes-readonly"));

  await core.request(COMMANDS.CLOSE_CONNECTION, { connectionId: "mqtt-aedes" });
  primaryOpen = false;
  await closeMqttClient(publisher);
  publisher = undefined;
  await core.shutdown();
});
