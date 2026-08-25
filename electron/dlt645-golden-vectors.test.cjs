"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "dlt645-golden-vectors.md"), "utf8");

test("DL/T 645 golden document fixes revision and safety boundaries", () => {
  for (const marker of [
    "DL/T 645-2007",
    "DL/T 645-1997",
    "全部代替",
    "12 位 BCD",
    "+33H",
    "2400 8E1",
    "B1H/A1H",
    "写数据",
    "拉闸/合闸",
    "软件 S2-S4a",
    "L2 pending",
  ]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("DL/T 645 golden document contains fixed requests, responses, and checksum failure", () => {
  for (const frame of [
    "FE FE FE FE 68 12 90 78 56 34 12 68 11 04 33 33 34 33 68 16",
    "68 12 90 78 56 34 12 68 91 08 33 33 34 33 AB 89 67 45 CC 16",
    "68 12 90 78 56 34 12 68 D1 01 39 91 16",
    "68 12 90 78 56 34 12 68 01 02 43 C3 8F 16",
    "68 12 90 78 56 34 12 68 81 06 43 C3 33 33 34 33 E0 16",
    "DLT645_CHECKSUM_MISMATCH",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});

