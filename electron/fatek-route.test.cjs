const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");

test("FATEK codec commands cross Rust/Electron bridge", () => {
  const commands = [
    "open_fatek_connection", "fatek_read_words", "fatek_read_discrete",
    "fatek_parse_address", "fatek_pack_command", "fatek_build_read_discrete",
    "fatek_build_write_discrete", "fatek_build_read_words", "fatek_build_write_words",
    "fatek_parse_response",
  ];
  const sources = [
    fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8"),
    fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8"),
    fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8"),
  ];
  for (const command of commands) for (const source of sources) assert.match(source, new RegExp(command));
});

test("FATEK page exposes ASCII framing and read-only boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const guide = fs.readFileSync(path.join(root, "src", "protocol-guides.js"), "utf8");
  for (const marker of ["data-view=\"fatek\"", "id=\"fatek-view\"", "fatek-host", "fatek-open-connection", "fatek-live-read", "fatek-station", "STX", "ETX", "TCP 5000", "TCP 只读", "RUN/STOP"]) {
    assert.match(html + main + guide, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});
