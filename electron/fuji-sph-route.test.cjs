const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");

test("Fuji SPH codec commands cross Rust/Electron bridge", () => {
  const commands = [
    "open_fuji_sph_connection",
    "fuji_sph_read",
    "fuji_sph_parse_address",
    "fuji_sph_build_read",
    "fuji_sph_build_write",
    "fuji_sph_parse_response",
  ];
  const sources = [
    fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8"),
    fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8"),
    fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8"),
  ];
  for (const command of commands) for (const source of sources) assert.match(source, new RegExp(command));
});

test("Fuji page exposes SPH binary framing and no live-control claim", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const guide = fs.readFileSync(path.join(root, "src", "protocol-guides.js"), "utf8");
  for (const marker of ["data-view=\"fuji\"", "id=\"fuji-view\"", "fuji-host", "fuji-open-connection", "fuji-live-read", "fuji-connection-id", "20 字节", "00H", "01H", "TCP 18245", "TCP 只读", "位写入"]) {
    assert.match(html + main + guide, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});
