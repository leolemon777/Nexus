"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const codecCommands = [
  "bacnet_ip_build_whois",
  "bacnet_ip_build_iam",
  "bacnet_ip_parse_frame",
  "bacnet_ip_build_read_property_request",
  "bacnet_ip_parse_read_property_request",
  "bacnet_ip_parse_read_property_ack",
  "open_bacnet_ip_connection",
  "bacnet_ip_whois",
  "bacnet_ip_read_property_live",
];

test("BACnet/IP commands cross the Rust/Electron offline codec bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of codecCommands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("BACnet/IP page exposes only offline codec boundaries", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const guide = fs.readFileSync(path.join(root, "src", "protocol-guides.js"), "utf8");
  const combined = html + main + guide;
  for (const marker of [
    'data-view="bacnet"',
    'id="bacnet-view"',
    "BACnet/IP",
    "Who-Is",
    "I-Am",
    "81H",
    "0AH/0BH",
    "01 00",
    "10H",
    "UDP 47808",
    "不打开 UDP",
    "UDP 只读会话",
    "0AH 定向单播",
    "C4",
    "ReadProperty",
    "ComplexACK",
    "bacnet_ip_build_whois",
    "bacnet_ip_build_iam",
    "bacnet_ip_parse_frame",
    "open_bacnet_ip_connection",
    "bacnet_ip_whois",
    "bacnet_ip_read_property_live",
    "实机 L2 pending",
  ]) {
    assert.match(combined, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
  for (const forbidden of [
    "bacnet_ip_discover",
    'bacnet_ip_read_property"',
    "bacnet_ip_read_property_multiple",
    "bacnet_ip_write_property",
    "bacnet_ip_subscribe_cov",
    "bacnet_ip_register_foreign_device",
  ]) {
    assert.doesNotMatch(html + main, new RegExp(forbidden), `${forbidden} must stay outside the UI`);
  }
});
