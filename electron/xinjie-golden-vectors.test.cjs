const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "xinjie-modbus-golden-vectors.md"), "utf8");

test("Xinje golden document keeps the confirmed D-only boundary explicit", () => {
  for (const marker of ["XC", "XD/XL", "标准 Modbus", "D100", "xinjie_parse_address", "其他区域", "L2"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});

test("Xinje golden document contains D and fail-closed vectors", () => {
  for (const vector of ["D0", "0x0000", "D100", "0x0064", "D65535", "0xFFFF", "X10", "HD0", "M0"]) {
    assert.match(doc, new RegExp(vector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), vector);
  }
});
