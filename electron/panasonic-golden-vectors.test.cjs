const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "panasonic-mewtocol-golden-vectors.md"), "utf8");

test("Panasonic golden document keeps the software-only boundary explicit", () => {
  for (const marker of ["MEWTOCOL-COM", "9600 8O1", "S2/S3", "S4 软件会话", "真实 FP PLC L2", "不能自动开关端口"]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("Panasonic golden document contains exact ASCII/BCC vectors", () => {
  for (const vector of [
    "%01#RDD001000010055\\r",
    "%01#WDD0010000100640052\\r",
    "%01#RCSX001F6A\\r",
    "%01$RD640014\\r",
  ]) {
    assert.match(doc, new RegExp(vector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), vector);
  }
});
