import { test } from "node:test";
import assert from "node:assert/strict";
import { buildPollPlan, splitBatchResult } from "../src/poll-planner.js";

// 工具: 构造一个寄存器点位(FC03)
const hr = (address, quantity, extra = {}) => ({
  unitId: 1,
  fc: 3,
  address,
  quantity,
  ...extra,
});

test("空点位数组返回空计划", () => {
  assert.deepEqual(buildPollPlan([]), []);
});

test("单点位原样返回为一批", () => {
  const batches = buildPollPlan([hr(0, 2)]);
  assert.equal(batches.length, 1);
  assert.deepEqual(batches[0], {
    unitId: 1,
    fc: 3,
    startAddress: 0,
    quantity: 2,
    pointIndexes: [0],
  });
});

test("连续地址合并为一批(8 个连续点位 -> 1 批)", () => {
  const points = [
    hr(0, 1), hr(1, 1), hr(2, 1), hr(3, 1),
    hr(4, 1), hr(5, 1), hr(6, 1), hr(7, 1),
  ];
  const batches = buildPollPlan(points);
  assert.equal(batches.length, 1);
  assert.equal(batches[0].startAddress, 0);
  assert.equal(batches[0].quantity, 8);
  assert.deepEqual(batches[0].pointIndexes, [0, 1, 2, 3, 4, 5, 6, 7]);
});

test("间隙超过 maxGap 时拆批(默认 maxGap=0)", () => {
  // [0,2) 与 [5,8) 间隙 3 > 0 -> 两批
  const batches = buildPollPlan([hr(0, 2), hr(5, 3)]);
  assert.equal(batches.length, 2);
  assert.deepEqual(batches[0], { unitId: 1, fc: 3, startAddress: 0, quantity: 2, pointIndexes: [0] });
  assert.deepEqual(batches[1], { unitId: 1, fc: 3, startAddress: 5, quantity: 3, pointIndexes: [1] });
});

test("maxGap 允许合并带间隙的相邻区间", () => {
  // [0,2) 与 [4,6): 间隙 2, maxGap=2 -> 合并为 [0,6) quantity=6
  const batches = buildPollPlan([hr(0, 2), hr(4, 2)], { maxGap: 2 });
  assert.equal(batches.length, 1);
  assert.equal(batches[0].startAddress, 0);
  assert.equal(batches[0].quantity, 6);
  assert.deepEqual(batches[0].pointIndexes, [0, 1]);
});

test("不同 unitId 不合并", () => {
  const batches = buildPollPlan([hr(0, 2, { unitId: 1 }), hr(2, 2, { unitId: 2 })]);
  assert.equal(batches.length, 2);
  assert.equal(batches[0].unitId, 1);
  assert.equal(batches[1].unitId, 2);
});

test("不同 FC 不合并", () => {
  // FC03 [0,2) 与 FC04 [2,2) 即使地址连续也分批
  const batches = buildPollPlan([hr(0, 2), hr(2, 2, { fc: 4 })]);
  assert.equal(batches.length, 2);
  assert.equal(batches[0].fc, 3);
  assert.equal(batches[1].fc, 4);
});

test("FC03/04 寄存器数量超过 125 拆批", () => {
  // 3 个连续寄存器点位合计 180 > 125 -> 必须拆批
  // 贪心合并: [0,60)+[60,120) 合并为 [0,120)(≤125); [120,180) 因再加入会超 cap 而单独成批
  const points = [hr(0, 60), hr(60, 60), hr(120, 60)];
  const batches = buildPollPlan(points);
  assert.equal(batches.length, 2);
  // 每批 quantity 均不超过 125
  assert.ok(batches.every((b) => b.quantity <= 125), "寄存器批次不得超过 125");
  // 批次区间无重叠且按地址升序
  assert.equal(batches[0].startAddress, 0);
  assert.equal(batches[0].quantity, 120);
  assert.deepEqual(batches[0].pointIndexes, [0, 1]);
  assert.equal(batches[1].startAddress, 120);
  assert.equal(batches[1].quantity, 60);
  assert.deepEqual(batches[1].pointIndexes, [2]);
  // 三个点位都被覆盖到
  const allIdx = new Set(batches.flatMap((b) => b.pointIndexes));
  assert.deepEqual([...allIdx].sort(), [0, 1, 2]);
});

test("单个点位 quantity > 125 也会被拆批", () => {
  const batches = buildPollPlan([hr(0, 200)]);
  assert.equal(batches.length, 2);
  assert.equal(batches[0].quantity, 125);
  assert.equal(batches[1].quantity, 75);
  // 同一点位出现在两批
  assert.deepEqual(batches[0].pointIndexes, [0]);
  assert.deepEqual(batches[1].pointIndexes, [0]);
});

