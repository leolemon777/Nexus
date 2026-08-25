"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const codecCommands = [
  "dlt645_parse_address",
  "dlt645_parse_data_id",
  "dlt645_build_read_request",
  "dlt645_parse_frame",
  "dlt645_parse_read_response",
];

test("DL/T 645 commands cross the Rust/Electron bridge and shared-COM handler", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of codecCommands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
  assert.match(preload, /"dlt645_serial_read"/);
  assert.match(main, /nexus:dlt645_serial_read/);
  assert.match(main, /createDlt645SerialService/);
});

test("DL/T 645 page exposes both revisions but only the read-only boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const guide = fs.readFileSync(path.join(root, "src", "protocol-guides.js"), "utf8");
  const combined = html + main + guide;
  for (const marker of [
    'data-view="dlt645"',
    'id="dlt645-view"',
    "DL/T 645-2007",
    "DL/T 645-1997",
    "共享 COM 只读",
    "12 位表地址",
    "数据标识 DI",
    "+33H/-33H",
    "生成读请求",
    "解析读响应",
    "dlt645_serial_read",
    "dlt645_build_read_request",
    "dlt645_parse_read_response",
    "L2 pending",
  ]) {
    assert.match(combined, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
  for (const forbidden of [
    "dlt645_write",
    "dlt645_set_time",
    "dlt645_set_address",
    "dlt645_freeze",
    "dlt645_breaker_control",
  ]) {
    assert.doesNotMatch(html + main, new RegExp(forbidden), `${forbidden} must stay outside the UI`);
  }
});

