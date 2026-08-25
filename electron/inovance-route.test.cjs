const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");

test("Inovance profile command crosses Rust/Electron bridge", () => {
  const command = "inovance_parse_address";
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const source of [rustClient, preload, main]) assert.match(source, new RegExp(command));
});

test("Inovance page exposes H3U/H5U and explicit AM boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const guide = fs.readFileSync(path.join(root, "src", "protocol-guides.js"), "utf8");
  for (const marker of ["data-view=\"inovance\"", "id=\"inovance-view\"", "id=\"inovance-series\"", "h3u-modbus", "h5u-modbus", "AM/AC/Easy"]) {
    assert.match(html + main + guide, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
  assert.match(main, /inovance_parse_address/);
});
