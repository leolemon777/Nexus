/**
 * 回放调度器 —— 按录制文件的原始时间轴(相对 ts 差 ÷ 倍速)重放帧记录。
 * 纯逻辑,不触碰 IPC:调用方传入 onRecord 把记录送回渲染层管线
 * (appendDebugLog + 曲线喂点),绝不伪造 nexus:debug_frame 事件。
 */

export class ReplayScheduler {
  /**
   * @param {{ records: Array<{ts:number, dir:string, bytes:number[]}>, onRecord: Function, onDone?: Function, speed?: number, maxDelayMs?: number }} options
   */
  constructor({ records, onRecord, onDone, speed = 1, maxDelayMs = 3_600_000 } = {}) {
    this.records = Array.isArray(records) ? records : [];
    this.onRecord = onRecord ?? (() => {});
    this.onDone = onDone ?? (() => {});
    this.speed = speed > 0 ? speed : 1;
    this.maxDelayMs = maxDelayMs;
    this.index = 0;
    this.state = "idle"; // idle | playing | paused | done
    this.timer = null;
  }

  /** 相邻两帧的调度间隔:负差(时钟回拨/乱序)按 0,无效 ts 按 0,超上限封顶。 */
  static computeDelay(prevTs, ts, speed = 1, maxDelayMs = 3_600_000) {
    const factor = speed > 0 ? speed : 1;
    if (!Number.isFinite(prevTs) || !Number.isFinite(ts)) return 0;
    const delta = (ts - prevTs) / factor;
    if (!Number.isFinite(delta) || delta <= 0) return 0;
    return Math.min(delta, maxDelayMs);
  }

  start() {
    this._clearTimer();
    this.index = 0;
    if (this.records.length === 0) {
      this.state = "done";
      this.onDone();
      return;
    }
    this.state = "playing";
    this._playCurrent();
  }

  pause() {
    if (this.state !== "playing") return;
    this._clearTimer();
    this.state = "paused";
  }

  resume() {
    if (this.state !== "paused") return;
    if (this.index >= this.records.length) {
      this.state = "done";
      this.onDone();
      return;
    }
    this.state = "playing";
    this._scheduleNext();
  }

  /** 单步:立即播放下一条(不改变播放态)。 */
  step() {
    if (this.index >= this.records.length) return false;
    const record = this.records[this.index];
    this.index += 1;
    this.onRecord(record);
    if (this.index >= this.records.length && this.state !== "playing") {
      this.state = "done";
      this.onDone();
    }
    return true;
  }

  setSpeed(speed) {
    if (Number.isFinite(speed) && speed > 0) this.speed = speed;
  }

  stop() {
    this._clearTimer();
    this.state = "idle";
  }

  get progress() {
    return { index: this.index, total: this.records.length, state: this.state };
  }

  _playCurrent() {
    if (this.state !== "playing") return;
    if (this.index >= this.records.length) {
      this.state = "done";
      this.onDone();
      return;
    }
    const record = this.records[this.index];
    this.index += 1;
    this.onRecord(record);
    if (this.index >= this.records.length) {
      this.state = "done";
      this.onDone();
      return;
    }
    this._scheduleNext();
  }

  _scheduleNext() {
    const prev = this.records[this.index - 1];
    const next = this.records[this.index];
    const delay = ReplayScheduler.computeDelay(prev?.ts, next?.ts, this.speed, this.maxDelayMs);
    this.timer = setTimeout(() => this._playCurrent(), delay);
  }

  _clearTimer() {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}

/** 把录制记录归一化成 debug_frame 管线需要的形状(补 timestamp/direction/hex)。 */
export function normalizeReplayRecords(records) {
  return (Array.isArray(records) ? records : [])
    .filter((rec) => rec && Array.isArray(rec.bytes) && (rec.dir === "TX" || rec.dir === "RX"))
    .map((rec) => ({
      timestamp: Number(rec.ts) || 0,
      direction: rec.dir,
      bytes: rec.bytes,
      hex: rec.bytes
        .map((b) => Number(b).toString(16).padStart(2, "0").toUpperCase())
        .join(" "),
    }));
}
