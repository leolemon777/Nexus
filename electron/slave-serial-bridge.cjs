/**
 * 串口从站桥接 —— Electron 持 COM 句柄,通过 JSONL 与 Rust 交换数据。
 *
 * 工作流程:
 * 1. start(serialService, rustCore) — 开始监听串口 RX
 * 2. 收到串口字节 → 累积到缓冲区,帧静默超时后拼成完整 RTU 帧
 * 3. 完整帧发给 Rust 的 slave_handle_serial_bytes
 * 4. Rust 返回响应字节 → Electron 写回串口
 * 5. stop() — 停止监听
 *
 * 拼帧逻辑(修复:低波特率下 RTU 帧常被 OS 拆成多个 data 事件):
 * - 每个 chunk 到达后重置静默定时器
 * - 静默超过 frameTimeoutMs 即判定一帧结束
 * - 超时按 3.5 字符时间从当前波特率动态计算,下限 2ms 上限 50ms
 */

class SlaveSerialBridge {
  constructor() {
    this.active = false;
    this.serialService = null;
    this.rustCoreRequestFn = null;
    this.slaveId = "serial-default";
    this.dataListener = null;
    // 拼帧状态
    this.accumulateBuffer = Buffer.alloc(0);
    this.frameTimer = null;
    this.frameTimeoutMs = 10;
  }

  /**
   * 根据波特率计算 RTU 帧静默超时(3.5 字符时间)。
   * 字符时间 = 11 位 / 波特率(1 start + 8 data + 1 parity + 1 stop)
   */
  _computeFrameTimeout() {
    const baudRate = this.serialService?.current?.config?.baudRate;
    if (!baudRate || baudRate <= 0) return 10; // 默认 10ms
    const charTimeMs = (11 * 1000) / baudRate;
    const timeout = Math.ceil(3.5 * charTimeMs);
    // 9600 → 4ms, 19200 → 2ms, 1200 → 33ms
    return Math.min(Math.max(timeout, 2), 50);
  }

  /**
   * 启动串口从站桥接。
   * @param {{ port: { on: Function, write: Function, drain: Function } }} serialService
   * @param {(cmd: string, payload: any) => Promise<any>} rustCoreRequestFn
   * @param {string} slaveId
   */
  async start(serialService, rustCoreRequestFn, slaveId = "serial-default") {
    if (this.active) throw new Error("串口从站已在运行");
    this.serialService = serialService;
    this.rustCoreRequestFn = rustCoreRequestFn;
    this.slaveId = slaveId;
    this.frameTimeoutMs = this._computeFrameTimeout();
    this.accumulateBuffer = Buffer.alloc(0);
    this.frameTimer = null;

    // 在 Rust 侧注册串口从站
    await this.rustCoreRequestFn("start_serial_slave", { slaveId: this.slaveId });

    this.active = true;
    // 绑定串口 RX 监听:累积字节,静默超时后处理整帧
    this.dataListener = (chunk) => {
      if (!this.active) return;
      this.accumulateBuffer = Buffer.concat([this.accumulateBuffer, chunk]);
      if (this.frameTimer) clearTimeout(this.frameTimer);
      this.frameTimer = setTimeout(() => this._processFrame(), this.frameTimeoutMs);
    };

    const port = this.serialService?.current?.port;
    if (port) {
      port.on("data", this.dataListener);
    }
  }

  /**
   * 处理累积完成的一帧数据。
   */
  async _processFrame() {
    this.frameTimer = null;
    if (this.accumulateBuffer.length === 0) return;
    const frameBytes = [...this.accumulateBuffer];
    this.accumulateBuffer = Buffer.alloc(0);
    if (!this.active) return;

    try {
      const result = await this.rustCoreRequestFn("slave_handle_serial_bytes", {
        slaveId: this.slaveId,
        bytes: frameBytes,
      });
      if (result.shouldRespond && result.responseBytes?.length > 0) {
        const port = this.serialService?.current?.port;
        if (port && port.isOpen) {
          port.write(Buffer.from(result.responseBytes));
          port.drain();
        }
      }
    } catch {
      // 忽略单帧处理错误(CRC 错误、站号不匹配等由 Rust 侧判定)
    }
  }

  /**
   * 停止串口从站桥接。
   */
  async stop() {
    if (!this.active) return;
    this.active = false;
    if (this.frameTimer) {
      clearTimeout(this.frameTimer);
      this.frameTimer = null;
    }
    this.accumulateBuffer = Buffer.alloc(0);
    const port = this.serialService?.current?.port;
    if (port && this.dataListener) {
      port.removeListener("data", this.dataListener);
    }
    try {
      await this.rustCoreRequestFn?.("stop_serial_slave", { slaveId: this.slaveId });
    } catch {
      // 忽略
    }
  }
}

module.exports = { SlaveSerialBridge };
