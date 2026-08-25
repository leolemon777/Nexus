const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(
  path.join(__dirname, "..", "docs", "fetchwrite-golden-vectors.md"),
  "utf8",
);

test("Fetch/Write golden document keeps fixed-header and port boundaries explicit", () => {
  assert.match(doc, /不能把 S7comm 的 102 端口当作 Fetch\/Write/);
  assert.match(doc, /`05` Fetch 请求、`06` Fetch 成功响应、`03` Write 请求、`04` Write 成功响应/);
  assert.match(doc, /真实 CP 返回码、连接资源、断线恢复和 8\/24\/72 小时长稳仍需 L2/);
});

test("Fetch/Write golden document contains exact request and response vectors", () => {
  for (const frame of [
    "53 35 10 01 03 05 03 08 01 01 00 00 00 02 FF 02",
    "53 35 10 01 03 06 03 08 00 01 00 00 00 02 FF 02 AA BB",
    "53 35 10 01 03 03 03 08 02 00 00 32 00 02 FF 02 CA FE",
    "53 35 10 01 03 04 03 08 00 00 00 32 00 02 FF 02",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
