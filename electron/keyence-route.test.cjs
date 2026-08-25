const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const commands = [
  "open_keyence_connection",
  "keyence_read_words",
  "keyence_read_bits",
  "keyence_parse_address",
  "keyence_build_connect",
  "keyence_build_read_words",
  "keyence_build_read_bits",
  "keyence_build_write_words",
  "keyence_build_write_bit",
  "keyence_parse_connect",
  "keyence_parse_words",
  "keyence_parse_bits",
  "keyence_parse_write",
];

test("Keyence KV Host Link commands cross the Rust/Electron bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of commands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("Keyence page exposes the TCP read-only session boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  for (const marker of ["keyence-host", "keyence-open-connection", "keyence-live-read-words", "keyence-live-read-bits", "TCP 8501", "TCP 只读", "WRS", "ST/RS"]) {
    assert.match(html, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), `${marker} missing from Keyence page`);
  }
  for (const marker of ["open_keyence_connection", "keyence_read_words", "keyence_read_bits", "keyenceSessionId"]) {
    assert.match(main, new RegExp(marker), `${marker} missing from Keyence UI controller`);
  }
});
