"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(
  path.join(__dirname, "..", "docs", "knxnet-ip-tunneling-golden-vectors.md"),
  "utf8",
);

test("KNXnet/IP golden document fixes audited Tunneling boundaries", () => {
  for (const marker of [
    "旧 `Nexus.Knx` 审计",
    "`10 00`",
    "缺 Header Length 06H",
    "无 Connect/Channel/Sequence/Tunneling ACK",
    "UDP 3671",
    "GroupValueRead",
    "GroupValueResponse",
    "Tunneling ACK",
    "软件审计 + 离线编解码 + 独立脚本 UDP 网关、手动/周期 Connection State、显式重连与 knx 独立栈互通 S1-S4b",
    "knx@2.5.4",
    "scripts/knx-independent-stack-integration.test.cjs",
    "open_knx_connection",
    "knx_group_read",
    "knx_disconnect",
    "knx_connection_state",
    "knx_start_keepalive",
    "knx_stop_keepalive",
    "knx_keepalive_status",
    "GroupValueRead → 网关 ACK → GroupValueResponse → 客户端 ACK",
    "KNX_CONNECTION_STATE_STATUS",
    "CONNECTION_NOT_FOUND",
    "knx_auto_reconnect",
    "L2 pending",
  ]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("KNXnet/IP golden document contains exact offline vectors", () => {
  for (const frame of [
    "06 10 02 05 00 1A",
    "08 01 7F 00 00 01 C3 50",
    "04 04 02 00",
    "06 10 02 06 00 14 15 00",
    "04 04 11 01",
    "06 10 04 20 00 15",
    "04 15 00 00",
    "11 00 BC E0 00 00 0A 03 01 00 00",
    "06 10 04 21 00 0A 04 15 00 00",
    "06 10 04 20 00 17",
    "29 00 BC E0 11 01 0A 03 03 00 40 12 34",
    "KNX_HEADER_INVALID",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
