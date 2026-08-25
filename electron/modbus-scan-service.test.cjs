"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");

const {
  createModbusScanService,
  DEFAULT_SCAN_BAUDS,
  DEFAULT_SCAN_PARITIES,
} = require("./modbus-scan-service.cjs");

/**
 * harness:stub serialService(记录 open/close,可控制初始状态与 open 成败)
 * 与 readHoldingRegistersOnce(按 onlineStations 集合决定应答)。
 * probes 记录每次探测的 (baudRate 来自当前 stub 状态, unitId)。
 */
function createHarness({ onlineStations = [], onlineAt = [], wasOpen = false, originalConfig = null, openFails = false } = {}) {
  const calls = [];
  // 初始串口状态跟随 wasOpen。
  let currentConfig = wasOpen ? { ...(originalConfig ?? { portName: "COM3", baudRate: 9600, parity: "none" }) } : null;
  const serialService = {
    getStatus() {
      return { isOpen: currentConfig != null, config: currentConfig ?? originalConfig };
    },
    async open(config) {
      if (openFails) throw new Error("open failed");
      currentConfig = { ...config };
      calls.push({ step: "open", baudRate: config.baudRate, parity: config.parity, config });
    },
    async close() {
      currentConfig = null;
      calls.push({ step: "close" });
    },
  };
  const probes = [];
  const readHoldingRegistersOnce = async (_ctx, args) => {
    const probe = { unitId: args.unitId, baudRate: currentConfig?.baudRate, parity: currentConfig?.parity };
    probes.push(probe);
    const exactMatch = onlineAt.some((entry) => entry.unitId === probe.unitId
      && entry.baudRate === probe.baudRate && entry.parity === probe.parity);
    if (exactMatch || (onlineAt.length === 0 && onlineStations.includes(args.unitId))) {
      return { ok: true, elapsedMs: 5, registers: [0] };
    }
    return { ok: false, error: { code: "SERIAL_RESPONSE_TIMEOUT" } };
  };
  const service = createModbusScanService({ serialService, readHoldingRegistersOnce });
  return { calls, probes, service, ctx: { serialService, readHoldingRegistersOnce } };
}

const openCalls = (calls) => calls.filter((c) => c.step === "open");
const openCombos = (calls) => openCalls(calls).map((c) => `${c.baudRate}/${c.parity}`);

test("默认档位表与扫描顺序:常见波特率在前,同波特率按 8N1→8E1→8O1", async () => {
  assert.deepEqual(DEFAULT_SCAN_BAUDS, [9600, 19200, 38400, 115200, 4800]);
  assert.deepEqual(DEFAULT_SCAN_PARITIES, ["none", "even", "odd"]);
  const harness = createHarness(); // 全离线
  const result = await harness.service.scanAll(harness.ctx, {
    comPort: "COM3",
    bauds: [9600, 19200],
    parities: ["none", "even"],
    stationStart: 1,
    stationEnd: 2,
    timeoutMs: 10,
  });
  assert.deepEqual(openCombos(harness.calls), ["9600/none", "9600/even", "19200/none", "19200/even"]);
  assert.equal(result.ok, true);
  assert.equal(result.found, false);
  assert.equal(result.triedCombos, 4);
  assert.deepEqual(harness.probes.map((p) => p.unitId), [1, 2, 1, 2, 1, 2, 1, 2]);
  assert.equal(result.triedProbes, 8);
});

test("firstHit:任一站号命中即停", async () => {
  const harness = createHarness({ onlineStations: [1], wasOpen: true, originalConfig: { portName: "COM3", baudRate: 57600 } });
  const result = await harness.service.scanAll(harness.ctx, {
    comPort: "COM3",
    bauds: [9600, 19200],
    parities: ["none", "even"],
    timeoutMs: 10,
  });
  assert.equal(result.found, true);
  assert.equal(result.hits.length, 1);
  assert.equal(result.hits[0].stationId, 1);
  assert.equal(result.hits[0].baudRate, 9600);
  assert.equal(result.hits[0].parity, "none");
  assert.equal(result.hits[0].parityLabel, "8N1");
  assert.equal(result.hits[0].firstResponseMs, 5);
  // 探测 open 只有一次(9600/none);恢复时另有一次 open(原配置 57600)
  const opens = openCalls(harness.calls);
  assert.equal(opens.length, 2);
  assert.equal(`${opens[0].baudRate}/${opens[0].parity}`, "9600/none");
  assert.equal(opens[1].baudRate, 57600);
  // 恢复动作记录在返回值 trace
  const restore = result.trace.find((c) => c.step === "restore");
  assert.equal(restore.reopened, true);
  assert.equal(result.cancelled, false);
});

