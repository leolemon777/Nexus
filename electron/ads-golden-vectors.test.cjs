const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "ads-ams-golden-vectors.md"), "utf8");

test("ADS/AMS golden document keeps the software-only boundary explicit", () => {
  for (const marker of [
    "ADS/AMS over TCP",
    "AMS NetId",
    "ReadDeviceInfo",
    "ReadState",
    "S2-S4a",
    "独立 TCP 对端",
    "TwinCAT",
  ]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("ADS/AMS golden document contains the fixed read vectors", () => {
  for (const frame of [
    "00 00 2C 00 00 00",
    "02 00 04 00 0C 00 00 00",
    "00 F0 00 00 20 00 00 00 04 00 00 00",
    "02 00 05 00 0C 00 00 00",
    "11 22 33 44",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
