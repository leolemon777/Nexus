function hostLinkError(message, code = "OMRON_HOSTLINK_SERIAL_ERROR", details) {
  const error = new Error(message);
  error.code = code;
  if (details !== undefined) error.details = details;
  return error;
}

function parseDmAddress(value) {
  const text = String(value ?? "").trim().toUpperCase();
  const match = /^D(\d+)$/.exec(text);
  if (!match) throw hostLinkError("HostLink C-mode 首轮只支持 DM 字地址，例如 D100", "OMRON_HOSTLINK_PARAM_INVALID");
  const dmStart = Number(match[1]);
  if (!Number.isInteger(dmStart) || dmStart < 0 || dmStart > 0xFFFF) {
    throw hostLinkError("HostLink DM 地址必须是 0..65535 的整数", "OMRON_HOSTLINK_PARAM_INVALID");
  }
  return dmStart;
}

function parseFinsWordAddress(value) {
  const text = String(value ?? "").trim().toUpperCase();
  const match = /^(D|CIO|W|H)(\d+)$/.exec(text);
  if (!match) {
    throw hostLinkError("HostLink FINS 首轮只支持 D/CIO/W/H 字地址，例如 D100 或 CIO0", "OMRON_HOSTLINK_PARAM_INVALID");
  }
  const byte = Number(match[2]);
  if (!Number.isInteger(byte) || byte < 0 || byte > 0xFFFFFF) {
    throw hostLinkError("HostLink FINS 地址必须是 0..16777215 的整数", "OMRON_HOSTLINK_PARAM_INVALID");
  }
  const area = { D: "DM", CIO: "CIO", W: "WR", H: "HR" }[match[1]];
  return { area, byte, address: `${match[1]}${byte}` };
}

function parseResponseStation(frame) {
  const bytes = Buffer.isBuffer(frame) ? frame : Buffer.from(frame ?? []);
  if (bytes.length < 5 || bytes[0] !== 0x40) {
    throw hostLinkError("HostLink 响应不是以 @ 开头的 ASCII 帧", "OMRON_HOSTLINK_RESPONSE_INVALID", { rx: [...bytes] });
  }
  const stationText = bytes.subarray(1, 3).toString("ascii");
  if (!/^[0-9A-F]{2}$/i.test(stationText)) {
    throw hostLinkError("HostLink 响应站号不是十六进制", "OMRON_HOSTLINK_RESPONSE_INVALID", { stationText });
  }
  const station = Number.parseInt(stationText, 16);
  if (station > 31) {
    throw hostLinkError("HostLink 响应站号超出 0..31", "OMRON_HOSTLINK_RESPONSE_INVALID", { station });
  }
  if (bytes.subarray(3, 5).toString("ascii") !== "RR") {
    throw hostLinkError("HostLink C-mode 响应命令不是 RR", "OMRON_HOSTLINK_RESPONSE_INVALID", { rx: [...bytes] });
  }
  return station;
}

function parseFinsResponseStation(frame) {
  const bytes = Buffer.isBuffer(frame) ? frame : Buffer.from(frame ?? []);
  if (bytes.length < 5 || bytes[0] !== 0x40) {
    throw hostLinkError("HostLink FINS 响应不是以 @ 开头的 ASCII 帧", "OMRON_HOSTLINK_RESPONSE_INVALID", { rx: [...bytes] });
  }
  const stationText = bytes.subarray(1, 3).toString("ascii");
  if (!/^[0-9A-F]{2}$/i.test(stationText)) {
    throw hostLinkError("HostLink FINS 响应站号不是十六进制", "OMRON_HOSTLINK_RESPONSE_INVALID", { stationText });
  }
  const station = Number.parseInt(stationText, 16);
  const header = bytes.subarray(3, 5).toString("ascii");
  if (station > 31 || header !== "FA") {
    throw hostLinkError("HostLink FINS 响应站号或头代码无效", "OMRON_HOSTLINK_RESPONSE_INVALID", { stationText, header });
  }
  return station;
}

