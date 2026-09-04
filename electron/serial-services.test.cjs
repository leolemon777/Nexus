/**
 * 串口协议服务层单元测试 —— fx / hostlink / ppi / rk512 / uss / panasonic。
 *
 * 背景(2026-08-25):fx-serial-service 因无测试文件漏掉 fx_prog_parse 信封字段错位(response vs frame),
 * 真机首读即失败。本文件为全部无测试串口服务补上"服务编排 + rust-core 信封字段"双层断言:
 * 桩 request 显式校验每条命令的载荷字段名——字段错位立即抛错,同类缺陷无法再溜过。
 * 串口收发(transact)与 COM 句柄按各服务签名打桩;真实帧语义由 rust-core/tests 各协议 e2e 负责。
 */

const test = require("node:test");
const assert = require("node:assert");
const { createFxSerialService } = require("./fx-serial-service.cjs");
const { createOmronHostLinkSerialService } = require("./omron-hostlink-serial-service.cjs");
const { createPpiSerialService } = require("./ppi-serial-service.cjs");
const { createRk512SerialService } = require("./rk512-serial-service.cjs");
const { createUssSerialService } = require("./uss-serial-service.cjs");
const { createPanasonicSerialService } = require("./panasonic-serial-service.cjs");

/** 记录调用并按命令校验载荷字段的 request 桩:字段集不符立即抛错(信封守卫)。 */
function recordingRequest(handlers) {
  const calls = [];
  const request = async (cmd, payload) => {
    calls.push([cmd, payload]);
    const h = handlers[cmd];
    if (!h) throw new Error(`unexpected command: ${cmd}`);
    const got = Object.keys(payload ?? {}).sort().join(",");
    const want = Object.keys(h.fields).sort().join(",");
    assert.strictEqual(got, want, `${cmd} 载荷字段错位: 期望[${want}] 实际[${got}]`);
    for (const [k, v] of Object.entries(h.fields)) {
      assert.ok(typeof payload[k] === v, `${cmd}.${k} 类型错误: 期望 ${v} 实际 ${typeof payload[k]}`);
    }
    return typeof h.reply === "function" ? h.reply(payload) : h.reply;
  };
  return { request, calls };
}

function makeTransact(scripts) {
  const calls = [];
  let i = 0;
  const transact = async (arg) => {
    calls.push(arg);
    const s = scripts[Math.min(i++, scripts.length - 1)];
    return typeof s === "function" ? s(arg) : s;
  };
  return { transact, calls };
}

// ─── FX 编程口(2026-08-25 缺陷回归) ───────────────────────────────

