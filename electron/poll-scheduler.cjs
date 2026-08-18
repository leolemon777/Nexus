/**
 * 轮询调度器 —— 用 setInterval 驱动周期性读取。
 *
 * 阶段 2 用 Electron 侧 setInterval 实现(快速可用)。
 * 阶段 5 会下沉到 Rust 侧(流式推送,消除 IPC 往返开销)。
 *
 * 支持:
 * - RTU/ASCII 串口轮询(复用 read_*_once IPC)
 * - TCP/UDP 轮询(复用 tcp_read_* IPC)
 * - 多点位轮询(一个调度器管多个地址)
 * - 启动/停止/状态查询
 */

class PollScheduler {
  constructor() {
    this.active = new Map(); // pollId → { timer, config, tickCount, errorCount }
    this.nextId = 1;
    this.onDataCallback = null;
    this.onErrorCallback = null;
  }

  /**
   * 设置数据回调。每次成功读到数据时调用。
   * @param {(pollId: string, data: {registers?: number[], coils?: boolean[], address: number, fc: number}) => void} callback
   */
  onData(callback) {
    this.onDataCallback = callback;
  }

  /**
   * 设置错误回调。
   * @param {(pollId: string, error: {code: string, message: string}) => void} callback
   */
  onError(callback) {
    this.onErrorCallback = callback;
  }

  /**
   * 启动轮询。
   * @param {{ invoke: Function }} invokeFn - IPC invoke 函数(callBackend)
   * @param {{ transport: string, connectionId?: string, unitId: number, fc: number, startAddress: number, quantity: number, intervalMs: number, dataType?: string }} config
   * @returns {string} pollId
   */
  start(invokeFn, config) {
    const pollId = `poll-${this.nextId++}`;
    const intervalMs = Math.max(50, config.intervalMs || 1000);

    const entry = {
      timer: null,
      config: { ...config },
      tickCount: 0,
      errorCount: 0,
      running: true,
    };

    const tick = async () => {
      if (!entry.running) return;
      // 重入保护:上一轮事务未完成(超时/慢设备)时跳过本轮,避免堆积与 SERIAL_BUSY 风暴
      if (entry.inFlight) return;
      entry.inFlight = true;
      try {
        const result = await this._readOnce(invokeFn, config);
        entry.tickCount++;
        if (result.ok) {
          this.onDataCallback?.(pollId, {
            registers: result.registers,
            coils: result.coils,
            address: config.startAddress,
            fc: config.fc,
            tickCount: entry.tickCount,
          });
        } else {
          entry.errorCount++;
          this.onErrorCallback?.(pollId, {
            code: result.error?.code ?? "POLL_ERROR",
            message: result.error?.message ?? "轮询读取失败",
          });
        }
      } catch (error) {
        entry.errorCount++;
        this.onErrorCallback?.(pollId, {
          code: error?.code ?? "POLL_EXCEPTION",
          message: error?.message ?? String(error),
        });
      } finally {
        entry.inFlight = false;
      }
    };

    entry.timer = setInterval(tick, intervalMs);
    entry.timer.unref?.(); // 不阻止 Node 退出
    this.active.set(pollId, entry);

    // 立即触发第一次
    tick();

    return pollId;
  }

  /**
   * 停止轮询。
   * @param {string} pollId
   */
  stop(pollId) {
    const entry = this.active.get(pollId);
    if (!entry) return false;
    entry.running = false;
    clearInterval(entry.timer);
    this.active.delete(pollId);
    return true;
  }

  /**
   * 停止所有轮询。
   */
  stopAll() {
    for (const [pollId, entry] of this.active) {
      entry.running = false;
      clearInterval(entry.timer);
    }
    this.active.clear();
  }

  /**
   * 获取轮询状态。
   */
  getStatus(pollId) {
    const entry = this.active.get(pollId);
    if (!entry) return null;
    return {
      pollId,
      running: entry.running,
      tickCount: entry.tickCount,
      errorCount: entry.errorCount,
      config: entry.config,
    };
  }

  /**
   * 列出所有活跃轮询。
   */
  listActive() {
    return [...this.active.keys()].map((id) => this.getStatus(id));
  }

  /**
   * 根据传输方式选择正确的 IPC 命令执行一次读。
   * @private
   */
  async _readOnce(invokeFn, config) {
    const { transport, connectionId, unitId, fc, startAddress, quantity } = config;
    const isTcp = ["tcp", "udp", "rtu-over-tcp", "ascii-over-tcp"].includes(transport);

    if (isTcp) {
      // TCP/UDP 路径:用端到端命令
      const proto = transport === "udp" ? "udp" : "tcp";
      const fcCmdMap = {
        1: `${proto}_read_coils`,
        2: `${proto}_read_discrete_inputs`,
        3: `${proto}_read_holding_registers`,
        4: `${proto}_read_input_registers`,
      };
      const cmd = fcCmdMap[fc] || `${proto}_read_holding_registers`;
      const result = await invokeFn(cmd, { connectionId: connectionId || "default", startAddress, quantity });
      // TCP 命令直接返回 { status, registers/coils, exceptionCode }
      if (result.status === "exception") {
        return {
          ok: false,
          error: {
            code: "MODBUS_EXCEPTION",
            message: `异常 0x${(result.exceptionCode ?? 0).toString(16)}: ${result.exceptionName ?? ""}`,
          },
        };
      }
      return { ok: true, registers: result.registers, coils: result.coils };
    } else {
      // RTU/ASCII 串口路径:用 read_*_once 命令
      const fcCmdMap = {
        1: "read_coils_once",
        2: "read_discrete_inputs_once",
        3: "read_holding_registers_once",
        4: "read_input_registers_once",
      };
      const cmd = fcCmdMap[fc] || "read_holding_registers_once";
      // 串口命令返回 { ok, registers/coils, error }
      return invokeFn(cmd, { unitId, startAddress, quantity });
    }
  }
}

module.exports = { PollScheduler };
