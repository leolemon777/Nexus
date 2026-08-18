/**
 * 三菱 FX 串口在线事务层 —— Electron 持串口,Rust core 组帧/解析。
 *
 * 流程(Modbus RTU 同款模式):
 * 1. build:JSONL fx_links_xx/fx_prog_xx 命令 → 请求帧字节
 * 2. serialService.transact(framing:"fx") → 收 STX..ETX+SUM / ACK / NAK 帧
 * 3. parse:JSONL fx_links_parse / fx_prog_parse → 结构化结果
 */

const DEFAULT_FX_SERIAL = {
  baudRate: 9600,
  dataBits: 7,
  parity: "even",
  stopBits: 1,
};

/** 值解析:FX Links 读数据(ASCII) → 数值数组。位:逐点 "0"/"1";字:每字 4 字符 hex。 */
function parseFxLinksData(dataAscii, points, isBit) {
  if (isBit) {
    return [...dataAscii.slice(0, points)].map((c) => (c === "1" ? 1 : 0));
  }
  const out = [];
  for (let i = 0; i < points; i++) {
    const hex = dataAscii.slice(i * 4, i * 4 + 4);
    out.push(parseInt(hex, 16) || 0);
  }
  return out;
}

/** FX 编程口读数据(STX..ETX 之间的 ASCII hex,每字 4 字符) → 数值数组。 */
function parseFxProgData(dataBytes, words) {
  const ascii = Buffer.from(dataBytes).toString("ascii");
  const out = [];
  for (let i = 0; i < words; i++) {
    out.push(parseInt(ascii.slice(i * 4, i * 4 + 4), 16) || 0);
  }
  return out;
}

/**
 * 创建 FX 串口服务。
 * @param {{ request: (cmd: string, payload: any) => Promise<any>, transact: Function }} deps
 *        request = rustCore.request(JSONL);transact = serialService.transact 绑定 fx framing 的包装
 */
function createFxSerialService({ request, transact }) {
  /**
   * FX Computer Link 读(station + 软元件 + 点数)。
   */
  async function linksRead({ station, device, head, points, delay, timeoutMs }) {
    // request 即 rustCore.request:失败时已 reject,成功 resolve Rust 的 result 字段
    const frame = (await request("fx_links_read", {
      station, device, head, points, delay: delay ?? 0,
    })).frame;
    const rx = await transact({ request: frame, timeoutMs: timeoutMs ?? 1000, framing: "fx" });
    const r = await request("fx_links_parse", { response: rx.rx });
    if (r.status === "nak") {
      return { ok: false, errorCode: r.errorCode, errorMessage: r.errorMessage };
    }
    const isBit = /^[XYMSTC]/i.test(device);
    return {
      ok: true,
      status: r.status,
      isBit,
      values: r.status === "data" ? parseFxLinksData(r.dataAscii, points, isBit) : [],
    };
  }

  /**
   * FX Computer Link 写(位/字按 device 前缀自动选 BW/WW)。
   */
  async function linksWrite({ station, device, head, values, delay, timeoutMs }) {
    const isBit = /^[XYMSTC]/i.test(device);
    const frame = (await request(isBit ? "fx_links_write_bits" : "fx_links_write_words", {
      station, device, head, values, delay: delay ?? 0,
    })).frame;
    const rx = await transact({ request: frame, timeoutMs: timeoutMs ?? 1000, framing: "fx" });
    const r = await request("fx_links_parse", { response: rx.rx });
    if (r.status === "nak") {
      return { ok: false, errorCode: r.errorCode, errorMessage: r.errorMessage };
    }
    return { ok: true, status: r.status };
  }

  /**
   * FX 编程口读(CMD "0")。
   */
  async function progRead({ device, address, words, timeoutMs }) {
    const frame = (await request("fx_prog_build_read", { device, address, words })).frame;
    const rx = await transact({ request: frame, timeoutMs: timeoutMs ?? 1000, framing: "fx" });
    const r = await request("fx_prog_parse", { response: rx.rx });
    if (r.status === "nak") {
      return { ok: false, errorCode: r.errorCode, errorMessage: r.errorMessage };
    }
    return { ok: true, status: r.status, values: r.status === "data" ? parseFxProgData(r.data, words) : [] };
  }

  /**
   * FX 编程口写(CMD "1")。
   */
  async function progWrite({ device, address, values, timeoutMs }) {
    const frame = (await request("fx_prog_build_write", { device, address, values })).frame;
    const rx = await transact({ request: frame, timeoutMs: timeoutMs ?? 1000, framing: "fx" });
    const r = await request("fx_prog_parse", { response: rx.rx });
    if (r.status === "nak") {
      return { ok: false, errorCode: r.errorCode, errorMessage: r.errorMessage };
    }
    return { ok: true, status: r.status };
  }

  /**
   * MC C24 串口在线读(3C 帧,格式1):组帧 → 串口 → 解封装 → 值。
   */
  async function mcC24Read({ address, points, station, format, timeoutMs }) {
    const build = await request("mc_c24_read", {
      address, points, format: format ?? "1", station: station ?? 0,
    });
    const rx = await transact({ request: build.frame, timeoutMs: timeoutMs ?? 1000, framing: "mc-c24" });
    return await request("mc_c24_parse_read", {
      frame: rx.rx, points, isBit: build.isBit, format: format ?? "1",
    });
  }

  return { linksRead, linksWrite, progRead, progWrite, mcC24Read };
}

module.exports = { createFxSerialService, parseFxLinksData, parseFxProgData, DEFAULT_FX_SERIAL };
