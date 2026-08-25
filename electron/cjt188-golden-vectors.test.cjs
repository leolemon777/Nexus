"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "cjt188-golden-vectors.md"), "utf8");

test("CJ/T 188 golden document fixes the audited 2004 frame and deviations", () => {
  for (const marker of [
    "单 68H",
    "模 256 算术和",
    "无 +33H",
    "DI0 DI1 + SER",
    "双 68H + XOR + +33H",
    "不继承",
    "10H 冷水水表",
    "11H 热水水表",
    "20H 热量表",
    "30H 燃气表",
    "0x40",
    "DL/T 645",
    "901F",
    "123456.78",
    "S0",
    "FFH",
    "软件审计 + 离线编解码 S1-S3",
    "L2 pending",
  ]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("CJ/T 188 golden document contains fixed vectors and checksum failure", () => {
  for (const frame of [
    "68 10 77 66 55 44 33 22 11 01 03 90 1F 01 08 16",
    "FE FE FE 68 10 77 66 55 44 33 22 11 01 03 90 1F 01 08 16",
    "FE FE FE 68 10 77 66 55 44 33 22 11 81 09 90 1F 01 78 56 34 12 00 FF A1 16",
    "68 20 77 66 55 44 33 22 11 81 09 90 1F 01 78 56 34 12 00 FF B1 16",
    "68 30 77 66 55 44 33 22 11 C1 04 90 1F 01 02 EB 16",
    "CJT188_CHECKSUM_MISMATCH",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