test("FC01/02 线圈数量上限为 2000", () => {
  // 2001 个连续线圈 -> 拆成 2000 + 1
  const points = [{ unitId: 1, fc: 1, address: 0, quantity: 2001 }];
  const batches = buildPollPlan(points);
  assert.equal(batches.length, 2);
  assert.equal(batches[0].quantity, 2000);
  assert.equal(batches[1].quantity, 1);
  assert.ok(batches.every((b) => b.quantity <= 2000));
});

test("FC02 离散输入同样遵循 2000 上限", () => {
  const points = [{ unitId: 1, fc: 2, address: 0, quantity: 4000 }];
  const batches = buildPollPlan(points);
  assert.equal(batches.length, 2);
  assert.ok(batches.every((b) => b.quantity <= 2000));
});

test("写功能码 5/6/15/16 被忽略(不参与轮询)", () => {
  const points = [
    hr(0, 2), // FC03, 合法
    { unitId: 1, fc: 5, address: 0, quantity: 1 }, // 写单线圈
    { unitId: 1, fc: 6, address: 0, quantity: 1 }, // 写单寄存器
    { unitId: 1, fc: 15, address: 0, quantity: 1 }, // 写多线圈
    { unitId: 1, fc: 16, address: 0, quantity: 1 }, // 写多寄存器
    hr(2, 2), // FC03, 与第一个点位合并
  ];
  const batches = buildPollPlan(points);
  assert.equal(batches.length, 1);
  assert.equal(batches[0].fc, 3);
  // pointIndexes 只包含 0 和 5(两个 HR 点位),写点位索引 1-4 被剔除
  assert.deepEqual(batches[0].pointIndexes, [0, 5]);
});

test("未排序的输入也能按地址合并(内部排序)", () => {
  // 故意打乱顺序,但三个点位地址真正连续:[0,2) [2,4) [4,5)
  const points = [hr(4, 1), hr(0, 2), hr(2, 2)];
  const batches = buildPollPlan(points);
  assert.equal(batches.length, 1);
  assert.equal(batches[0].startAddress, 0);
  assert.equal(batches[0].quantity, 5);
  // pointIndexes 按地址升序: 原 idx1(addr0), idx2(addr2), idx0(addr4)
  assert.deepEqual(batches[0].pointIndexes, [1, 2, 0]);
});

test("splitBatchResult 按地址偏移切回各点位", () => {
  // 一个批次 [0,10), 内含两个点位: idx0 [0,2), idx1 [5,3)
  const batch = { startAddress: 0, quantity: 10, pointIndexes: [0, 1] };
  const points = [hr(0, 2), hr(5, 3)];
  const values = [10, 20, 0, 0, 0, 30, 40, 50, 0, 0];
  const slices = splitBatchResult(batch, points, values);
  assert.ok(slices instanceof Map);
  assert.deepEqual(slices.get(0), [10, 20]);
  assert.deepEqual(slices.get(1), [30, 40, 50]);
});

test("splitBatchResult 处理间隙跳读(maxGap 合并的批次)", () => {
  // maxGap=5 时 [0,2) 与 [5,3) 合并为 [0,8), 中间 2..5 为填充
  const batches = buildPollPlan([hr(0, 2), hr(5, 3)], { maxGap: 5 });
  assert.equal(batches.length, 1);
  const batch = batches[0];
  const points = [hr(0, 2), hr(5, 3)];
  // values 长度 = quantity 8
  const values = [100, 200, 0, 0, 0, 300, 400, 500];
  const slices = splitBatchResult(batch, points, values);
  assert.deepEqual(slices.get(0), [100, 200]);
  assert.deepEqual(slices.get(1), [300, 400, 500]);
});

test("splitBatchResult 处理被 cap 拆批后的跨界点位", () => {
  // 单点位 quantity=200 拆成 [0,125) + [125,200)
  const batches = buildPollPlan([hr(0, 200)]);
  const points = [hr(0, 200)];
  // 模拟两次读取的返回值
  const firstValues = Array.from({ length: 125 }, (_, i) => i); // 0..124
  const secondValues = Array.from({ length: 75 }, (_, i) => 125 + i); // 125..199
  const s1 = splitBatchResult(batches[0], points, firstValues);
  assert.deepEqual(s1.get(0), firstValues);
  const s2 = splitBatchResult(batches[1], points, secondValues);
  assert.deepEqual(s2.get(0), secondValues);
});
