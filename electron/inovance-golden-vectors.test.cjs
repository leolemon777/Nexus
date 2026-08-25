const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "inovance-modbus-golden-vectors.md"), "utf8");

test("Inovance golden document keeps the standard Modbus/profile boundary explicit", () => {
  for (const marker of ["H3U", "H5U", "标准 Modbus", "inovance_parse_address", "AM/AC/Easy", "L2"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});

test("Inovance golden document contains published H3U/H5U vectors", () => {
  for (const vector of ["X10", "0xF808", "Y1777", "0xFFFF", "C200", "0xF700", "M7680", "R32767", "0xAFFF"]) {
    assert.match(doc, new RegExp(vector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), vector);
  }
});
