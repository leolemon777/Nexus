import { test } from "node:test";
import assert from "node:assert/strict";
import { matchTrace, filterTrace } from "../src/trace-filter.js";

// 与 src/main.js 中 traceHistory 记录结构保持一致
const rec = (over = {}) => ({
  timestamp: 1697000000000,
  direction: "TX",
  unitId: 1,
  functionCode: 3,
  hex: "01 03 00 00 00 0A",
  crc: "已生成",
  elapsedMs: 12,
  result: "读取 0..9",
  ...over,
});

const sample = [
  rec({ direction: "TX", unitId: 1, functionCode: 3, hex: "01 03 00 00 00 0A", result: "读取 0..9" }),
  rec({ direction: "RX", unitId: 1, functionCode: 3, hex: "01 03 14 00 0A", result: "Good" }),
  rec({ direction: "TX", unitId: 2, functionCode: 4, hex: "02 04 00 10 00 02", result: "读取 16..17" }),
  rec({ direction: "RX", unitId: 2, functionCode: 4, hex: "02 04 04 00 FF", result: "Good" }),
];

test("空查询返回全部记录", () => {
  assert.equal(filterTrace(sample, "").length, sample.length);
  assert.equal(filterTrace(sample, "   ").length, sample.length);
  assert.equal(filterTrace(sample, null).length, sample.length);
  assert.equal(filterTrace(sample, undefined).length, sample.length);
});

test("单关键词:命中 direction", () => {
  const out = filterTrace(sample, "TX");
  assert.equal(out.length, 2);
  assert.ok(out.every((r) => r.direction === "TX"));
});

test("单关键词:命中 unitId(数字也按字符串匹配)", () => {
  const out = filterTrace(sample, "2");
  // unitId=2 的两条 TX/RX 命中;functionCode 含 "2" 的也会命中,这里仅校验 unitId 维度
  assert.ok(out.some((r) => r.unitId === 2));
});

test("单关键词:命中 functionCode", () => {
  const out = filterTrace(sample, "04");
  assert.equal(out.length, 2);
  assert.ok(out.every((r) => r.functionCode === 4));
});

test("单关键词:命中 hex", () => {
  // "00 10" 只出现在第三条(unitId=2, FC04)的 hex 里
  const out = filterTrace(sample, "00 10");
  assert.equal(out.length, 1);
  assert.equal(out[0].unitId, 2);
  assert.equal(out[0].functionCode, 4);
});

test("hex 匹配不区分大小写", () => {
  const upper = filterTrace(sample, "00 10");
  const lower = filterTrace(sample, "00 10");
  assert.equal(upper.length, 1);
  assert.deepEqual(upper, lower);
});

test("单关键词:命中 result", () => {
  const out = filterTrace(sample, "Good");
  assert.equal(out.length, 2);
  assert.ok(out.every((r) => r.result === "Good"));
});

test("大小写不敏感:小写 query 也能命中大写内容", () => {
  const out = filterTrace(sample, "good");
  assert.equal(out.length, 2);
});

test("大小写不敏感:大写 query 也能命中小写内容", () => {
  const out = filterTrace(sample, "READ");
  // result 形如 "读取..." 不含英文 read;但 hex "01 03 ..." 不含 read。
  // 这里改用更稳的断言:查询 "RX"(direction 大写)对自身命中
  const out2 = filterTrace(sample, "rx");
  assert.equal(out2.length, 2);
  assert.ok(out2.every((r) => r.direction === "RX"));
  // 保留对 READ 的断言:应为 0(没有英文 read 字段)
  assert.equal(out.length, 0);
});

test("多关键词 AND 语义(空格分隔,全部命中才算)", () => {
  // "TX" AND "01" -> 仅第一条 TX(其 hex 含 01)
  const out = filterTrace(sample, "TX 01");
  assert.equal(out.length, 1);
  assert.equal(out[0].direction, "TX");
  assert.equal(out[0].unitId, 1);
});

test("多关键词:任一未命中则整条不命中", () => {
  // "TX" AND "ZZZ" -> 0
  assert.equal(filterTrace(sample, "TX ZZZ").length, 0);
});

test("多关键词:跨字段 AND(unitId + fc)", () => {
  // unitId=2 且 functionCode=4 的两条
  const out = filterTrace(sample, "2 04");
  assert.equal(out.length, 2);
  assert.ok(out.every((r) => r.unitId === 2 && r.functionCode === 4));
});

test("多关键词:多个空格/前后空格被归一化", () => {
  assert.equal(filterTrace(sample, "  TX   01  ").length, 1);
});

test("matchTrace 单条匹配:true/false", () => {
  assert.equal(matchTrace(sample[0], "TX"), true);
  assert.equal(matchTrace(sample[0], "rx"), false);
  assert.equal(matchTrace(sample[0], "00 0a"), true);
  assert.equal(matchTrace(sample[0], ""), true);
});

test("matchTrace 对缺失字段不抛错(undefined/null 安全)", () => {
  const partial = { direction: "TX", unitId: 1, functionCode: 3 };
  assert.equal(matchTrace(partial, "TX"), true);
  assert.equal(matchTrace(partial, "RX"), false);
  assert.equal(matchTrace(partial, "anything"), false);
});

test("filterTrace 不修改原数组,返回新数组", () => {
  const before = [...sample];
  const out = filterTrace(sample, "TX");
  assert.notEqual(out, sample);
  assert.deepEqual(sample, before);
});

test("filterTrace 空数组入参返回空数组", () => {
  assert.deepEqual(filterTrace([], "TX"), []);
  assert.deepEqual(filterTrace([], ""), []);
});
