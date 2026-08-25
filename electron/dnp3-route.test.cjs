const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const commands = [
  "open_dnp3_connection",
  "dnp3_integrity_poll",
  "dnp3_class_scan",
  "dnp3_read",
  "dnp3_build_link_frame",
  "dnp3_parse_link_frame",
  "dnp3_build_class_scan",
  "dnp3_build_read_request",
  "dnp3_parse_application_response",
  "dnp3_build_confirm",
];

test("DNP3 commands cross the Rust/Electron bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of commands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("DNP3 page exposes only the first read-only master boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const combined = html + main;
  for (const marker of [
    'data-view="dnp3"',
    'id="dnp3-view"',
    "TCP 20000",
    "连接只读 Master",
    "完整性轮询 0/1/2/3",
    "Class 0",
    "只读 Class 扫描",
    "只读对象范围",
    "DNP3 离线 READ",
    "不生成控制、校时或管理功能码",
    "open_dnp3_connection",
    "dnp3_integrity_poll",
    "dnp3_class_scan",
    "dnp3_read",
  ]) {
    assert.match(combined, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
  for (const forbidden of [
    "dnp3_select",
    "dnp3_operate",
    "dnp3_direct_operate",
    "dnp3_time_sync",
    "dnp3_restart",
    "dnp3_freeze",
  ]) {
    assert.doesNotMatch(combined, new RegExp(forbidden), `${forbidden} must stay outside the UI`);
  }
});

