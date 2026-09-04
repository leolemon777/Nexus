/**
 * replay-scheduler 单元测试: 间隔计算 / 单步 / 暂停恢复 / 空记录。
 */
const test = require("node:test");
const assert = require("node:assert/strict");
const modPromise = import("../src/replay-scheduler.js");

function records() {
  return [
    { ts: 1000, dir: "TX", bytes: [1] },
    { ts: 1100, dir: "RX", bytes: [2] },
    { ts: 1300, dir: "RX", bytes: [3] },
  ];
}

test("computeDelay: 正差÷倍速,负差/无效 ts/超上限处理", async () => {
  const { ReplayScheduler } = await modPromise;
  const d = ReplayScheduler.computeDelay;
  assert.equal(d(1000, 1100, 1), 100);
  assert.equal(d(1000, 1100, 2), 50);
  assert.equal(d(1100, 1000, 1), 0); // 时钟回拨/乱序 → 0
  assert.equal(d(NaN, 1000), 0);
  assert.equal(d(0, 10_000_000, 1, 5000), 5000); // 封顶
});

test("播放: 首帧立即出,后续按 ts 差调度,播完回调 done", async () => {
  const { ReplayScheduler } = await modPromise;
  const played = [];
  let doneAt = 0;
  const sched = new ReplayScheduler({
    records: records().map((r) => ({ ...r, ts: Date.now() + r.ts })),
    onRecord: (rec) => played.push(rec.bytes[0]),
    onDone: () => { doneAt = played.length; },
    speed: 1,
  });
  sched.start();
  await new Promise((resolve) => setTimeout(resolve, 30));
  assert.equal(played.length, 1); // 首帧立即
  await new Promise((resolve) => setTimeout(resolve, 400)); // 后两帧按 100ms+200ms 调度
  assert.deepEqual(played, [1, 2, 3]);
  assert.equal(sched.progress.state, "done");
  assert.equal(doneAt, 3);
});

test("暂停停住调度,恢复续播;单步不依赖播放态", async () => {
  const { ReplayScheduler } = await modPromise;
  const played = [];
  const base = Date.now();
  const sched = new ReplayScheduler({
    records: [
      { ts: base, dir: "RX", bytes: [1] },
      { ts: base + 400, dir: "RX", bytes: [2] },
      { ts: base + 800, dir: "RX", bytes: [3] },
    ],
    onRecord: (rec) => played.push(rec.bytes[0]),
    speed: 1,
  });
  sched.start();
  await new Promise((resolve) => setTimeout(resolve, 20));
  sched.pause();
  assert.equal(sched.progress.state, "paused");
  await new Promise((resolve) => setTimeout(resolve, 500));
  assert.equal(played.length, 1); // 暂停后没有新帧
  sched.step(); // 单步立即推一条(仍是暂停态)
  assert.deepEqual(played, [1, 2]);
  assert.equal(sched.progress.state, "paused");
  sched.resume();
  await new Promise((resolve) => setTimeout(resolve, 500)); // 下一帧按 ts 差 400ms 调度
  assert.deepEqual(played, [1, 2, 3]);
  assert.equal(sched.progress.state, "done");
});

test("空记录 start 直接 done;stop 复位", async () => {
  const { ReplayScheduler } = await modPromise;
  let done = 0;
  const sched = new ReplayScheduler({ records: [], onRecord: () => {}, onDone: () => { done += 1; } });
  sched.start();
  assert.equal(sched.progress.state, "done");
  assert.equal(done, 1);
  sched.stop();
  assert.equal(sched.progress.state, "idle");
});

test("normalizeReplayRecords 补 timestamp/direction/hex 并过滤脏行", async () => {
  const { normalizeReplayRecords } = await modPromise;
  const out = normalizeReplayRecords([
    { ts: 5, dir: "RX", bytes: [0x01, 0xAB] },
    { ts: 6, dir: "XX", bytes: [1] },
    null,
  ]);
  assert.equal(out.length, 1);
  assert.equal(out[0].timestamp, 5);
  assert.equal(out[0].direction, "RX");
  assert.equal(out[0].hex, "01 AB");
});
