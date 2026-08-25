const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const commands = [
  "panasonic_parse_data_address",
  "panasonic_parse_contact_address",
  "panasonic_build_read",
  "panasonic_build_write",
  "panasonic_build_read_contact",
  "panasonic_build_write_contact",
  "panasonic_parse_response",
];

test("Panasonic MEWTOCOL commands cross the Rust/Electron bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of commands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("Panasonic shared COM read-only IPC route is exposed only through preload and main", () => {
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  assert.match(preload, /"panasonic_serial_read"/);
  assert.match(main, /nexus:panasonic_serial_read/);
});
