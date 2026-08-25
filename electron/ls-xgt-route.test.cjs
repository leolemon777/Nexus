const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const commands = [
  "open_ls_xgt_connection",
  "ls_xgt_read",
  "ls_xgt_read_continuous",
  "ls_xgt_parse_address",
  "ls_xgt_build_read",
  "ls_xgt_build_continuous_read",
  "ls_xgt_build_write",
  "ls_xgt_build_continuous_write",
  "ls_xgt_parse_response",
];

test("LS XGT FEnet commands cross the Rust/Electron bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of commands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("LS XGT page exposes the TCP read-only session boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  for (const marker of ["xgt-host", "xgt-open-connection", "xgt-live-read", "xgt-live-read-continuous", "TCP 2004", "TCP 只读", "InvokeId"]) {
    assert.match(html, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), `${marker} missing from XGT page`);
  }
  for (const marker of ["open_ls_xgt_connection", "ls_xgt_read", "ls_xgt_read_continuous", "xgtSessionId"]) {
    assert.match(main, new RegExp(marker), `${marker} missing from XGT UI controller`);
  }
});