function assertSerialStatus(status) {
  if (!status?.isOpen) {
    throw hostLinkError("请先在主站页打开 HostLink 使用的 COM 串口", "SERIAL_NOT_OPEN");
  }
}

function createOmronHostLinkSerialService({ request, transact, getSerialStatus }) {
  if (typeof request !== "function") throw new TypeError("request must be a function");
  if (typeof transact !== "function") throw new TypeError("transact must be a function");
  if (typeof getSerialStatus !== "function") throw new TypeError("getSerialStatus must be a function");

  return {
    async read({ station = 0, address = "D0", count = 1, timeoutMs = 1500 } = {}) {
      const targetStation = Number(station);
      const wordCount = Number(count);
      const timeout = Number(timeoutMs);
      if (!Number.isInteger(targetStation) || targetStation < 0 || targetStation > 31) {
        throw hostLinkError("HostLink 站号必须是 0..31 的整数", "OMRON_HOSTLINK_PARAM_INVALID");
      }
      if (!Number.isInteger(wordCount) || wordCount < 1 || wordCount > 100) {
        throw hostLinkError("HostLink C-mode 首轮读取字数必须是 1..100", "OMRON_HOSTLINK_PARAM_INVALID");
      }
      if (!Number.isInteger(timeout) || timeout < 1 || timeout > 600_000) {
        throw hostLinkError("HostLink 超时必须是 1..600000 毫秒", "OMRON_HOSTLINK_PARAM_INVALID");
      }
      const dmStart = parseDmAddress(address);
      if (dmStart + wordCount - 1 > 0xFFFF) {
        throw hostLinkError("HostLink C-mode DM 地址窗口超出 0..65535", "OMRON_HOSTLINK_PARAM_INVALID", {
          dmStart,
          count: wordCount,
        });
      }
      const status = getSerialStatus();
      assertSerialStatus(status);
      const built = await request("hostlink_build_cmode_read", {
        station: targetStation,
        dmStart,
        wordCount,
      });
      if (!Array.isArray(built?.frame) || built.frame[0] !== 0x40) {
        throw hostLinkError("Rust Core 返回的 HostLink 请求帧无效", "OMRON_HOSTLINK_BUILD_INVALID");
      }
      const transaction = await transact({
        request: built.frame,
        timeoutMs: timeout,
        framing: "hostlink",
      });
      const rx = Array.isArray(transaction?.rx) ? transaction.rx : [];
      const responseStation = parseResponseStation(rx);
      if (responseStation !== targetStation) {
        throw hostLinkError("HostLink 响应站号不匹配，疑似重复帧或串线", "OMRON_HOSTLINK_STATION_CONFLICT", {
          expected: targetStation,
          actual: responseStation,
          rx,
        });
      }
      const parsed = await request("hostlink_parse_cmode_read", { frame: rx });
      if (!Array.isArray(parsed?.words) || parsed.words.length !== wordCount) {
        throw hostLinkError("HostLink 响应字数与请求不一致", "OMRON_HOSTLINK_LENGTH_MISMATCH", {
          expected: wordCount,
          actual: parsed?.words?.length ?? null,
          rx,
        });
      }
      return {
        ok: true,
        protocol: "omron-hostlink-cmode-serial",
        station: targetStation,
        address: `D${dmStart}`,
        count: wordCount,
        words: parsed.words,
        tx: Array.isArray(transaction?.tx) ? transaction.tx : built.frame,
        rx,
        elapsedMs: transaction?.elapsedMs,
        readOnly: true,
        serialWarnings: [
          "当前只实现 HostLink C-mode RR 读 DM；不代表 HostLink FINS、写入或全部欧姆龙机型兼容。",
          "COM 的 7E2/7E1 等格式必须按 CPU/串口单元手册在主站页预先配置。",
        ],
      };
    },
    async readFins({ station = 0, address = "D0", count = 1, timeoutMs = 1500 } = {}) {
      const targetStation = Number(station);
      const wordCount = Number(count);
      const timeout = Number(timeoutMs);
      if (!Number.isInteger(targetStation) || targetStation < 0 || targetStation > 31) {
        throw hostLinkError("HostLink FINS 站号必须是 0..31 的整数", "OMRON_HOSTLINK_PARAM_INVALID");
      }
      if (!Number.isInteger(wordCount) || wordCount < 1 || wordCount > 100) {
        throw hostLinkError("HostLink FINS 首轮读取字数必须是 1..100", "OMRON_HOSTLINK_PARAM_INVALID");
      }
      if (!Number.isInteger(timeout) || timeout < 1 || timeout > 600_000) {
        throw hostLinkError("HostLink FINS 超时必须是 1..600000 毫秒", "OMRON_HOSTLINK_PARAM_INVALID");
      }
      const parsedAddress = parseFinsWordAddress(address);
      if (parsedAddress.byte + wordCount - 1 > 0xFFFFFF) {
        throw hostLinkError("HostLink FINS 地址窗口超出三字节地址范围", "OMRON_HOSTLINK_PARAM_INVALID", {
          address: parsedAddress.byte,
          count: wordCount,
        });
      }
      assertSerialStatus(getSerialStatus());
      const built = await request("hostlink_build_fins", {
        station: targetStation,
        area: parsedAddress.area,
        byte: parsedAddress.byte,
        count: wordCount,
      });
      if (!Array.isArray(built?.frame) || built.frame[0] !== 0x40) {
        throw hostLinkError("Rust Core 返回的 HostLink FINS 请求帧无效", "OMRON_HOSTLINK_BUILD_INVALID");
      }
      const transaction = await transact({ request: built.frame, timeoutMs: timeout, framing: "hostlink" });
      const rx = Array.isArray(transaction?.rx) ? transaction.rx : [];
      const responseStation = parseFinsResponseStation(rx);
      if (responseStation !== targetStation) {
        throw hostLinkError("HostLink FINS 响应站号不匹配，疑似重复帧或串线", "OMRON_HOSTLINK_STATION_CONFLICT", {
          expected: targetStation,
          actual: responseStation,
          rx,
        });
      }
      const parsed = await request("hostlink_parse_fins", { frame: rx });
      if (Number(parsed?.sid) !== 1) {
        throw hostLinkError("HostLink FINS 响应 SID 不匹配，疑似重复帧", "OMRON_HOSTLINK_SID_MISMATCH", {
          expected: 1,
          actual: parsed?.sid ?? null,
          rx,
        });
      }
      if (Number(parsed?.endCode) !== 0) {
        throw hostLinkError(`HostLink FINS 设备结束码 0x${Number(parsed?.endCode ?? 0).toString(16).padStart(4, "0")}`, "OMRON_HOSTLINK_DEVICE_ERROR", parsed);
      }
      const data = Array.isArray(parsed?.data) ? parsed.data : [];
      if (data.length !== wordCount * 2) {
        throw hostLinkError("HostLink FINS 响应字节数与请求不一致", "OMRON_HOSTLINK_LENGTH_MISMATCH", {
          expected: wordCount * 2,
          actual: data.length,
          rx,
        });
      }
      const values = [];
      for (let index = 0; index < data.length; index += 2) values.push((Number(data[index]) << 8) | Number(data[index + 1]));
      return {
        ok: true,
        protocol: "omron-hostlink-fins-serial",
        station: targetStation,
        address: parsedAddress.address,
        count: wordCount,
        values,
        isBit: false,
        tx: Array.isArray(transaction?.tx) ? transaction.tx : built.frame,
        rx,
        elapsedMs: transaction?.elapsedMs,
        readOnly: true,
        serialWarnings: [
          "当前只实现 HostLink FINS 0101 字读取；写入、位读取和完整 HostLink 服务集仍未开放。",
          "COM 的波特率/校验/停止位必须按 CPU/串口单元手册在主站页预先配置。",
        ],
      };
    },
  };
}

module.exports = {
  createOmronHostLinkSerialService,
  parseDmAddress,
  parseFinsWordAddress,
  parseResponseStation,
  parseFinsResponseStation,
  hostLinkError,
};
