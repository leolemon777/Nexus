const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.join(__dirname, "..");

test("GE SRTP commands are routed through Rust and preload", () => {
  const protocol = fs.readFileSync(path.join(root, "rust-core", "src", "protocol.rs"), "utf8");
  const preload = fs.readFileSync(path.join(__dirname, "preload.cjs"), "utf8");
  const main = fs.readFileSync(path.join(__dirname, "main.cjs"), "utf8");
  for (const command of [
    "open_ge_srtp_connection",
    "ge_srtp_read",
    "ge_srtp_parse_address",
    "ge_srtp_build_handshake",
    "ge_srtp_parse_handshake",
    "ge_srtp_build_read",
    "ge_srtp_build_write",
    "ge_srtp_parse_response",
  ]) {
    assert.match(protocol, new RegExp(`\\"${command}\\"`), command);
    assert.match(preload, new RegExp(`\\"${command}\\"`), command);
    assert.match(main, new RegExp(`\\"${command}\\"`), command);
  }
});

test("GE SRTP page exposes the audited binary boundary", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  for (const marker of [
    'data-view="ge"',
    'id="ge-view"',
    "56 字节",
    "18245",
    "R/AI/AQ",
    "位访问",
    "TCP 只读连接",
    "TCP 只读读取",
    "离线编解码",
  ]) assert.match(html, new RegExp(marker.replace(/[.*+?^${}()|[\\]\\]/g, "\\$&")), marker);
  for (const marker of ["initGeUi", "open_ge_srtp_connection", "ge_srtp_read", "ge_srtp_build_read", "ge_srtp_parse_response"]) {
    assert.match(main, new RegExp(marker), marker);
  }
});
