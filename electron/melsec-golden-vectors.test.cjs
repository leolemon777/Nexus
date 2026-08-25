const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(
  path.join(__dirname, "..", "docs", "melsec-golden-vectors.md"),
  "utf8",
);

test("MELSEC golden document keeps C24 read-only and 3E fallback boundaries explicit", () => {
  assert.match(doc, /mc_c24_serial_read/);
  assert.match(doc, /FX_BAD_PROTOCOL/);
  assert.match(doc, /不得回落到 3E/);
  assert.match(doc, /不是[\s\S]*真机 L2/);
  assert.match(doc, /X \/ Y[\s\S]*八进制/);
});

test("MELSEC golden document contains the 3E D100 vectors", () => {
  for (const frame of [
    "50 00 00 FF FF 03 00 0C 00 10 00 01 04 01 00 64 00 00 A8 01 00",
    "D0 00 00 FF FF 03 00 04 00 00 00 34 12",
    "D0 00 00 FF FF 03 00 02 00 00 00",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
