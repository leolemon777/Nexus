/**
 * src/serial-plot.js 单元测试(纯逻辑层: SeriesStore / 手动规则 / 寄存器配对 / CSV 行)。
 * 绘制函数依赖 DOM/Canvas,由 scripts/audit-ui-layout.cjs 与桌面冒烟覆盖,不在本文件测。
 */
const test = require("node:test");
const assert = require("node:assert/strict");

const modPromise = import("../src/serial-plot.js");

test("SeriesStore: add/has/remove/clear 与重复 key 拒绝", async () => {
  const { SeriesStore } = await modPromise;
  const store = new SeriesStore();
  assert.equal(store.add("a", { name: "A" }), true);
  assert.equal(store.add("a", { name: "A2" }), false); // 重复 key
  assert.equal(store.has("a"), true);
  assert.equal(store.get("a").name, "A");
  assert.equal(store.remove("a"), true);
  assert.equal(store.remove("a"), false);
  store.add("b", { name: "B" });
  store.clear();
  assert.equal(store.size, 0);
});

test("SeriesStore: feed 裁剪时间窗与点数上限,暂停不阻断数据入环", async () => {
  const { SeriesStore } = await modPromise;
  const store = new SeriesStore({ windowMs: 1000, maxPoints: 2 });
  store.add("a", { name: "A" });
  assert.equal(store.feed("a", 100, 1), true);
  store.feed("a", 150, 2);
  store.feed("a", 3000, 3); // cutoff = 3000-1000-2000 = 0, t=100 被裁;maxPoints=2 再裁到 2 点
  const points = store.get("a").dataPoints;
  assert.equal(points.length, 2);
  assert.equal(points[0].t, 150);
  assert.equal(points[1].v, 3);
  store.paused = true; // 暂停只冻结绘制
  store.feed("a", 3100, 4);
  assert.equal(store.get("a").dataPoints.length, 2); // 上限 2,推入同时裁掉最老点
  assert.equal(store.get("a").dataPoints[1].v, 4);
  assert.equal(store.feed("a", 4000, "abc"), false); // 非数值忽略
  assert.equal(store.feed("missing", 4000, 1), false); // 未知通道忽略
});

test("parseHeadHex: 空/正常/非法输入", async () => {
  const { parseHeadHex } = await modPromise;
  assert.equal(parseHeadHex(""), null);
  assert.equal(parseHeadHex("   "), null);
  assert.equal(parseHeadHex(null), null);
  assert.deepEqual(parseHeadHex("01 03"), [0x01, 0x03]);
  assert.deepEqual(parseHeadHex("0x01,0x03"), [0x01, 0x03]);
  assert.throws(() => parseHeadHex("GG"), /非法帧头字节/);
  assert.throws(() => parseHeadHex("0100 +"), /非法帧头字节/);
});

test("evalManualRule: 帧头过滤 + 各类型/字节序/缩放", async () => {
  const { evalManualRule } = await modPromise;
  const frame = [0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD];
  // u16 大端 @3
  assert.equal(evalManualRule({ head: [0x01, 0x03], offset: 3, type: "u16", order: "be", scale: 1 }, frame), 0x1234);
  // u16 小端 @3
  assert.equal(evalManualRule({ head: null, offset: 3, type: "u16", order: "le", scale: 1 }, frame), 0x3412);
  // 帧头不匹配 → null
  assert.equal(evalManualRule({ head: [0x02, 0x03], offset: 3, type: "u16", order: "be", scale: 1 }, frame), null);
  // 偏移+长度越界 → null
  assert.equal(evalManualRule({ head: null, offset: 6, type: "u16", order: "be", scale: 1 }, frame), null);
  // u8
  assert.equal(evalManualRule({ head: null, offset: 3, type: "u8", order: "be", scale: 1 }, frame), 0x12);
  // i16 负值: 帧尾 AB CD → 0xABCD = -21555
  assert.equal(evalManualRule({ head: null, offset: 5, type: "i16", order: "be", scale: 1 }, frame), 0xabcd - 0x10000);
  // i16 小端 @4: 34 AB → 0xAB34,最高位为 1 → 有符号值 -21708
  assert.equal(evalManualRule({ head: null, offset: 4, type: "i16", order: "le", scale: 1 }, frame), 0xab34 - 0x10000);
  // u32 大端 @2 = 0x041234AB
  assert.equal(evalManualRule({ head: null, offset: 2, type: "u32", order: "be", scale: 1 }, frame), 0x041234ab);
  // i32 小端 @0: bytes[0..3]=01 03 04 12 → 0x12040301
  assert.equal(evalManualRule({ head: null, offset: 0, type: "i32", order: "le", scale: 1 }, frame), 0x12040301);
  // f32 小端 1.5
  assert.equal(evalManualRule({ head: null, offset: 0, type: "f32", order: "le", scale: 1 }, [0x00, 0x00, 0xc0, 0x3f]), 1.5);
  // f32 大端 1.5
  assert.equal(evalManualRule({ head: null, offset: 0, type: "f32", order: "be", scale: 1 }, [0x3f, 0xc0, 0x00, 0x00]), 1.5);
  // 缩放: 0x1234=4660, ×0.1(浮点结果与 4660*0.1 完全一致)
  assert.equal(evalManualRule({ head: null, offset: 3, type: "u16", order: "be", scale: 0.1 }, frame), 4660 * 0.1);
  // 未知类型 → null
  assert.equal(evalManualRule({ head: null, offset: 0, type: "f64", order: "be", scale: 1 }, frame), null);
});

