const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");

test("Xinje profile command crosses Rust/Electron bridge", () => {
  const command = "xinjie_parse_address";
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const source of [rustClient, preload, main]) assert.match(source, new RegExp(command));
});

test("Xinje page exposes XC/XD and the confirmed-D boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const guide = fs.readFileSync(path.join(root, "src", "protocol-guides.js"), "utf8");
  for (const marker of ["data-view=\"xinje\"", "id=\"xinje-view\"", "id=\"xinje-series\"", "xc-modbus", "xd-modbus", "仅 D 区确认", "其他区域", "fail-closed"]) {
    assert.match(html + main + guide, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
  assert.match(main, /xinjie_parse_address/);
});
