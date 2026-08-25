"use strict";

/**
 * 原生 S7-200/SMART PPI 串口只读事务。
 *
 * PPI 是双拍链路：
 *   1. 主站发送 SD2 读请求，PLC 回 E5；
 *   2. 主站发送 SA 确认，PLC 回 SD2 数据帧。
 *
 * Electron 独占 COM 句柄并处理两拍时序，Rust Core 负责 S7 地址/PDU、
 * PPI FCS 和响应解析。这个服务暂不暴露写操作，避免把“能组帧”误报为
 * 真机写入已验收。
 */

function ppiError(message, code = "PPI_SERIAL_ERROR", details) {
  const error = new Error(message);
  error.code = code;
  if (details !== undefined) error.details = details;
  return error;
}

function assertStation(value, label) {
  const station = Number(value);
  if (!Number.isInteger(station) || station < 0 || station > 126) {
    throw ppiError(`${label}必须是 0..126 的整数`, "PPI_PARAM_INVALID");
  }
  return station;
}

function assertTimeout(value) {
  const timeoutMs = Number(value ?? 1500);
  if (!Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 600_000) {
    throw ppiError("PPI 超时必须是 1..600000 毫秒", "PPI_PARAM_INVALID");
  }
  return timeoutMs;
}

function assertRetries(value) {
  const retries = Number(value ?? 1);
  if (!Number.isInteger(retries) || retries < 0 || retries > 3) {
    throw ppiError("PPI 重试次数必须是 0..3 的整数", "PPI_PARAM_INVALID");
  }
  return retries;
}

function createPpiSerialService({ request, transact, getSerialStatus }) {
  if (typeof request !== "function") throw new TypeError("request must be a function");
  if (typeof transact !== "function") throw new TypeError("transact must be a function");

  return {
    async read({ station = 2, master = 0, address, count = 1, timeoutMs = 1500, retries = 1 } = {}) {
      const targetStation = assertStation(station, "PPI 从站站号");
      const masterStation = assertStation(master, "PPI 主站站号");
      const timeout = assertTimeout(timeoutMs);
      const retryCount = assertRetries(retries);
      const textAddress = String(address ?? "").trim();
      if (!textAddress) throw ppiError("PPI 地址不能为空", "PPI_PARAM_INVALID");
      const quantity = Number(count);
      if (!Number.isInteger(quantity) || quantity < 1 || quantity > 960) {
        throw ppiError("PPI 读取数量必须是 1..960 的整数", "PPI_PARAM_INVALID");
      }

      const serialStatus = typeof getSerialStatus === "function" ? getSerialStatus() : null;
      if (serialStatus && !serialStatus.isOpen) {
        throw ppiError("请先在主站页打开 PPI 使用的 COM 串口", "SERIAL_NOT_OPEN");
      }
      const serialConfig = serialStatus?.config || {};
      const serialWarnings = [];
      if (serialConfig.baudRate !== undefined && ![9600, 19200, 38400, 187500].includes(Number(serialConfig.baudRate))) {
        serialWarnings.push(`当前波特率 ${serialConfig.baudRate} 不在常用 PPI 档位(9600/19200/38400/187500)内`);
      }
      if (serialConfig.dataBits !== undefined && Number(serialConfig.dataBits) !== 8) serialWarnings.push("PPI 常见数据位为 8");
      if (serialConfig.parity !== undefined && String(serialConfig.parity).toLowerCase() !== "even") serialWarnings.push("当前校验不是常见的偶校验(8E1)");
      if (serialConfig.stopBits !== undefined && String(serialConfig.stopBits) !== "1") serialWarnings.push("PPI 常见停止位为 1");

      const built = await request("ppi_build_read", {
        station: targetStation,
        master: masterStation,
        address: textAddress,
        count: quantity,
      });
      const requestFrame = built?.frame;
      if (!Array.isArray(requestFrame) || requestFrame.length < 10) {
        throw ppiError("Rust Core 返回的 PPI 读请求帧无效", "PPI_BUILD_INVALID");
      }

      const confirm = await request("ppi_build_sa_confirm", {
        station: targetStation,
        master: masterStation,
      });
      const confirmFrame = confirm?.frame;
      if (!Array.isArray(confirmFrame) || confirmFrame.length !== 6) {
        throw ppiError("Rust Core 返回的 PPI SA 确认帧无效", "PPI_BUILD_INVALID");
      }

      let lastError = null;
      for (let attempt = 1; attempt <= retryCount + 1; attempt++) {
        try {
          const first = await transact({
            request: requestFrame,
            timeoutMs: timeout,
            framing: "ppi",
          });
          const firstRx = Array.isArray(first?.rx) ? first.rx : [];
          if (firstRx.length !== 1 || firstRx[0] !== 0xE5) {
            throw ppiError("PPI 第一拍未收到 E5 单字节确认", firstRx[0] === 0x15 ? "PPI_NAK" : "PPI_CONFIRM_INVALID", {
              tx: first?.tx ?? requestFrame,
              rx: firstRx,
              attempt,
            });
          }

          const second = await transact({
            request: confirmFrame,
            timeoutMs: timeout,
            framing: "ppi",
          });
          const responseFrame = Array.isArray(second?.rx) ? second.rx : [];
          if (responseFrame[0] !== 0x68) {
            throw ppiError("PPI 第二拍未收到 SD2 数据帧", "PPI_RESPONSE_INVALID", {
              tx: second?.tx ?? confirmFrame,
              rx: responseFrame,
              attempt,
            });
          }

          const parsed = await request("ppi_parse_read_response", { response: responseFrame });
          if (parsed?.destination !== masterStation || parsed?.source !== targetStation || parsed?.functionCode !== 0x08) {
            throw ppiError("PPI 响应站号或功能码不匹配，疑似回显/重复帧/站号冲突", "PPI_STATION_CONFLICT", {
              expected: { destination: masterStation, source: targetStation, functionCode: 0x08 },
              received: { destination: parsed?.destination, source: parsed?.source, functionCode: parsed?.functionCode },
              attempt,
              rx: responseFrame,
            });
          }
          return {
            ok: true,
            protocol: "siemens-ppi-serial",
            station: targetStation,
            master: masterStation,
            address: textAddress,
            count: quantity,
            attempts: attempt,
            serialWarnings,
            first: { tx: first?.tx ?? requestFrame, rx: firstRx, elapsedMs: first?.elapsedMs ?? null },
            second: { tx: second?.tx ?? confirmFrame, rx: responseFrame, elapsedMs: second?.elapsedMs ?? null },
            ...parsed,
          };
        } catch (error) {
          lastError = error;
          if (attempt > retryCount) break;
        }
      }

      throw ppiError(
        `PPI 读事务失败，已尝试 ${retryCount + 1} 次：${lastError?.message || "未知错误"}`,
        "PPI_RETRY_EXHAUSTED",
        { attempts: retryCount + 1, last: lastError ? { code: lastError.code, message: lastError.message, details: lastError.details } : null },
      );
    },
  };
}

module.exports = { createPpiSerialService, ppiError };
