"use strict";

/**
 * DL/T 645-1997/2007 read-only shared-COM service.
 *
 * This layer never opens or closes a COM port. It reuses the serial port that
 * the operator opened on the main page, asks Rust Core to build/parse every
 * protocol frame, and exposes only a read operation. Multi-frame continuation
 * and every write/control function remain fail-closed in this boundary.
 */

function dlt645SerialError(message, code = "DLT645_SERIAL_ERROR", details) {
  const error = new Error(message);
  error.code = code;
  if (details !== undefined) error.details = details;
  return error;
}

function integer(value, minimum, maximum, label) {
  const number = Number(value);
  if (!Number.isInteger(number) || number < minimum || number > maximum) {
    throw dlt645SerialError(`${label}必须是 ${minimum}..${maximum} 的整数`, "DLT645_PARAM_INVALID");
  }
  return number;
}

function normalizeVersion(value) {
  const version = String(value ?? "2007").trim();
  if (!new Set(["1997", "2007"]).has(version)) {
    throw dlt645SerialError("DL/T 645 版本只能是 1997 或 2007", "DLT645_PARAM_INVALID");
  }
  return version;
}

function normalizeAddress(value) {
  const address = String(value ?? "").trim();
  if (!/^\d{12}$/.test(address)) {
    throw dlt645SerialError("DL/T 645 表地址必须是恰好 12 位十进制数字", "DLT645_PARAM_INVALID");
  }
  if (address === "999999999999") {
    throw dlt645SerialError("DL/T 645 只读事务不允许使用广播地址", "DLT645_BROADCAST_READ_FORBIDDEN");
  }
  return address;
}

function normalizeDataId(version, value) {
  const dataId = String(value ?? "").replace(/\s/g, "").toUpperCase();
  const expectedDigits = version === "1997" ? 4 : 8;
  if (!new RegExp(`^[0-9A-F]{${expectedDigits}}$`).test(dataId)) {
    throw dlt645SerialError(
      `DL/T 645-${version} 数据标识必须是 ${expectedDigits} 位十六进制`,
      "DLT645_PARAM_INVALID",
    );
  }
  return dataId;
}

function assertSerialStatus(status) {
  if (!status?.isOpen) {
    throw dlt645SerialError("请先在主站页打开 DL/T 645 使用的 COM 串口", "SERIAL_NOT_OPEN");
  }
}

function serialWarnings(status) {
  const config = status?.config ?? {};
  const warnings = [];
  if (config.baudRate !== undefined && Number(config.baudRate) !== 2400) {
    warnings.push(`当前波特率 ${config.baudRate}；DL/T 645-2007 默认通信速率为 2400 bps`);
  }
  if (config.dataBits !== undefined && Number(config.dataBits) !== 8) warnings.push("DL/T 645 默认数据位应为 8");
  if (config.parity !== undefined && String(config.parity).toLowerCase() !== "even") warnings.push("DL/T 645 默认应使用偶校验(8E1)");
  if (config.stopBits !== undefined && String(config.stopBits) !== "1") warnings.push("DL/T 645 默认停止位应为 1");
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
    "DLT645_RESPONSE_INVALID",
    "DLT645_FRAME_TOO_LONG",
    "DLT645_CHECKSUM_MISMATCH",
  ]).has(error?.code);
}

function validateBuiltFrame(frame, version) {
  if (!Array.isArray(frame) || frame.length < 14 || frame.length > 216) {
    throw dlt645SerialError("Rust Core 返回的 DL/T 645 读帧长度无效", "DLT645_BUILD_INVALID");
  }
  let coreStart = 0;
  while (coreStart < frame.length && frame[coreStart] === 0xFE) coreStart += 1;
  const expectedDataLength = version === "1997" ? 2 : 4;
  if (
    coreStart > 4
    || frame[coreStart] !== 0x68
    || frame[coreStart + 7] !== 0x68
    || frame[coreStart + 8] !== (version === "1997" ? 0x01 : 0x11)
    || frame[coreStart + 9] !== expectedDataLength
    || frame.length !== coreStart + 12 + expectedDataLength
    || frame[frame.length - 1] !== 0x16
  ) {
    throw dlt645SerialError("Rust Core 返回的 DL/T 645 读帧结构无效", "DLT645_BUILD_INVALID");
  }
}

function createDlt645SerialService({ request, transact, getSerialStatus }) {
  if (typeof request !== "function") throw new TypeError("request must be a function");
  if (typeof transact !== "function") throw new TypeError("transact must be a function");
  if (typeof getSerialStatus !== "function") throw new TypeError("getSerialStatus must be a function");

  return Object.freeze({
    async read({
      version = "2007",
      address = "000000000001",
      dataId = "00010000",
      preambleCount = 4,
      timeoutMs = 1500,
      retries = 1,
      model = "",
      serialNumber = "",
    } = {}) {
      const targetVersion = normalizeVersion(version);
      const targetAddress = normalizeAddress(address);
      const targetDataId = normalizeDataId(targetVersion, dataId);
      const wakeBytes = integer(preambleCount, 0, 4, "DL/T 645 前导 FE 数量");
      const timeout = integer(timeoutMs, 1, 600_000, "DL/T 645 超时");
      const retryCount = integer(retries, 0, 3, "DL/T 645 重试次数");
      const status = getSerialStatus();
      assertSerialStatus(status);
      const record = deviceRecord(status, { model, serialNumber });

      const built = await request("dlt645_build_read_request", {
        version: targetVersion,
        address: targetAddress,
        dataId: targetDataId,
        preambleCount: wakeBytes,
      });
      validateBuiltFrame(built?.frame, targetVersion);

      let lastError = null;
      for (let attempt = 1; attempt <= retryCount + 1; attempt++) {
        try {
          const transaction = await transact({
            request: built.frame,
            timeoutMs: timeout,
            framing: "dlt645",
          });
          const rx = Array.isArray(transaction?.rx) ? transaction.rx : [];
          const parsed = await request("dlt645_parse_read_response", {
            version: targetVersion,
            address: targetAddress,
            dataId: targetDataId,
            frame: rx,
          });
          const response = parsed?.response;
          if (!response || typeof response !== "object") {
            throw dlt645SerialError("Rust Core 未返回 DL/T 645 解析结果", "DLT645_PARSE_INVALID");
          }
          if (response.hasFollowFrame) {
            throw dlt645SerialError(
              "电表返回了后续帧标志；当前只读边界尚未开放多帧续读，已停止以避免返回不完整数据",
              "DLT645_FOLLOW_UNSUPPORTED",
              { control: response.control },
            );
          }
          return {
            ok: true,
            protocol: `dlt645-${targetVersion}-serial`,
            version: targetVersion,
            address: targetAddress,
            dataId: targetDataId,
            tx: Array.isArray(transaction?.tx) ? transaction.tx : built.frame,
            rx,
            elapsedMs: transaction?.elapsedMs ?? null,
            attempt,
            readOnly: true,
            response,
            meterException: response.exception ?? null,
            deviceRecord: record,
            serialWarnings: serialWarnings(status),
          };
        } catch (error) {
          lastError = error;
          if (!isRetryable(error) || attempt > retryCount) break;
        }
      }
      throw dlt645SerialError(
        `DL/T 645-${targetVersion} 只读事务失败：${lastError?.message ?? "未知错误"}`,
        lastError?.code ?? "DLT645_SERIAL_ERROR",
        { attempts: retryCount + 1, cause: lastError?.details ?? null, frame: built.frame },
      );
    },
  });
}

module.exports = {
  createDlt645SerialService,
  dlt645SerialError,
  parseDeviceRecord: deviceRecord,
};
