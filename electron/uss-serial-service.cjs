"use strict";

/** USS 参数读取的串口只读适配器。控制字、给定值和参数写入不在本轮开放。 */

function ussError(message, code = "USS_SERIAL_ERROR", details) {
  const error = new Error(message);
  error.code = code;
  if (details !== undefined) error.details = details;
  return error;
}

function assertStation(value) {
  const station = Number(value);
  if (!Number.isInteger(station) || station < 0 || station > 30) {
    throw ussError("USS 站号必须是 0..30 的整数", "USS_PARAM_INVALID");
  }
  return station;
}

function assertParam(value) {
  const param = Number(value);
  if (!Number.isInteger(param) || param < 0 || param > 0x0FFF) {
    throw ussError("USS 参数号 PNU 必须是 0..4095 的整数", "USS_PARAM_INVALID");
  }
  return param;
}

function createUssSerialService({ request, transact, getSerialStatus }) {
  if (typeof request !== "function") throw new TypeError("request must be a function");
  if (typeof transact !== "function") throw new TypeError("transact must be a function");

  return {
    async read({ station = 1, param = 700, pzdBytes = 4, timeoutMs = 1500 } = {}) {
      const targetStation = assertStation(station);
      const pnu = assertParam(param);
      const pzdLength = Number(pzdBytes);
      if (!Number.isInteger(pzdLength) || ![0, 2, 4, 8].includes(pzdLength)) {
        throw ussError("USS PZD 长度只允许 0/2/4/8 字节", "USS_PARAM_INVALID");
      }
      const timeout = Number(timeoutMs);
      if (!Number.isInteger(timeout) || timeout < 1 || timeout > 600_000) {
        throw ussError("USS 超时必须是 1..600000 毫秒", "USS_PARAM_INVALID");
      }
      const status = typeof getSerialStatus === "function" ? getSerialStatus() : null;
      if (status && !status.isOpen) throw ussError("请先在主站页打开 USS 使用的 COM 串口", "SERIAL_NOT_OPEN");
      const pzd = Array.from({ length: pzdLength }, () => 0);
      const built = await request("uss_build_request", { station: targetStation, param: pnu, pzd });
      if (!Array.isArray(built?.frame) || built.frame.length < 8) throw ussError("Rust Core 返回的 USS 请求帧无效", "USS_BUILD_INVALID");
      const transaction = await transact({ request: built.frame, timeoutMs: timeout, framing: "uss" });
      const response = Array.isArray(transaction?.rx) ? transaction.rx : [];
      const parsed = await request("uss_parse_response", { frame: response });
      if (parsed?.station !== targetStation) {
        throw ussError("USS 响应站号不匹配，疑似重复帧或总线冲突", "USS_STATION_CONFLICT", {
          expected: targetStation,
          received: parsed?.station,
          rx: response,
        });
      }
      if (!Number.isInteger(parsed?.pkeAkCode) || parsed.pkeAkCode < 0x10) {
        throw ussError(`USS 设备返回任务码 ${parsed?.pkeAk || "未知"}，不是参数读应答`, "USS_DEVICE_ERROR", {
          pkeAkCode: parsed?.pkeAkCode ?? null,
          pkeAkMessage: parsed?.pkeAkMessage ?? null,
          rx: response,
        });
      }
      return {
        ok: true,
        protocol: "siemens-uss-serial",
        station: targetStation,
        param: pnu,
        tx: transaction?.tx ?? built.frame,
        rx: response,
        elapsedMs: transaction?.elapsedMs ?? null,
        ...parsed,
      };
    },
  };
}

module.exports = { createUssSerialService, ussError };
