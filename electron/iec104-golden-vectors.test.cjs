const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "iec104-golden-vectors.md"), "utf8");

test("IEC104 golden document fixes the read-only master and L2 boundary", () => {
  for (const marker of [
    "IEC 60870-5-104",
    "Client/Master",
    "STARTDT",
    "ACT_CON",
    "ACT_TERM",
    "M_SP_NA_1",
    "M_DP_NA_1",
    "M_ME_NA_1",
    "M_ME_NC_1",
    "M_IT_NA_1",
    "不提供遥控",
    "独立脚本 Outstation",
    "真实 RTU/IED",
    "L2 pending",
  ]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("IEC104 golden document contains the fixed first-round frames", () => {
  for (const frame of [
    "68 04 07 00 00 00",
    "68 04 0B 00 00 00",
    "68 04 13 00 00 00",
    "68 04 23 00 00 00",
    "68 04 43 00 00 00",
    "68 04 83 00 00 00",
    "68 0E 00 00 00 00 64 01 06 00 01 00 00 00 00 14",
    "68 0E 00 00 02 00 64 01 07 00 01 00 00 00 00 14",
    "68 04 01 00 02 00",
    "01 01 14 00 01 00 2A 00 00 81",
    "68 0E 0C 00 02 00 64 01 0A 00 01 00 00 00 00 14",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
