"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(path.join(__dirname, "..", "docs", "bacnet-ip-golden-vectors.md"), "utf8");

test("BACnet/IP golden document fixes old implementation deviations", () => {
  for (const marker of [
    "旧 `Nexus.Bacnet` 移除审计",
    "BVLC Function 全部写 00H",
    "上下文标签缺 class 位",
    "I-Am 缺 Object Identifier 标签",
    "Original-Unicast-NPDU=0AH",
    "Original-Broadcast-NPDU=0BH",
    "tag number 占高 4 位",
    "C4",
    "91",
    "ReadProperty 请求",
    "ComplexACK",
    "Unsigned/Real",
    "软件审计 + 离线编解码 + 独立脚本 UDP 对端 + bacstack 0.0.1-beta.14 独立栈互操作 S1-S4b",
    "L2 pending",
  ]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("BACnet/IP golden document contains fixed Who-Is and I-Am frames", () => {
  for (const frame of [
    "81 0B 00 08 01 00 10 08",
    "81 0B 00 0E 01 00 10 08 0A 03 E8 1A 07 D0",
    "81 0A 00 14 01 00 10 00 C4 02 00 03 E9 22 01 E0 91 03 21 2A",
    "81 0B 00 14 01 00 10 00 C4 02 00 03 E9 22 01 E0 91 03 21 2A",
    "81 0A 00 11 01 04 00 03 01 0C 0C 00 00 03 E9 19 55",
    "81 0A 00 17 01 00 30 01 0C 0C 00 00 03 E9 19 55 3E 44 42 F6 E6 66 3F",
    "81 00 00 08 01 00 10 08",
    "81 0B 00 08 01 20 10 08",
    "81 0B 00 09 01 00 10 08",
    "BACNET_BVLC_FUNCTION_UNSUPPORTED",
    "BACNET_NPDU_CONTROL_UNSUPPORTED",
    "BACNET_BVLC_LENGTH_MISMATCH",
    "BACNET_READ_PROPERTY_MISMATCH",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
