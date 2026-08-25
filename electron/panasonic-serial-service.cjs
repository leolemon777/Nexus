"use strict";

/**
 * Panasonic FP MEWTOCOL-COM 只读串口会话。
 *
 * 这个服务复用主站页已经打开的 SerialService，不自行打开或关闭 COM，
 * 只允许 RD/RCS 读取；WD/WCS 仍然只停留在离线编解码页面。这样可以把
 * “共享 COM 的软件会话”与“真实 FP 型号/电气层 L2”明确分开。
 */

function panasonicSerialError(message, code = "PANASONIC_SERIAL_ERROR", details) {
  const error = new Error(message);
  error.code = code;
  if (details !== undefined) error.details = details;
  return error;
}

function integer(value, minimum, maximum, label, code = "PANASONIC_PARAM_INVALID") {
  const number = Number(value);
  if (!Number.isInteger(number) || number < minimum || number > maximum) {
    throw panasonicSerialError(`${label}必须是 ${minimum}..${maximum} 的整数`, code);
  }
  return number;
}

function assertSerialStatus(status) {
  if (!status?.isOpen) {
    throw panasonicSerialError("请先在主站页打开 Panasonic MEWTOCOL 使用的 COM 串口", "SERIAL_NOT_OPEN");
  }
}

function serialWarnings(status) {
  const config = status?.config ?? {};
  const warnings = [];
  if (config.baudRate !== undefined && Number(config.baudRate) !== 9600) {
    warnings.push(`当前波特率 ${config.baudRate}；FP 首轮默认记录为 9600`);
  }
  if (config.dataBits !== undefined && Number(config.dataBits) !== 8) warnings.push("MEWTOCOL-COM 首轮常见数据位为 8");
  if (config.parity !== undefined && String(config.parity).toLowerCase() !== "odd") warnings.push("当前校验不是典型 FP 记录中的奇校验(8O1)");
  if (config.stopBits !== undefined && String(config.stopBits) !== "1") warnings.push("MEWTOCOL-COM 首轮常见停止位为 1");
  return warnings;
}

function deviceRecord(status, { model, serialNumber } = {}) {
  const config = status?.config ?? {};
  return {
    model: String(model ?? "").trim() || null,
    serialNumber: String(serialNumber ?? "").trim() || null,
    portName: config.portName ?? null,
    baudRate: config.baudRate ?? null,
    dataBits: config.dataBits ?? null,
    parity: config.parity ?? null,
    stopBits: config.stopBits ?? null,
    recordedAt: new Date().toISOString(),
    source: "operator-input-and-shared-com-status",
  };
}

function isRetryable(error) {
  return new Set([
    "SERIAL_RESPONSE_TIMEOUT",
    "SERIAL_IO_ERROR",
    "SERIAL_FLUSH_TIMEOUT",
    "SERIAL_WRITE_TIMEOUT",
    "SERIAL_DRAIN_TIMEOUT",
    "MEWTOCOL_RESPONSE_INVALID",
    "PANASONIC_INVALID",
  ]).has(error?.code);
}

function validateFrame(frame) {
  if (!Array.isArray(frame) || frame.length < 9 || ![0x25, 0x3c].includes(frame[0]) || frame[frame.length - 1] !== 0x0D) {
    throw panasonicSerialError("Rust Core 返回的 MEWTOCOL 读帧无效", "PANASONIC_BUILD_INVALID");
  }
}

