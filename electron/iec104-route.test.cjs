const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const commands = [
  "open_iec104_connection",
  "iec104_general_interrogation",
  "iec104_test_frame",
  "iec104_build_i_frame",
  "iec104_build_s_frame",
  "iec104_build_u_frame",
  "iec104_parse_apdu",
  "iec104_build_general_interrogation",
  "iec104_parse_asdu",
];

test("IEC104 commands cross the Rust/Electron bridge", () => {
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  for (const command of commands) {
    assert.match(rustClient, new RegExp(`"${command}"`), `${command} missing from COMMANDS`);
    assert.match(preload, new RegExp(`"${command}"`), `${command} missing from preload allow-list`);
    assert.match(main, new RegExp(`"${command}"`), `${command} missing from main IPC forwarding`);
  }
});

test("IEC104 page exposes only the first read-only master boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const combined = html + main;
  for (const marker of [
    'data-view="iec104"',
    'id="iec104-view"',
    "TCP 2404",
    "连接并 STARTDT",
    "只读总召",
    "TESTFR",
    "STOPDT",
    "ACT_TERM",
    "M_SP_NA_1",
    "M_ME_NC_1",
    "遥控、设点、校时",
    "open_iec104_connection",
    "iec104_general_interrogation",
  ]) {
    assert.match(combined, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
  for (const forbidden of ["iec104_single_command", "iec104_double_command", "iec104_setpoint", "iec104_clock_sync"]) {
    assert.doesNotMatch(combined, new RegExp(forbidden), `${forbidden} must stay outside the UI`);
  }
});
