"use strict";

/** 3964R/RK512 读事务：STX → DLE → 数据帧 → DLE 释放。只读首轮。 */

function rk512Error(message, code = "RK512_SERIAL_ERROR", details) {
  const error = new Error(message);
  error.code = code;
  if (details !== undefined) error.details = details;
  return error;
}

function parseAddress(address) {
  const text = String(address ?? "").trim().toUpperCase();
  let match = /^DB(\d+)\.(?:B|W)(\d+)$/.exec(text);
  if (match) return { area: "DB", db: Number(match[1]), offset: Number(match[2]) };
  match = /^([MIQ])(?:B|W)?(\d+)$/.exec(text);
  if (match) return { area: match[1], db: 0, offset: Number(match[2]) };
  throw rk512Error("RK512 地址格式为 DB1.W0、M0、I0 或 Q0", "RK512_PARAM_INVALID");
}

function createRk512SerialService({ request, transact, getSerialStatus }) {
  if (typeof request !== "function") throw new TypeError("request must be a function");
  if (typeof transact !== "function") throw new TypeError("transact must be a function");

  return {
    async read({ address, count = 1, timeoutMs = 1500 } = {}) {
      const parsedAddress = parseAddress(address);
      const quantity = Number(count);
      if (!Number.isInteger(quantity) || quantity < 1 || quantity > 512) {
        throw rk512Error("RK512 读取字数必须是 1..512 的整数", "RK512_PARAM_INVALID");
      }
      if (parsedAddress.db > 0xFFFF || parsedAddress.offset > 0xFFFF) throw rk512Error("RK512 DB/偏移超出范围", "RK512_PARAM_INVALID");
      const timeout = Number(timeoutMs);
      if (!Number.isInteger(timeout) || timeout < 1 || timeout > 600_000) throw rk512Error("RK512 超时必须是 1..600000 毫秒", "RK512_PARAM_INVALID");
      const status = typeof getSerialStatus === "function" ? getSerialStatus() : null;
      if (status && !status.isOpen) throw rk512Error("请先在主站页打开 RK512 使用的 COM 串口", "SERIAL_NOT_OPEN");

      const built = await request("rk512_build_read", {
        area: parsedAddress.area,
        db: parsedAddress.db,
        offset: parsedAddress.offset,
        count: quantity,
      });
      if (!Array.isArray(built?.frame) || built.frame[0] !== 0x02) throw rk512Error("Rust Core 返回的 RK512 读帧无效", "RK512_BUILD_INVALID");

      const linkStart = await transact({ request: [0x02], timeoutMs: timeout, framing: "rk512" });
      const linkAck = Array.isArray(linkStart?.rx) ? linkStart.rx : [];
      if (linkAck[0] !== 0x10) throw rk512Error("3964R 链路未收到 DLE 确认", "RK512_LINK_ERROR", { rx: linkAck });

      const transaction = await transact({ request: built.frame, timeoutMs: timeout, framing: "rk512" });
      const response = Array.isArray(transaction?.rx) ? transaction.rx : [];
      if (response[0] === 0x15) throw rk512Error("RK512 设备返回 NAK", "RK512_NAK", { rx: response });
      const parsedResponse = await request("rk512_parse_response", { frame: response });
      if (parsedResponse?.error !== 0) throw rk512Error(`RK512 设备错误 0x${Number(parsedResponse?.error ?? 0).toString(16).padStart(2, "0")}`, "RK512_DEVICE_ERROR", parsedResponse);
      const expectedFunc = parsedAddress.area === "DB" ? 0x01 : parsedAddress.area === "M" ? 0x03 : 0x05;
      if (parsedResponse?.func !== expectedFunc || parsedResponse?.db !== parsedAddress.db || parsedResponse?.offset !== parsedAddress.offset) {
        throw rk512Error("RK512 响应地址/功能码不匹配", "RK512_RESPONSE_MISMATCH", {
          expected: { func: expectedFunc, db: parsedAddress.db, offset: parsedAddress.offset },
          received: { func: parsedResponse?.func, db: parsedResponse?.db, offset: parsedResponse?.offset },
        });
      }
      const responseData = Array.isArray(parsedResponse?.data) ? parsedResponse.data : [];
      if (responseData.length !== quantity * 2) {
        throw rk512Error("RK512 响应数据长度与请求字数不一致", "RK512_LENGTH_MISMATCH", {
          expectedBytes: quantity * 2,
          actualBytes: responseData.length,
          count: quantity,
        });
      }

      const release = await transact({ request: [0x10], timeoutMs: timeout, framing: "rk512" });
      const releaseRx = Array.isArray(release?.rx) ? release.rx : [];
      if (releaseRx[0] !== 0x10) throw rk512Error("3964R 链路释放未收到 DLE", "RK512_LINK_ERROR", { rx: releaseRx });
      return {
        ok: true,
        protocol: "siemens-rk512-serial",
        address: String(address).trim(),
        count: quantity,
        linkStart: { tx: linkStart?.tx ?? [0x02], rx: linkAck },
        transaction: { tx: transaction?.tx ?? built.frame, rx: response, elapsedMs: transaction?.elapsedMs ?? null },
        release: { tx: release?.tx ?? [0x10], rx: releaseRx },
        ...parsedResponse,
        data: responseData,
      };
    },
  };
}

module.exports = { createRk512SerialService, parseAddress, rk512Error };
