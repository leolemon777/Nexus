"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const codecCommands = [
  "cjt188_parse_meter_type",
  "cjt188_parse_address",
  "cjt188_parse_data_id",
  "cjt188_build_read_request",
  "cjt188_parse_frame",
  "cjt188_parse_read_response",
];

test("CJ/T 188 commands cross the Rust/Electron offline codec bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of codecCommands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("CJ/T 188 page exposes only the offline read-only codec boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const guide = fs.readFileSync(path.join(root, "src", "protocol-guides.js"), "utf8");
  const combined = html + main + guide;
  for (const marker of [
    'data-view="cjt188"',
    'id="cjt188-view"',
    "CJ/T 188-2004",
    "单 68H",
    "算术和",
    "不做 +33H",
    "901F",
    "SER",
    "离线只读编解码",
    "cjt188_build_read_request",
    "cjt188_parse_read_response",
    "真实表计 L2 pending",
    "不提供 COM 发送入口",
  ]) {
    assert.match(combined, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
  for (const forbidden of [
    "cjt188_write",
    "cjt188_set_address",
    "cjt188_valve_control",
    "cjt188_read_follow",
    "cjt188_read_address",
    "cjt188_serial_read",
  ]) {
    assert.doesNotMatch(html + main, new RegExp(forbidden), `${forbidden} must stay outside the UI`);
  }
});
