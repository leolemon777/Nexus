"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const codecCommands = [
  "knx_parse_group_address",
  "knx_build_connect_request",
  "knx_parse_connect_response",
  "knx_build_group_read_request",
  "knx_parse_tunneling_request",
  "knx_parse_group_value_response",
  "knx_parse_tunneling_ack",
  "knx_build_tunneling_ack",
  "open_knx_connection",
  "knx_group_read",
  "knx_disconnect",
  "knx_connection_state",
  "knx_start_keepalive",
  "knx_stop_keepalive",
  "knx_keepalive_status",
];

test("KNXnet/IP commands cross the Rust/Electron offline codec bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of codecCommands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("KNXnet/IP page exposes only Tunneling read-only offline boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const guide = fs.readFileSync(path.join(root, "src", "protocol-guides.js"), "utf8");
  const combined = html + main + guide;
  for (const marker of [
    'data-view="knx"',
    'id="knx-view"',
    "KNXnet/IP Tunneling v1",
    "06 10",
    "Connect Request",
    "GroupValueRead",
    "GroupValueResponse",
    "Tunneling ACK",
    "UDP 3671",
    "不打开 UDP",
    "L_Data.req",
    "L_Data.ind",
    "旧 Nexus.Knx",
    "knx_build_connect_request",
    "knx_build_group_read_request",
    "knx_parse_group_value_response",
    "knx_build_tunneling_ack",
    "实机 L2 pending",
    "UDP 只读隧道会话",
    "GroupValueRead → 网关 ACK → GroupValueResponse → 客户端 ACK",
    "Connection State 保活",
    "knx_connection_state",
    "启动周期保活",
    "knx_start_keepalive",
    "失败后只释放本地会话，不自动改写连接状态",
  ]) {
    assert.match(combined, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
  for (const forbidden of [
    "knx_group_write",
    "knx_write_group_value",
    "knx_group_write_live",
    "knx_scene_control",
    "knx_auto_reconnect",
  ]) {
    assert.doesNotMatch(html + main, new RegExp(forbidden), `${forbidden} must stay outside the UI`);
  }
});
