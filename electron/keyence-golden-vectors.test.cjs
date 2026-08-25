const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "keyence-kv-host-link-golden-vectors.md"), "utf8");

test("Keyence KV Host Link golden document keeps the software-only boundary explicit", () => {
  for (const marker of ["KV Host Link ASCII", "CR 02", "MC Compatible", "S2-S4a", "真实 PLC L2", "独立 TCP 对端"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("Keyence KV Host Link golden document contains exact ASCII vectors", () => {
  for (const vector of [
    "43 52 20 30 32 0D",
    "52 44 53 20 44 4D 30 2E 55 20 32 0D",
    "52 44 53 20 4D 52 31 30 30 31 20 33 0D",
    "57 52 53 20 44 4D 30 2E 55 20 32 20 31 20 36 35 35 33 35 0D",
    "53 54 20 4D 52 31 30 30 31 0D",
  ]) {
    assert.match(doc, new RegExp(vector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), vector);
  }
});
