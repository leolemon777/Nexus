const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const commands = [
  "open_enip_connection",
  "enip_read_tag",
  "enip_build_register_session",
  "enip_build_unregister_session",
  "enip_build_read_tag",
  "enip_parse_frame",
  "enip_parse_cip_response",
];

test("Allen-Bradley explicit codec commands cross the Rust/Electron bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of commands) {
    assert.match(rustClient, new RegExp(`\\"${command}\\"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`\\"${command}\\"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`\\"${command}\\"`), `${command} missing from main IPC forwarding`);
  }
});

test("Allen-Bradley page exposes the TCP read-only session boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  for (const marker of ["ab-host", "ab-open-connection", "ab-live-read", "TCP 44818", "TCP 只读会话 S4a", "RegisterSession", "open_enip_connection", "enip_read_tag"]) {
    assert.match(html + main, new RegExp(marker.replace(/[.*+?^${}()|[\\]\\]/g, "\\\\$&")), marker);
  }
});
