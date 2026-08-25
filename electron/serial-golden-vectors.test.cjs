const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const doc = fs.readFileSync(
  path.join(__dirname, "..", "docs", "serial-golden-vectors.md"),
  "utf8",
);

test("serial golden-vector document keeps all software-first protocol boundaries explicit", () => {
  for (const marker of [
    "PPI 原生 COM",
    "USS（参数读取只读首轮）",
    "3964R / RK512",
    "Omron HostLink C-mode",
    "Omron HostLink FINS",
    "L2 记录",
    "首轮不写输出",
  ]) {
    assert.match(doc, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), marker);
  }
});

test("software golden vectors contain the exact frame boundaries used by the codecs", () => {
  for (const frame of [
    "68 1B 1B 68 02 00 6C",
    "10 02 00 5C 5E 16",
    "02 09 01 12 BC 00 00 00 00 00 00 A4",
    "02 09 81 1B BC 00 00 00 06 00 00 2B",
    "02 01 00 02 00 01 00 00 00 00 10 03 11",
    "@00RR0064000240*\\r\\n",
    "@00FA8000020000000000000101010182000064000247*\\r\\n",
  ]) {
    assert.match(doc, new RegExp(frame.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), frame);
  }
});
