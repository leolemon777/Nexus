/**
 * 会话录制服务 —— 串口调试 TX/RX 整段落盘为 JSONL(spec-plan-serial-plot-parse-replay.md 批次 3)。
 *
 * 文件格式:
 *   首行 header: {"recordVersion":1,"transport":"serial","startedAt":ISO,"part":1}
 *   随后每帧一行: {"ts":<epoch ms>,"dir":"TX"|"RX","bytes":[0..255]}
 * 单文件超 5MB 自动轮转另起新 part(参照 write-audit 先例,不丢弃)。
 * 坏行读取时跳过并计数,不让整个文件失效。
 */

const fs = require("node:fs");
const path = require("node:path");

const RECORD_VERSION = 1;
const ROTATE_BYTES = 5 * 1024 * 1024;
const MAX_READ_FRAMES = 100000;

class RecordService {
  constructor({ rotateBytes = ROTATE_BYTES } = {}) {
    this.rotateBytes = rotateBytes;
    this.stream = null;
    this.dir = null;
    this.filePath = null;
    this.baseName = null;
    this.part = 0;
    this.files = [];
    this.frameCount = 0;
    this.writtenBytes = 0;
    this.startedAt = null;
  }

  /**
   * 开始录制。重复调用幂等(返回当前状态)。
   * @param {{ dir?: string }} [options]
   */
  start({ dir } = {}) {
    if (this.stream) {
      return { recording: true, file: this.filePath, frameCount: this.frameCount, part: this.part };
    }
    if (!dir) throw new Error("未配置录制目录");
    fs.mkdirSync(dir, { recursive: true });
    const stamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
    this.dir = dir;
    this.baseName = `session-${stamp}`;
    this.part = 0;
    this.files = [];
    this.frameCount = 0;
    this.startedAt = new Date().toISOString();
    this._openPart();
    return { recording: true, file: this.filePath, frameCount: 0, part: this.part };
  }

  /** 帧到达时写入。未在录制时静默忽略(返回 false)。 */
  handleFrame(record) {
    if (!this.stream || !record || !Array.isArray(record.bytes)) return false;
    const line = `${JSON.stringify({
      ts: Number(record.timestamp) || 0,
      dir: record.direction === "TX" ? "TX" : "RX",
      bytes: record.bytes,
    })}\n`;
    this.stream.write(line);
    this.writtenBytes += Buffer.byteLength(line, "utf8");
    this.frameCount += 1;
    if (this.writtenBytes >= this.rotateBytes) {
      const old = this.stream;
      this.stream = null;
      old.end(() => {
        if (this.frameCount > 0 && !this.stream) {
          this.part += 1;
          this._openPart();
        }
      });
    }
    return true;
  }

  /** 停止录制(等待落盘)。 */
  stop() {
    if (!this.stream) {
      return Promise.resolve({ recording: false, files: [...this.files], frameCount: this.frameCount });
    }
    const stream = this.stream;
    this.stream = null;
    return new Promise((resolve) => {
      stream.end(() => {
        resolve({ recording: false, files: [...this.files], frameCount: this.frameCount });
      });
    });
  }

  status() {
    return {
      recording: Boolean(this.stream),
      file: this.filePath,
      part: this.part,
      frameCount: this.frameCount,
      dir: this.dir,
    };
  }

  _openPart() {
    this.part = Math.max(1, this.part + 1);
    const name = this.part === 1
      ? `${this.baseName}.nxsession.jsonl`
      : `${this.baseName}.part${this.part}.nxsession.jsonl`;
    this.filePath = path.join(this.dir, name);
    this.stream = fs.createWriteStream(this.filePath, { flags: "ax", encoding: "utf8" });
    this.files.push(this.filePath);
    const header = {
      recordVersion: RECORD_VERSION,
      transport: "serial",
      startedAt: this.startedAt,
      part: this.part,
    };
    this.stream.write(`${JSON.stringify(header)}\n`);
    this.writtenBytes = 0;
  }

  /**
   * 读取并校验录制文件(单 part)。
   * @returns {{ header: object, records: Array<{ts,dir,bytes}>, skipped: number, truncated: boolean }}
   */
  static readSession(filePath, { maxFrames = MAX_READ_FRAMES } = {}) {
    if (!filePath || typeof filePath !== "string") throw new Error("未指定录制文件");
    const text = fs.readFileSync(filePath, "utf8");
    const lines = text.split("\n");
    let header = null;
    const records = [];
    let skipped = 0;
    let truncated = false;
    for (const line of lines) {
      const trimmed = line.trim();
      if (!trimmed) continue;
      let parsed;
      try {
        parsed = JSON.parse(trimmed);
      } catch {
        skipped += 1;
        continue;
      }
      if (!header) {
        if (parsed.recordVersion !== RECORD_VERSION) {
          throw new Error(`不支持的录制格式版本: ${parsed.recordVersion}`);
        }
        header = parsed;
        continue;
      }
      if (records.length >= maxFrames) {
        truncated = true;
        break;
      }
      if (
        typeof parsed.ts === "number"
        && (parsed.dir === "TX" || parsed.dir === "RX")
        && Array.isArray(parsed.bytes)
      ) {
        records.push({ ts: parsed.ts, dir: parsed.dir, bytes: parsed.bytes });
      } else {
        skipped += 1;
      }
    }
    if (!header) throw new Error("录制文件缺少版本头(recordVersion)");
    return { header, records, skipped, truncated };
  }
}

module.exports = { RecordService, RECORD_VERSION, ROTATE_BYTES };
