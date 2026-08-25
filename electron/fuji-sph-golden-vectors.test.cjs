const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "fuji-sph-golden-vectors.md"), "utf8");

test("Fuji SPH golden document keeps the software-only boundary explicit", () => {
  for (const marker of ["Fuji", "SPH", "20 字节", "00H", "01H", "TCP 18245", "L2", "位写入"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});

test("Fuji SPH golden document contains fixed header and little-endian vectors", () => {
  for (const vector of ["FB 80 80 00 FF 7B FE", "M10.258", "02 01 00", "34 12", "wordCount"]) {
    assert.match(doc, new RegExp(vector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), vector);
  }
});
