const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const commands = [
  "open_ads_connection",
  "ads_read",
  "ads_read_device_info",
  "ads_read_state",
  "ads_build_read",
  "ads_build_write",
  "ads_build_readwrite",
  "ads_build_read_device_info",
  "ads_build_read_state",
  "ads_parse_frame",
  "ads_parse_response",
];

test("Beckhoff ADS/AMS codec commands cross the Rust/Electron bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of commands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("Beckhoff page exposes the TCP read-only session boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  for (const marker of ["ads-host", "ads-open-connection", "ads-live-read", "TCP 48898", "TCP 只读会话 S4a", "open_ads_connection", "ads_read_device_info", "AMS Route"]) {
    assert.match(html + main, new RegExp(marker.replace(/[.*+?^${}()|[\\]\\]/g, "\\\\$&")), marker);
  }
});
