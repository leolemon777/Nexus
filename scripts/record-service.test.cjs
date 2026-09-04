/**
 * record-service 单元测试: 落盘/轮转/读取校验/坏行容错。
 */
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { RecordService, RECORD_VERSION } = require("../electron/record-service.cjs");

function tempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "nexus-record-"));
}

function waitForIdle() {
  return new Promise((resolve) => setTimeout(resolve, 60));
}

test("start/handleFrame/stop 落盘可读回,帧内容与顺序一致", async () => {
  const dir = tempDir();
  const service = new RecordService();
  const started = service.start({ dir });
  assert.equal(started.recording, true);
  assert.ok(started.file.endsWith(".nxsession.jsonl"));
  service.handleFrame({ timestamp: 1000, direction: "TX", bytes: [1, 2] });
  service.handleFrame({ timestamp: 1050, direction: "RX", bytes: [3, 4, 5] });
  service.handleFrame(null); // 未在录制的脏输入不应抛错
  const stopped = await service.stop();
  assert.equal(stopped.recording, false);
  assert.equal(stopped.frameCount, 2);
  assert.equal(service.status().recording, false);

  const session = RecordService.readSession(started.file);
  assert.equal(session.header.recordVersion, RECORD_VERSION);
  assert.equal(session.header.transport, "serial");
  assert.equal(session.skipped, 0);
  assert.deepEqual(session.records, [
    { ts: 1000, dir: "TX", bytes: [1, 2] },
    { ts: 1050, dir: "RX", bytes: [3, 4, 5] },
  ]);
  fs.rmSync(dir, { recursive: true, force: true });
});

test("重复 start 幂等;未启动时 handleFrame 返回 false", () => {
  const dir = tempDir();
  const service = new RecordService();
  assert.equal(service.handleFrame({ timestamp: 1, direction: "RX", bytes: [1] }), false);
  const first = service.start({ dir });
  const again = service.start({ dir });
  assert.equal(again.recording, true);
  assert.equal(again.file, first.file);
  return service.stop().then(() => fs.rmSync(dir, { recursive: true, force: true }));
});

test("超过轮转阈值自动另起新 part,不丢帧", async () => {
  const dir = tempDir();
  const service = new RecordService({ rotateBytes: 120 }); // 每帧约 60 字节,2 帧轮转
  service.start({ dir });
  for (let i = 0; i < 6; i++) {
    service.handleFrame({ timestamp: 1000 + i * 10, direction: "RX", bytes: [i, i, i, i] });
    await waitForIdle(); // 轮转在 end 回调里异步开新 part
  }
  const stopped = await service.stop();
  assert.equal(stopped.frameCount, 6);
  assert.ok(stopped.files.length >= 2, `应至少 2 个 part,实际 ${stopped.files.length}`);
  const part1 = RecordService.readSession(stopped.files[0]);
  assert.ok(part1.records.length >= 1);
  fs.rmSync(dir, { recursive: true, force: true });
});

test("readSession: 坏行跳过计数,版本头缺失/不支持显式报错", () => {
  const dir = tempDir();
  const file = path.join(dir, "manual.jsonl");
  fs.writeFileSync(file, [
    JSON.stringify({ recordVersion: RECORD_VERSION, transport: "serial", part: 1 }),
    JSON.stringify({ ts: 1, dir: "RX", bytes: [9] }),
    "not-json{{{",
    JSON.stringify({ junk: true }),
    JSON.stringify({ ts: 2, dir: "XX", bytes: [1] }), // 方向非法 → 跳过
    JSON.stringify({ ts: 3, dir: "TX", bytes: [7, 8] }),
    "",
  ].join("\n"));
  const session = RecordService.readSession(file);
  assert.equal(session.records.length, 2);
  assert.equal(session.skipped, 3);
  assert.equal(session.records[1].bytes[0], 7);

  const badVersion = path.join(dir, "bad.jsonl");
  fs.writeFileSync(badVersion, JSON.stringify({ recordVersion: 99 }) + "\n" + JSON.stringify({ ts: 1, dir: "RX", bytes: [1] }));
  assert.throws(() => RecordService.readSession(badVersion), /不支持的录制格式版本/);
  assert.throws(() => RecordService.readSession(path.join(dir, "none.jsonl")));
  fs.rmSync(dir, { recursive: true, force: true });
});