test("pairRegisterChannels: TX/RX 配对出 HR/IR 通道,无配对退化为偏移名", async () => {
  const { pairRegisterChannels } = await modPromise;
  const tx = { isValid: true, direction: "request", baseFunctionCode: 3, address: 0, quantity: 2, unitId: 1 };
  const rx = { isValid: true, isException: false, baseFunctionCode: 3, registers: [10, 20], unitId: 1 };
  assert.deepEqual(pairRegisterChannels(tx, rx), [
    { key: "reg:HR:0", name: "HR[0]", value: 10 },
    { key: "reg:HR:1", name: "HR[1]", value: 20 },
  ]);
  // FC04 → IR,起始地址取自 TX(registers 两个值 → 两条通道)
  const tx4 = { ...tx, baseFunctionCode: 4, address: 5 };
  const rx4 = { ...rx, baseFunctionCode: 4 };
  assert.deepEqual(pairRegisterChannels(tx4, rx4), [
    { key: "reg:IR:5", name: "IR[5]", value: 10 },
    { key: "reg:IR:6", name: "IR[6]", value: 20 },
  ]);
  // 无 TX 配对 → 响应内偏移名
  assert.deepEqual(pairRegisterChannels(null, rx), [
    { key: "reg:HR+0", name: "HR+0", value: 10 },
    { key: "reg:HR+1", name: "HR+1", value: 20 },
  ]);
  // 站号不一致 → 不配对
  const txWrongUnit = { ...tx, unitId: 2 };
  assert.equal(pairRegisterChannels(txWrongUnit, rx)[0].key, "reg:HR+0");
  // 异常/无效/无寄存器 → 空
  assert.deepEqual(pairRegisterChannels(tx, { ...rx, isException: true }), []);
  assert.deepEqual(pairRegisterChannels(tx, { ...rx, isValid: false }), []);
  assert.deepEqual(pairRegisterChannels(tx, { ...rx, registers: [] }), []);
});

test("buildCsvRows: 按时间合并通道,稀疏单元格留空,单位进列名", async () => {
  const { SeriesStore, buildCsvRows } = await modPromise;
  const store = new SeriesStore();
  assert.deepEqual(buildCsvRows(store), []); // 空存储
  store.add("a", { name: "温度", unit: "℃" });
  store.add("b", { name: "压力" });
  store.feed("a", 1700000000000, 25.1);
  store.feed("b", 1700000000000, 3);
  store.feed("a", 1700000000500, 25.2);
  const rows = buildCsvRows(store);
  assert.equal(rows.length, 2);
  assert.equal(rows[0].time, new Date(1700000000000).toISOString());
  assert.equal(rows[0]["温度(℃)"], 25.1);
  assert.equal(rows[0]["压力"], 3);
  assert.equal(rows[1]["温度(℃)"], 25.2);
  assert.equal(rows[1]["压力"], "");
});

test("buildCsvRows: 重名列追加通道 key 去重", async () => {
  const { SeriesStore, buildCsvRows } = await modPromise;
  const store = new SeriesStore();
  store.add("m:a", { name: "V", rule: {} });
  store.add("m:b", { name: "V", rule: {} });
  store.feed("m:a", 1000, 1);
  store.feed("m:b", 1000, 2);
  const [row] = buildCsvRows(store);
  assert.equal(row["V"], 1);
  assert.equal(row["V#m:b"], 2);
});

test("computeYRange: 自动量程(8% padding/退化/空)与手动覆盖(B.10)", async () => {
  const { SeriesStore, computeYRange } = await modPromise;
  const store = new SeriesStore({ windowMs: 60_000 });
  store.add("a", { name: "A" });

  // 空 → 0..1 再加 8% padding(与原绘制行为一致)
  assert.deepEqual(computeYRange(store, null, 1000), { min: -0.08, max: 1.08 });

  // 窗口内 10..20 → padding 8% = 0.8
  store.feed("a", 2000, 10);
  store.feed("a", 3000, 20);
  assert.deepEqual(computeYRange(store, null, 1500), { min: 9.2, max: 20.8 });
  // 窗口外点被忽略(1500 起点,2000 在内;另一窗口起点 2500 时 2000 被排除)
  const only = computeYRange(store, null, 2500); // 只剩单点 20 → 19..21 → padding
  assert.equal(only.min, 18.84);
  assert.equal(only.max, 21.16);

  // 退化 min==max → ±1 再 padding
  const flat = new SeriesStore();
  flat.add("a", { name: "A" });
  flat.feed("a", 1000, 5);
  assert.deepEqual(computeYRange(flat, null, 0), { min: 3.84, max: 6.16 });

  // 手动覆盖:精确值、不加 padding
  assert.deepEqual(computeYRange(store, { min: 0, max: 100 }, 1500), { min: 0, max: 100 });
  // 非法覆盖回退自动:min>=max / NaN / Infinity / null
  assert.deepEqual(computeYRange(store, { min: 100, max: 0 }, 1500), { min: 9.2, max: 20.8 });
  assert.deepEqual(computeYRange(store, { min: Number.NaN, max: 10 }, 1500), { min: 9.2, max: 20.8 });
  assert.deepEqual(
    computeYRange(store, { min: Number.NEGATIVE_INFINITY, max: Number.POSITIVE_INFINITY }, 1500),
    { min: 9.2, max: 20.8 },
  );
  assert.deepEqual(computeYRange(store, null, 1500), { min: 9.2, max: 20.8 });
});
