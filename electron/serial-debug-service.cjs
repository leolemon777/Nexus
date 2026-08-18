/**
 * 串口调试服务 —— 透明收发原始串口字节。
 *
 * 这是唯一允许渲染层提交任意字节的模式(通过显式模式切换保护)。
 * 与 SerialService.transact()(请求-响应模型)不同,调试服务是:
 *   持续监听 RX + 按需发送 TX + 帧累积超时分隔。
 *
 * 核心能力:
 * - startListening(): 持续监听串口 data 事件,累积字节
 * - 帧分隔: 超过 frameDelimiterMs 无新字节即判定一帧结束
 * - send({ bytes, mode }): HEX / ASCII / Modbus-RTU(自动CRC) 三种模式
 * - 收发开关: allowReceive / allowSend / appendCrc
 * - onFrame callback: 帧到达时推送到渲染层
 */

class SerialDebugService {
  constructor() {
    this.port = null;
    this.allowReceive = true;
    this.allowSend = true;
    this.appendCrc = false;
    this.sendMode = "hex"; // "hex" | "ascii" | "modbus-rtu"
    this.frameDelimiterMs = 10;
    this.accumulateBuffer = Buffer.alloc(0);
    this.frameTimer = null;
    this.onFrameCallback = null;
    this.frameLog = []; // 收发记录(环形缓冲,最多 1000 条)
    this.maxLogSize = 1000;
    this.rustCoreRequestFn = null; // 用于计算 CRC
  }

  /**
   * 注入 Rust core 请求函数(用于 CRC 计算)。
   */
  setRustCoreRequest(fn) {
    this.rustCoreRequestFn = fn;
  }

  /**
   * 设置帧回调。
   * @param {(record: {timestamp, direction, bytes, hex}) => void} callback
   */
  onFrame(callback) {
    this.onFrameCallback = callback;
  }

  /**
   * 绑定到一个已打开的 SerialPort 实例(由 SerialService 打开)。
   */
  attach(port) {
    this.port = port;
    this.port.on("data", (chunk) => this._accumulate(chunk));
  }

  /**
   * 解绑。
   */
  detach() {
    if (this.port) {
      this.port.removeAllListeners("data");
      this.port = null;
    }
    if (this.frameTimer) {
      clearTimeout(this.frameTimer);
      this.frameTimer = null;
    }
    this.accumulateBuffer = Buffer.alloc(0);
  }

  /**
   * 发送字节。
   * @param {{ bytes: number[], mode?: string }} param0
   */
  async send({ bytes, mode }) {
    if (!this.port) throw new Error("串口未打开");
    if (!this.allowSend) throw new Error("发送已禁用");

    const sendMode = mode || this.sendMode;
    let payload = Buffer.from(bytes ?? []);

    if (sendMode === "ascii") {
      // ASCII 模式:bytes 是字符的 char code
      payload = Buffer.from(bytes ?? []);
    } else if (sendMode === "modbus-rtu" || this.appendCrc) {
      // Modbus RTU 模式:自动追加 CRC16(低字节在前)
      if (this.rustCoreRequestFn) {
        const result = await this.rustCoreRequestFn("compute_crc16", { bytes: [...payload] });
        const crc = result.crc;
        payload = Buffer.concat([payload, Buffer.from([crc & 0xff, (crc >> 8) & 0xff])]);
      }
    }

    await new Promise((resolve, reject) => {
      this.port.write(payload, (err) => (err ? reject(err) : resolve()));
    });
    await new Promise((resolve, reject) => {
      this.port.drain((err) => (err ? reject(err) : resolve()));
    });

    // 记录 TX
    this._recordFrame("TX", [...payload]);
  }

  /**
   * 清空记录。
   */
  clearLog() {
    this.frameLog = [];
  }

  /**
   * 获取记录副本。
   */
  getLog() {
    return [...this.frameLog];
  }

  // === 内部方法 ===

  _accumulate(chunk) {
    if (!this.allowReceive) return;
    this.accumulateBuffer = Buffer.concat([this.accumulateBuffer, chunk]);
    // 重置帧超时定时器
    if (this.frameTimer) clearTimeout(this.frameTimer);
    this.frameTimer = setTimeout(() => this._flushFrame(), this.frameDelimiterMs);
  }

  _flushFrame() {
    if (this.accumulateBuffer.length === 0) return;
    const bytes = [...this.accumulateBuffer];
    this.accumulateBuffer = Buffer.alloc(0);
    this._recordFrame("RX", bytes);
  }

  _recordFrame(direction, bytes) {
    const record = {
      timestamp: Date.now(),
      direction,
      bytes,
      hex: bytes.map((b) => b.toString(16).padStart(2, "0").toUpperCase()).join(" "),
    };
    this.frameLog.push(record);
    if (this.frameLog.length > this.maxLogSize) {
      this.frameLog.shift();
    }
    this.onFrameCallback?.(record);
  }
}

module.exports = { SerialDebugService };
