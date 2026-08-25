const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "dnp3-golden-vectors.md"), "utf8");

test("DNP3 golden document fixes the read-only master and L2 boundary", () => {
  for (const marker of [
    "DNP3",
    "TCP 20000 Master",
    "Class 0/1/2/3",
    "IIN",
    "Quality",
    "Select",
    "Operate",
    "Time Write",
    "Secure Authentication",
    "独立实现 CRC",
    "脚本 Outstation",
    "真实 RTU/IED",
    "L2 pending",
    "非商业、非生产",
    "OpenDNP3 已归档",
  ]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("DNP3 golden document contains the fixed first-round frames", () => {
  for (const frame of [
    "05 64 05 C0 01 00 00 04 E9 21",
    "05 64 14 C4 00 04 01 00 E9 B6 C0 C0 01 3C 01 06 3C 02 06 3C 03 06 3C 04 06 9C 09",
    "C0 01 3C 01 06 3C 02 06 3C 03 06 3C 04 06",
    "C3 01 1E 05 01 34 12 35 12",
    "C0 00",
    "D7 00",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});

