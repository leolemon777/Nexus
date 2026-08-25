const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const commands = [
  "open_mqtt_connection",
  "mqtt_subscribe",
  "mqtt_read_publish",
  "mqtt_ping",
  "mqtt_build_connect",
  "mqtt_parse_connack",
  "mqtt_build_subscribe",
  "mqtt_parse_suback",
  "mqtt_parse_publish",
  "mqtt_build_pingreq",
  "mqtt_parse_pingresp",
  "mqtt_build_disconnect",
];

test("MQTT 3.1.1 commands cross the Rust/Electron bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of commands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("MQTT page exposes the TCP read-only subscription boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  for (const marker of [
    'data-view="mqtt"',
    'id="mqtt-view"',
    "mqtt-host",
    "mqtt-open-connection",
    "mqtt-subscribe",
    "mqtt-read-publish",
    "TCP 1883",
    "TCP 只读订阅 S4b",
    "CONNECT/CONNACK",
    "不发送 PUBLISH",
    "open_mqtt_connection",
    "mqtt_read_publish",
  ]) {
    assert.match(html + main, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});
