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

/** FX 编程口读数据(STX..ETX 之间的 ASCII hex,每字 4 字符,低字节在前) → 数值数组。 */
function parseFxProgData(dataBytes, words) {
  const ascii = Buffer.from(dataBytes).toString("ascii");
  const out = [];
  for (let i = 0; i < words; i++) {
    const hex = ascii.slice(i * 4, i * 4 + 4);
    // "3412" = 低字节 0x34 在前 → 0x1234(§3.3.3 低字节在前;与 FX Links 的高字节在前相反)
    out.push(((parseInt(hex.slice(2, 4), 16) || 0) << 8) | (parseInt(hex.slice(0, 2), 16) || 0));
  }
  return out;
}

/**
 * FX 编程口位软元件数据(ASCII hex,每字节 2 字符) → 位序列。
 * 每字节 8 点、LSB 在前;`offset` = 起始编号%8(序列第 i 点位于 (offset+i)/8 字节的 (offset+i)%8 位)。
 */
function parseFxProgBits(dataBytes, points, offset = 0) {
  const ascii = Buffer.from(dataBytes ?? []).toString("ascii");
  const out = [];
  for (let i = 0; i < points; i++) {
    const pos = offset + i;
    const byte = parseInt(ascii.slice((pos >> 3) * 2, (pos >> 3) * 2 + 2), 16) || 0;
    out.push((byte >> (pos & 7)) & 1);
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
   * 位软元件(X/Y/M/S):点数按位计,请求 ceil(points/16) 字(接口按字,只读多读无害),
   * 响应每字节 8 点、LSB 在前(HSL SoftBasic.ByteToBoolArray 同序),取前 points 位。
   */
  async function progRead({ device, address, words, timeoutMs }) {
    const isBitDevice = /^(X|Y|M|S|T|C)$/i.test(String(device ?? "").trim());
    const points = Number(words) || 0;
    const requestWords = isBitDevice ? Math.max(1, Math.ceil(points / 16)) : points;
    const frame = (await request("fx_prog_build_read", { device, address, words: requestWords })).frame;
    const rx = await transact({ request: frame, timeoutMs: timeoutMs ?? 1000, framing: "fx" });
    // fx_prog_parse 的信封字段是 frame(与 fx_links_parse 的 response 不同,Rust 端 deny_unknown_fields)
    const r = await request("fx_prog_parse", { frame: rx.rx });
    if (r.status === "nak") {
      return { ok: false, errorCode: r.errorCode, errorMessage: r.errorMessage };
    }
    if (isBitDevice) {
      // 起始编号按 X/Y 八进制解析(与 rust 一致);请求地址由 rust 算(基址+编号/8),
      // 解包按 字节内偏移=编号%8 对齐
      const radix = /^(X|Y)$/i.test(String(device).trim()) ? 8 : 10;
      const start = Number.parseInt(String(address ?? "0").trim(), radix) || 0;
      const bits = parseFxProgBits(r.data, points, start % 8);
      return { ok: true, status: r.status, values: bits, isBit: true };
    }
    // 编程口字数据低字节在前("3412"→0x1234),Rust 端 decode 已按序解码,直接取 words,勿用原始 data 朴素重解析
    return { ok: true, status: r.status, values: r.status === "data" && Array.isArray(r.words) ? r.words : [] };
  }

  /**
   * FX 编程口写(CMD "1")。
   * 位软元件(X/Y/M/S/T/C):值按 8 点/字节 LSB 打包(与读解包对称),写入字节序列;
   * 字软元件:每字低字节在前(rust 端处理)。
   */
  async function progWrite({ device, address, values, timeoutMs }) {
    const isBitDevice = /^(X|Y|M|S|T|C)$/i.test(String(device ?? "").trim());
    let payloadValues = values;
    if (isBitDevice) {
      // X/Y 编号为八进制书写(与 rust fx_prog_parse_number 一致);字节内偏移 = 编号%8,
      // 点 i 落在 (编号+i)/8 字节的 (编号+i)%8 位(与读解包对称;整字节写入会覆写同字节邻位,协议如此)
      const radix = /^(X|Y)$/i.test(String(device).trim()) ? 8 : 10;
      const start = Number.parseInt(String(address ?? "0").trim(), radix) || 0;
      const bytes = [];
      for (let i = 0; i < values.length; i++) {
        if (!values[i]) continue;
        const pos = start + i;
        const byteIdx = pos >> 3;
        while (bytes.length <= byteIdx) bytes.push(0);
        bytes[byteIdx] |= 1 << (pos & 7);
      }
      payloadValues = bytes;
    }
    const frame = (await request("fx_prog_build_write", { device, address, values: payloadValues })).frame;
    const rx = await transact({ request: frame, timeoutMs: timeoutMs ?? 1000, framing: "fx" });
    const r = await request("fx_prog_parse", { frame: rx.rx });
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

module.exports = { createFxSerialService, parseFxLinksData, parseFxProgData, parseFxProgBits, DEFAULT_FX_SERIAL };
