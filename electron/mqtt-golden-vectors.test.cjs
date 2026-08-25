const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "mqtt-311-golden-vectors.md"), "utf8");

test("MQTT golden document keeps the read-only broker boundary explicit", () => {
  for (const marker of ["MQTT 3.1.1", "CONNECT/CONNACK", "SUBSCRIBE/SUBACK", "QoS 0", "不提供 PUBLISH 写入", "Sparkplug B", "独立 TCP Broker", "Aedes 1.1.1", "MQTT.js 5.15.2", "MQTT_SUBACK_REJECTED", "MQTT_CONNACK_REJECTED"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("MQTT golden document contains the fixed first-round vectors", () => {
  for (const frame of [
    "10 16 00 04 4D 51 54 54 04 02 00 1E",
    "20 02 00 00",
    "82 14 00 07 00 0F",
    "90 03 00 07 00",
    "30 0D 00 05 73 74 61 74 65 4F 4B 21 00 01 02",
    "C0 00",
    "D0 00",
    "E0 00",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
