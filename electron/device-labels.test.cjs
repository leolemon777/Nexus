/**
 * 三菱软元件序列标签测试 —— X/Y 八进制编号守卫。
 *
 * 背景(2026-08-25 真机,用户发现):读 Y1×20 时表格出现 Y8/Y9,但 FX3U 的 X/Y 是八进制编号
 * (Y0~Y7 之后是 Y10~Y17,Y8/Y9 不存在)。协议层编址一直正确,仅渲染标签按十进制递增错位。
 */

const test = require("node:test");
const assert = require("node:assert");

// src/device-labels.js 是 ESM,用动态 import 加载
async function load() {
  const mod = await import("../src/device-labels.js");
  return mod.formatDeviceSeries;
}

test("X/Y 按八进制递增:Y1×20 跨过 Y7 后是 Y10,不出现 Y8/Y9", async () => {
  const f = await load();
  const labels = f("y", "1", 20);
  assert.ok(labels);
  assert.deepStrictEqual(labels.slice(0, 10), ["Y1","Y2","Y3","Y4","Y5","Y6","Y7","Y10","Y11","Y12"]);
  assert.deepStrictEqual(labels.slice(10), ["Y13","Y14","Y15","Y16","Y17","Y20","Y21","Y22","Y23","Y24"]);
  assert.ok(!labels.some((l) => /^Y[89]$/.test(l)), "不得出现 Y8/Y9");
});

test("X 起始编号 10(八进制)继续递增", async () => {
  const f = await load();
  assert.deepStrictEqual(f("X", "10", 5), ["X10","X11","X12","X13","X14"]);
  assert.deepStrictEqual(f("X", "7", 3), ["X7","X10","X11"]);
});

test("D/M 等其余软元件按十进制递增", async () => {
  const f = await load();
  assert.deepStrictEqual(f("D", "0", 6), ["D0","D1","D2","D3","D4","D5"]);
  assert.deepStrictEqual(f("m", "8", 3), ["M8","M9","M10"]);
});

test("非法输入返回 null 而非抛错", async () => {
  const f = await load();
  assert.strictEqual(f("", "0", 3), null);
  assert.strictEqual(f("D", "abc", 3), null);
  assert.strictEqual(f("D", "-1", 3), null);
});