test("fx progRead: fx_prog_parse 信封必须是 frame 字段(真机缺陷回归)", async () => {
  const { request, calls } = recordingRequest({
    fx_prog_build_read: { fields: { device: "string", address: "string", words: "number" }, reply: { frame: [2, 49, 48, 48, 48, 48, 67, 3, 54, 55] } },
    // 桩在字段校验阶段就会拒绝 {response:...};只有 {frame:...} 能到达这里
    fx_prog_parse: { fields: { frame: "object" }, reply: { status: "data", data: [51, 52, 49, 50], words: [4660] } },
  });
  const { transact } = makeTransact([{ rx: [6] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.progRead({ device: "D", address: "0", words: 1 });
  assert.strictEqual(r.ok, true);
  assert.deepStrictEqual(r.values, [0x1234]);
  assert.deepStrictEqual(calls.map((c) => c[0]), ["fx_prog_build_read", "fx_prog_parse"]);
});

test("fx progWrite: fx_prog_parse 同样走 frame 字段", async () => {
  const { request, calls } = recordingRequest({
    fx_prog_build_write: { fields: { device: "string", address: "string", values: "object" }, reply: { frame: [2, 49, 49, 48, 48, 48, 48, 52, 52, 51, 3, 54, 54] } },
    fx_prog_parse: { fields: { frame: "object" }, reply: { status: "ack", data: [], words: [] } },
  });
  const { transact } = makeTransact([{ rx: [6] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.progWrite({ device: "D", address: "0", values: [0x1234] });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(calls[1][0], "fx_prog_parse");
});

test("fx linksRead: fx_links_parse 契约是 response 字段(与 prog 相反,双向守卫)", async () => {
  const { request, calls } = recordingRequest({
    fx_links_read: { fields: { station: "number", device: "string", head: "number", points: "number", delay: "number" }, reply: { frame: [5, 48, 48, 48, 48, 66, 82, 48, 48, 48, 48, 48, 50, 68, 3, 55, 50] } },
    fx_links_parse: { fields: { response: "object" }, reply: { status: "data", dataAscii: "1234" } },
  });
  const { transact } = makeTransact([{ rx: [2, 49, 50, 51, 52, 3, 55, 50] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.linksRead({ station: 0, device: "D", head: 0, points: 1 });
  assert.strictEqual(r.ok, true);
  assert.deepStrictEqual(r.values, [0x1234]);
  assert.strictEqual(calls[1][0], "fx_links_parse");
});

test("fx progRead: NAK 返回错误码与错误文案", async () => {
  const { request } = recordingRequest({
    fx_prog_build_read: { fields: { device: "string", address: "string", words: "number" }, reply: { frame: [2] } },
    fx_prog_parse: { fields: { frame: "object" }, reply: { status: "nak", errorCode: "06", errorMessage: "软元件编号无效" } },
  });
  const { transact } = makeTransact([{ rx: [0x15, 54, 54] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.progRead({ device: "D", address: "0", words: 1 });
  assert.strictEqual(r.ok, false);
  assert.strictEqual(r.errorCode, "06");
  assert.strictEqual(r.errorMessage, "软元件编号无效");
});

// ─── 欧姆龙 HostLink C-mode ──────────────────────────────────────

test("hostlink read: build→transact(hostlink framing)→parse(frame) 全链", async () => {
  const { request, calls } = recordingRequest({
    hostlink_build_cmode_read: { fields: { station: "number", dmStart: "number", wordCount: "number" }, reply: { frame: [0x40, 0x30, 0x30, 0x52, 0x44, 0x30, 0x30, 0x30, 0x32, 0x35, 0x46, 0x0d] } },
    hostlink_parse_cmode_read: { fields: { frame: "object" }, reply: { words: [0x1234] } },
  });
  const { transact, calls: tcalls } = makeTransact([{ rx: [0x40, 0x30, 0x30, 0x52, 0x52, 0x30, 0x30, 0x31, 0x32, 0x33, 0x34, 0x2a, 0x0d], elapsedMs: 20 }]);
  const svc = createOmronHostLinkSerialService({ request, transact, getSerialStatus: () => ({ isOpen: true, config: {} }) });
  const r = await svc.read({ station: 0, address: "D0", count: 1 });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(calls[0][0], "hostlink_build_cmode_read");
  assert.strictEqual(calls[1][0], "hostlink_parse_cmode_read");
  assert.strictEqual(tcalls[0].framing, "hostlink");
});

// ─── 西门子 PPI(两拍事务) ────────────────────────────────────────

test("ppi read: E5 确认拍 + SA 确认帧 + 数据帧解析", async () => {
  const { request, calls } = recordingRequest({
    ppi_build_read: { fields: { station: "number", master: "number", address: "string", count: "number" }, reply: { frame: [0x68, 1, 1, 0x6c, 0x32, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0e, 0x00, 0x00, 0x04, 0x01, 0x12, 0x0a, 0x10, 0x02, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0xf9, 0x16] } },
    ppi_build_sa_confirm: { fields: { station: "number", master: "number" }, reply: { frame: [0x10, 0x02, 0x00, 0x5c, 0x16, 0xe6] } },
    ppi_parse_read_response: { fields: { response: "object" }, reply: { destination: 0, source: 2, functionCode: 0x08, data: [0x12, 0x34] } },
  });
  const { transact, calls: tcalls } = makeTransact([
    { rx: [0xe5], elapsedMs: 15 },
    { rx: [0x68, 0x11, 0x11, 0x68, 0x00, 0x02, 0x08, 0x12, 0x34, 0x00, 0x00, 0x16], elapsedMs: 18 },
  ]);
  const svc = createPpiSerialService({ request, transact, getSerialStatus: () => ({ isOpen: true, config: { baudRate: 9600, dataBits: 8, parity: "even", stopBits: 1 } }) });
  const r = await svc.read({ station: 2, master: 0, address: "VB0", count: 1 });
  assert.strictEqual(r.ok, true);
  assert.deepStrictEqual(calls.map((c) => c[0]), ["ppi_build_read", "ppi_build_sa_confirm", "ppi_parse_read_response"]);
  assert.strictEqual(tcalls.length, 2);
  assert.strictEqual(tcalls[0].framing, "ppi");
});

// ─── 西门子 RK512(3964R 链路拍 + 数据拍) ─────────────────────────

test("rk512 read: DLE 链路拍 + RK512 数据帧解析", async () => {
  const { request, calls } = recordingRequest({
    rk512_build_read: { fields: { area: "string", db: "number", offset: "number", count: "number" }, reply: { frame: [0x02, 0x31, 0x30, 0x30, 0x31, 0x30, 0x30, 0x30, 0x30, 0x30, 0x32, 0x03] } },
    rk512_parse_response: { fields: { frame: "object" }, reply: { error: 0, func: 0x01, db: 1, offset: 0, data: [0x12, 0x34] } },
  });
  const { transact, calls: tcalls } = makeTransact([
    { rx: [0x10], elapsedMs: 12 },
    { rx: [0x02, 0x31, 0x30, 0x30, 0x31, 0x30, 0x30, 0x30, 0x30, 0x31, 0x32, 0x33, 0x34, 0x03], elapsedMs: 20 },
    { rx: [0x10], elapsedMs: 8 },
  ]);
  const svc = createRk512SerialService({ request, transact, getSerialStatus: () => ({ isOpen: true, config: {} }) });
  const r = await svc.read({ address: "DB1.W0", count: 1 });
  assert.strictEqual(r.ok, true);
  assert.deepStrictEqual(calls.map((c) => c[0]), ["rk512_build_read", "rk512_parse_response"]);
  assert.strictEqual(tcalls[0].request.length, 1);
});

// ─── 西门子 USS ──────────────────────────────────────────────────

test("uss read: build(station/param/pzd)→transact(uss)→parse(frame) 站号核对", async () => {
  const { request, calls } = recordingRequest({
    uss_build_request: { fields: { station: "number", param: "number", pzd: "object" }, reply: { frame: [0x02, 0x01, 0x0b, 0x00, 0x02, 0xbc, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xb5, 0x5e] } },
    uss_parse_response: { fields: { frame: "object" }, reply: { station: 1, pkeAkCode: 0x11, pkeAk: "应答报文", pkeAkMessage: "参数值应答", pzd: [0x12, 0x34] } },
  });
  const { transact, calls: tcalls } = makeTransact([{ rx: [0x02, 0x01, 0x01, 0x12, 0xbc, 0x00, 0x00, 0x12, 0x34], elapsedMs: 30 }]);
  const svc = createUssSerialService({ request, transact, getSerialStatus: () => ({ isOpen: true, config: {} }) });
  const r = await svc.read({ station: 1, param: 700, pzdBytes: 4 });
  assert.strictEqual(r.ok, true);
  assert.deepStrictEqual(calls.map((c) => c[0]), ["uss_build_request", "uss_parse_response"]);
  assert.strictEqual(tcalls[0].framing, "uss");
});

// ─── 松下 MEWTOCOL-COM ───────────────────────────────────────────

test("panasonic readData: build→transact(mewtocol)→parse(station/expectedCommand/expectedHeader/response)", async () => {
  const { request, calls } = recordingRequest({
    panasonic_build_read: { fields: { station: "number", address: "string", wordCount: "number" }, reply: { frame: [0x25, 0x30, 0x31, 0x52, 0x44, 0x44, 0x30, 0x30, 0x30, 0x31, 0x37, 0x0d] } },
    panasonic_parse_response: { fields: { station: "number", expectedCommand: "string", expectedHeader: "string", response: "object" }, reply: { values: [0x1234] } },
  });
  const { transact, calls: tcalls } = makeTransact([{ rx: [0x25, 0x30, 0x31, 0x52, 0x44, 0x31, 0x32, 0x33, 0x34, 0x37, 0x0d], elapsedMs: 25 }]);
  const svc = createPanasonicSerialService({ request, transact, getSerialStatus: () => ({ isOpen: true, config: {} }) });
  const r = await svc.read({ station: 1, address: "DT0", wordCount: 1 });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.address, "DT0");
  assert.deepStrictEqual(calls.map((c) => c[0]), ["panasonic_build_read", "panasonic_parse_response"]);
  assert.strictEqual(calls[1][1].expectedCommand, "RD");
  assert.strictEqual(tcalls[0].framing, "mewtocol");
});

// ─── FX 编程口位软元件(2026-08-25 真机缺陷 4:M5 强制 ON 却显示 M0 ON) ──

test("fx progRead 位软元件:M0×6 请求按点数折算字,响应按 8 点/字节 LSB 解包(真机缺陷回归)", async () => {
  const { request, calls } = recordingRequest({
    fx_prog_build_read: {
      fields: { device: "string", address: "string", words: "number" },
      reply: (p) => {
        // 位软元件 6 点 → 请求 1 字(2 字节,16 位,只读多读)
        if (p.device === "M" && p.words === 1) return { frame: [2] };
        throw new Error("位读请求字数折算错误: " + JSON.stringify(p));
      },
    },
    // byte0=0x20(bit5=M5 ON) byte1=0x00 → ASCII "2000"
    fx_prog_parse: { fields: { frame: "object" }, reply: { status: "data", data: [50, 48, 48, 48], words: [0x0020] } },
  });
  const { transact } = makeTransact([{ rx: [6] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.progRead({ device: "M", address: "0", words: 6 });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.isBit, true);
  assert.deepStrictEqual(r.values, [0, 0, 0, 0, 0, 1], "M5 应为 1(ON),其余 0 —— 用户真机场景");
});

test("fx progRead 位软元件跨字节:M0×10,byte0=0x01(M0) byte1=0x02(M9)", async () => {
  const { request } = recordingRequest({
    fx_prog_build_read: { fields: { device: "string", address: "string", words: "number" }, reply: { frame: [2] } },
    fx_prog_parse: { fields: { frame: "object" }, reply: { status: "data", data: [48, 49, 48, 50], words: [0x0102] } },
  });
  const { transact } = makeTransact([{ rx: [6] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.progRead({ device: "X", address: "0", words: 10 });
  assert.deepStrictEqual(r.values, [1, 0, 0, 0, 0, 0, 0, 0, 0, 1], "X0 与 X9 应为 1");
});

test("fx progRead 字软元件不受影响:D0×1 仍走 words 语义", async () => {
  const { request, calls } = recordingRequest({
    fx_prog_build_read: { fields: { device: "string", address: "string", words: "number" }, reply: (p) => {
      if (p.device === "D" && p.words === 1) return { frame: [2] };
      throw new Error("字读请求不应折算: " + JSON.stringify(p));
    } },
    fx_prog_parse: { fields: { frame: "object" }, reply: { status: "data", data: [51, 52, 49, 50], words: [4660] } },
  });
  const { transact } = makeTransact([{ rx: [6] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.progRead({ device: "D", address: "0", words: 1 });
  assert.strictEqual(r.isBit, undefined);
  assert.deepStrictEqual(r.values, [4660]);
});

// ─── FX 编程口位写打包 + 位地址(2026-08-26 HSL 对照发现的缺陷 5 回归) ──

test("fx progWrite 位软元件:值按 8 点/字节 LSB 打包后写入(缺陷 5 回归)", async () => {
  const { request, calls } = recordingRequest({
    fx_prog_build_write: {
      fields: { device: "string", address: "string", values: "object" },
      reply: (p) => {
        // M0 起 5 点 [0,0,0,0,0,1,0,0]... 用户场景:M5=1 → 打包 1 字节 0x20
        if (p.device === "M" && JSON.stringify(p.values) === "[32]") return { frame: [2] };
        throw new Error("位写打包错误: " + JSON.stringify(p));
      },
    },
    fx_prog_parse: { fields: { frame: "object" }, reply: { status: "ack", data: [], words: [] } },
  });
  const { transact } = makeTransact([{ rx: [6] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.progWrite({ device: "M", address: "5", values: [1] });
  assert.strictEqual(r.ok, true);
});

test("fx progWrite 位软元件跨字节:16 点 → 2 字节", async () => {
  const { request } = recordingRequest({
    fx_prog_build_write: {
      fields: { device: "string", address: "string", values: "object" },
      reply: (p) => {
        // X0=1(bit0) 与 X10(=8dec 位 8 → byte1 bit0)=1 → [0x01, 0x01]
        if (p.device === "X" && JSON.stringify(p.values) === "[1,1]") return { frame: [2] };
        throw new Error("跨字节打包错误: " + JSON.stringify(p));
      },
    },
    fx_prog_parse: { fields: { frame: "object" }, reply: { status: "ack", data: [], words: [] } },
  });
  const { transact } = makeTransact([{ rx: [6] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.progWrite({ device: "X", address: "0", values: [1, 0, 0, 0, 0, 0, 0, 0, 1] });
  assert.strictEqual(r.ok, true);
});

test("fx progWrite 字软元件不受影响:D0 写字值原样传递", async () => {
  const { request } = recordingRequest({
    fx_prog_build_write: {
      fields: { device: "string", address: "string", values: "object" },
      reply: (p) => {
        if (p.device === "D" && JSON.stringify(p.values) === "[4660]") return { frame: [2] };
        throw new Error("字写不应打包: " + JSON.stringify(p));
      },
    },
    fx_prog_parse: { fields: { frame: "object" }, reply: { status: "ack", data: [], words: [] } },
  });
  const { transact } = makeTransact([{ rx: [6] }]);
  const svc = createFxSerialService({ request, transact });
  const r = await svc.progWrite({ device: "D", address: "0", values: [4660] });
  assert.strictEqual(r.ok, true);
});
