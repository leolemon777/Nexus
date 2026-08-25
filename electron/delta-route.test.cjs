const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");

test("Delta address profile command crosses Rust/Electron bridge", () => {
  const command = "delta_parse_address";
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  assert.match(rustClient, new RegExp(`"${command}"`));
  assert.match(preload, new RegExp(`"${command}"`));
  assert.match(main, new RegExp(`"${command}"`));
});

test("Delta readonly Modbus service is exposed through main IPC and preload", () => {
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of ["delta_modbus_plan", "delta_modbus_read"]) {
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`nexus:${command}`), `${command} missing from main IPC`);
  }
  assert.match(main, /createDeltaModbusService/);
  assert.match(main, /readHoldingRegistersOnce/);
  assert.match(main, /readInputRegistersOnce/);
  assert.match(main, /readCoilsOnce/);
  assert.match(main, /readDiscreteInputsOnce/);
});

test("Delta page and guide expose both DVP and AS profiles", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const guide = fs.readFileSync(path.join(root, "src", "protocol-guides.js"), "utf8");
  for (const marker of ["data-view=\"delta\"", "id=\"delta-view\"", "id=\"delta-series\"", "dvp-modbus", "as-modbus"]) assert.match(html, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  assert.match(main, /delta_parse_address/);
  assert.match(main, /delta_modbus_plan/);
  assert.match(main, /delta_modbus_read/);
  assert.match(guide, /DVP\/ES\/EX\/SS/);
  assert.match(guide, /AS300\/DVP-ES3/);
});
