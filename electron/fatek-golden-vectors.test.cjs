const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "fatek-ascii-golden-vectors.md"), "utf8");

test("FATEK golden document keeps the software-only boundary explicit", () => {
  for (const marker of ["FATEK", "STX", "ETX", "校验", "TCP 5000", "fatek_parse_response", "L2"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});

test("FATEK golden document contains command and address vectors", () => {
  for (const vector of ["014603R0001275", "X0000", "Y0000", "R00012", "44", "45", "46", "47"]) {
    assert.match(doc, new RegExp(vector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), vector);
  }
});
