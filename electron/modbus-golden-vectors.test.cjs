const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(
  path.join(__dirname, "..", "docs", "modbus-golden-vectors.md"),
  "utf8",
);

test("Modbus golden document keeps UDP, serial FC and L2 boundaries explicit", () => {
  assert.match(doc, /udp_read_holding_registers/);
  assert.match(doc, /read_coils_once/);
  assert.match(doc, /CONNECTION_TYPE_MISMATCH/);
  assert.match(doc, /不是[\s\S]*真实 PLC\/仪表 L2/);
  assert.match(doc, /广播站号 0/);
});

test("Modbus golden document contains the FC03 RTU vectors", () => {
  for (const frame of ["01 03 00 00 00 02 C4 0B", "01 03 00 00 00 0A C5 CD", "01 83 02"]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
