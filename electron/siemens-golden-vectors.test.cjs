const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(
  path.join(__dirname, "..", "docs", "siemens-route-golden-vectors.md"),
  "utf8",
);

test("Siemens route golden document forbids silent S7comm fallback", () => {
  assert.match(doc, /不得静默回落 S7comm/);
  assert.match(doc, /open_s7_connection/);
  assert.match(doc, /ppi_serial_read/);
  assert.match(doc, /uss_serial_read/);
  assert.match(doc, /rk512_serial_read/);
  assert.match(doc, /s7web_connect/);
  assert.match(doc, /默认端口 2000/);
  assert.match(doc, /不等于 S7-200\/SMART\/1200\/1500 L2/);
});