test("full 模式:每个档位扫描全部站号并收集命中", async () => {
  const harness = createHarness({ onlineStations: [1, 3] });
  const result = await harness.service.scanAll(harness.ctx, {
    comPort: "COM3",
    bauds: [9600],
    parities: ["none"],
    stationStart: 1,
    stationEnd: 3,
    timeoutMs: 10,
    mode: "full",
  });
  assert.equal(result.found, true);
  assert.deepEqual(result.hits.map((h) => h.stationId), [1, 3]);
  // 首探(1) + 剩余站 2、3
  assert.deepEqual(harness.probes.map((p) => p.unitId), [1, 2, 3]);
});

for (const stationId of [5, 16]) {
  test(`firstHit:前置站号离线时仍能命中站号 ${stationId}`, async () => {
    const harness = createHarness({
      onlineAt: [{ unitId: stationId, baudRate: 19200, parity: "even" }],
    });
    const result = await harness.service.scanAll(harness.ctx, {
      comPort: "COM3",
      bauds: [9600, 19200],
      parities: ["none", "even"],
      stationStart: 1,
      stationEnd: 16,
      timeoutMs: 10,
      mode: "firstHit",
    });
    assert.equal(result.found, true);
    assert.equal(result.hit.stationId, stationId);
    assert.equal(result.hit.baudRate, 19200);
    assert.equal(result.hit.parity, "even");
    assert.deepEqual(
      harness.probes.slice(-stationId).map((p) => p.unitId),
      Array.from({ length: stationId }, (_v, i) => i + 1),
    );
  });
}

test("取消:onProgress 触发 requestCancel 后在档间退出并恢复原状态", async () => {
  const harness = createHarness({ wasOpen: false });
  let cancelledOnce = false;
  const progressEvents = [];
  const result = await harness.service.scanAll(
    harness.ctx,
    { comPort: "COM3", bauds: [9600, 19200], parities: ["none"], timeoutMs: 10 },
    (p) => {
      progressEvents.push(p);
      if (!cancelledOnce) {
        cancelledOnce = true;
        harness.service.requestCancel();
      }
    },
  );
  assert.equal(result.cancelled, true);
  assert.equal(result.found, false);
  // 取消后不再进入下一档(wasOpen=false,无恢复 open)
  assert.deepEqual(openCombos(harness.calls), ["9600/none"]);
  assert.ok(progressEvents.length >= 1);
  assert.ok(progressEvents[0].totalCombos >= 2);
  // wasOpen=false:恢复时只关闭,不重开
  const restore = result.trace.find((c) => c.step === "restore");
  assert.equal(restore.reopened, false);
});

test("探测配置自带全部必填字段,不依赖已打开的串口配置", async () => {
  const harness = createHarness({ originalConfig: null }); // 串口从未打开
  await harness.service.scanAll(harness.ctx, { comPort: "COM3", bauds: [9600], parities: ["even"], timeoutMs: 10 });
  const firstOpen = openCalls(harness.calls)[0];
  assert.equal(firstOpen.config.portName, "COM3");
  assert.equal(firstOpen.config.baudRate, 9600);
  assert.equal(firstOpen.config.parity, "even");
  assert.equal(firstOpen.config.dataBits, 8);
  assert.equal(firstOpen.config.stopBits, 1);
  assert.equal(firstOpen.config.flowControl, "none");
  assert.equal(firstOpen.config.dtrMode, "preserve");
  assert.equal(firstOpen.config.rtsMode, "preserve");
  assert.equal(firstOpen.config.readTimeoutMs, 10);
  assert.equal(firstOpen.config.writeTimeoutMs, 10);
});

test("open 持续失败:单档重试一次后跳档,返回未命中且不抛错", async () => {
  const harness = createHarness({ openFails: true });
  const result = await harness.service.scanAll(harness.ctx, {
    comPort: "COM3",
    bauds: [9600],
    parities: ["none"],
    timeoutMs: 10,
  });
  assert.equal(result.ok, true);
  assert.equal(result.found, false);
  assert.equal(result.hits.length, 0);
  assert.deepEqual(harness.probes, []); // 从未成功 open,不探测
  assert.ok(result.trace.some((c) => c.step === "open-failed"));
  // 仍恢复(关闭)原状态
  assert.ok(result.trace.some((c) => c.step === "restore"));
});

test("缺少 comPort 返回 INVALID_PARAM 且不触碰串口", async () => {
  const harness = createHarness();
  const result = await harness.service.scanAll(harness.ctx, {});
  assert.equal(result.ok, false);
  assert.equal(result.error.code, "INVALID_PARAM");
  assert.deepEqual(harness.calls, []);
});

test("非法站号范围返回 INVALID_PARAM 且不触碰串口", async () => {
  const harness = createHarness();
  const result = await harness.service.scanAll(harness.ctx, {
    comPort: "COM3",
    stationStart: 16,
    stationEnd: 1,
  });
  assert.equal(result.ok, false);
  assert.equal(result.error.code, "INVALID_PARAM");
  assert.deepEqual(harness.calls, []);
});
