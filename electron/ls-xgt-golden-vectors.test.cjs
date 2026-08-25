const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "ls-xgt-fenet-golden-vectors.md"), "utf8");

test("LS XGT golden document keeps the software-only boundary explicit", () => {
  for (const marker of ["XGT FEnet", "TCP 2004", "LSIS-XGT", "S2-S4a", "真实 PLC L2", "独立 TCP 对端"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("LS XGT golden document contains exact binary vector fields", () => {
  for (const vector of [
    "4C 53 49 53 2D 58 47 54",
    "54 00 02 00 00 00 01 00 06 00 25 44 57 31 30 30",
    "55 00 02 00 00 00 01 00 00 00 00 00 01 00 04 00 11 22 33 44",
  ]) {
    assert.match(doc, new RegExp(vector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), vector);
  }
});
