const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(
  path.join(__dirname, "..", "docs", "enip-cip-golden-vectors.md"),
  "utf8",
);

test("EtherNet/IP/CIP golden document keeps the software-only boundary explicit", () => {
  for (const marker of [
    "RegisterSession/UnregisterSession",
    "SendRRData/CPF",
    "CIP Read Tag",
    "S2-S4a",
    "不是",
    "CompactLogix/ControlLogix",
    "真实控制器的 L2",
    "独立 TCP 对端",
  ]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("EtherNet/IP/CIP golden document contains the exact first-round vectors", () => {
  for (const frame of [
    "65 00 04 00 00 00 00 00 00 00 00 00 01 00 00 00",
    "66 00 00 00 44 33 22 11 00 00 00 00 07 00 00 00",
    "6F 00 1E 00 44 33 22 11 00 00 00 00 07 00 00 00",
    "4C 05 91 05 4D 79 54 61 67 00 28 03 02 00",
    "CC 00 00 00 34 12",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
