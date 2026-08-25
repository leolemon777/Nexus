const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

test("GE SRTP golden-vector document records the first codec boundary", () => {
  const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "ge-srtp-golden-vectors.md"), "utf8");
  for (const marker of ["GE SRTP", "56 字节", "R1", "0x4C", "0x94", "0xD4", "ge_srtp_parse_response", "L2"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\\]\\]/g, "\\$&")), marker);
  }
});
