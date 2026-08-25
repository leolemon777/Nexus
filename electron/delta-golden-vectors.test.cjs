const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "delta-modbus-golden-vectors.md"), "utf8");

test("Delta golden document keeps the profile-only boundary explicit", () => {
  for (const marker of ["DVP/AS", "标准 Modbus", "delta_parse_address", "D100.5", "真实 DVP/AS/ES3 L2"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});

test("Delta golden document contains published boundary vectors", () => {
  for (const vector of ["D4096", "0x9000", "M1536", "0xB000", "Y17", "0x050F", "X1.2", "0x6012", "D29999", "0x752F"]) {
    assert.match(doc, new RegExp(vector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), vector);
  }
});