function createPanasonicSerialService({ request, transact, getSerialStatus }) {
  if (typeof request !== "function") throw new TypeError("request must be a function");
  if (typeof transact !== "function") throw new TypeError("transact must be a function");
  if (typeof getSerialStatus !== "function") throw new TypeError("getSerialStatus must be a function");

  async function readFrame({ buildCommand, parseCommand, buildPayload, parsePayload, expectedCommand, station, timeoutMs, retries, status, record }) {
    const built = await request(buildCommand, buildPayload);
    validateFrame(built?.frame);
    const expectedHeader = String(built?.text ?? String.fromCharCode(built.frame[0])).charAt(0) || "%";
    let lastError = null;
    for (let attempt = 1; attempt <= retries + 1; attempt++) {
      try {
        const transaction = await transact({ request: built.frame, timeoutMs, framing: "mewtocol" });
        const rx = Array.isArray(transaction?.rx) ? transaction.rx : [];
        const parsed = await request(parseCommand, {
          station,
          expectedCommand,
          expectedHeader,
          response: rx,
          ...parsePayload,
        });
        return {
          ok: true,
          protocol: "panasonic-mewtocol-com-serial",
          station,
          tx: Array.isArray(transaction?.tx) ? transaction.tx : built.frame,
          rx,
          elapsedMs: transaction?.elapsedMs ?? null,
          attempt,
          readOnly: true,
          frameText: built.text ?? null,
          ...parsed,
          deviceRecord: record,
          serialWarnings: serialWarnings(status),
        };
      } catch (error) {
        lastError = error;
        if (!isRetryable(error) || attempt > retries) break;
      }
    }
    throw panasonicSerialError(
      `MEWTOCOL 只读事务失败：${lastError?.message ?? "未知错误"}`,
      lastError?.code ?? "PANASONIC_SERIAL_ERROR",
      { attempts: retries + 1, cause: lastError?.details ?? null, frame: built.frame },
    );
  }

  return {
    async read({ station = 1, address = "DT100", wordCount = 1, timeoutMs = 1500, retries = 1, model = "", serialNumber = "" } = {}) {
      const targetStation = integer(station, 1, 32, "MEWTOCOL 站号");
      const count = integer(wordCount, 1, 500, "MEWTOCOL 读取字数");
      const timeout = integer(timeoutMs, 1, 600_000, "MEWTOCOL 超时");
      const retryCount = integer(retries, 0, 3, "MEWTOCOL 重试次数");
      const targetAddress = String(address ?? "").trim();
      if (!targetAddress) throw panasonicSerialError("MEWTOCOL 数据地址不能为空", "PANASONIC_PARAM_INVALID");
      const status = getSerialStatus();
      assertSerialStatus(status);
      return readFrame({
        buildCommand: "panasonic_build_read",
        parseCommand: "panasonic_parse_response",
        buildPayload: { station: targetStation, address: targetAddress, wordCount: count },
        parsePayload: {},
        expectedCommand: "RD",
        station: targetStation,
        timeoutMs: timeout,
        retries: retryCount,
        status,
        record: deviceRecord(status, { model, serialNumber }),
      }).then((result) => ({ ...result, address: targetAddress, count }));
    },

    async readContact({ station = 1, address = "X1F", timeoutMs = 1500, retries = 1, model = "", serialNumber = "" } = {}) {
      const targetStation = integer(station, 1, 32, "MEWTOCOL 站号");
      const timeout = integer(timeoutMs, 1, 600_000, "MEWTOCOL 超时");
      const retryCount = integer(retries, 0, 3, "MEWTOCOL 重试次数");
      const targetAddress = String(address ?? "").trim();
      if (!targetAddress) throw panasonicSerialError("MEWTOCOL 触点地址不能为空", "PANASONIC_PARAM_INVALID");
      const status = getSerialStatus();
      assertSerialStatus(status);
      return readFrame({
        buildCommand: "panasonic_build_read_contact",
        parseCommand: "panasonic_parse_response",
        buildPayload: { station: targetStation, address: targetAddress },
        parsePayload: {},
        expectedCommand: "RCS",
        station: targetStation,
        timeoutMs: timeout,
        retries: retryCount,
        status,
        record: deviceRecord(status, { model, serialNumber }),
      }).then((result) => ({ ...result, address: targetAddress, count: 1, contact: true }));
    },
  };
}

module.exports = {
  createPanasonicSerialService,
  panasonicSerialError,
  parseDeviceRecord: deviceRecord,
};
