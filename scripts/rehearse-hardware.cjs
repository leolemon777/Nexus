"use strict";

/**
 * 桌面实机验证脚本（安全版）。
 * 默认 FX/485 全流程只读：check → adp-read → scan；现场三硬件通讯对象使用
 * all-field：check → adp-read → smart-read → scan。任何 PLC 写入都必须单独执行，
 * 并带显式确认参数。详见 docs/spec-plan-desktop-demo-hardware-p0.md。
 */

const os = require("node:os");
const net = require("node:net");
const path = require("node:path");

const ADP_HOST = process.env.NEXUS_ADP_HOST || "192.168.1.20";
const ADP_PORT = Number(process.env.NEXUS_ADP_PORT || 5000);
const NOTES_PATH = path.join(__dirname, "..", "docs", "implementation-notes.md");
const NOTES_ANCHOR = "结果回填本节。";
const passRecordLines = [];

function arg(name, fallback = undefined) {
  const argv = process.argv.slice(2);
  const i = argv.indexOf(`--${name}`);
  return i >= 0 && argv[i + 1] != null ? argv[i + 1] : fallback;
}

function hex(value) {
  return `0x${(Number(value) & 0xffff).toString(16).toUpperCase().padStart(4, "0")}`;
}

function parseIntegerArg(name, fallback, min, max) {
  const raw = arg(name);
  const value = raw == null ? fallback : Number(raw);
  if (!Number.isInteger(value) || value < min || value > max) {
    throw new Error(`--${name} 必须是 ${min}..${max} 的整数`);
  }
  return value;
}

function parseNumberArg(name, fallback) {
  const raw = arg(name);
  const value = raw == null ? fallback : Number(raw);
  if (!Number.isFinite(value)) throw new Error(`--${name} 必须是有效数字`);
  return value;
}

function parseHexBytesArg(name) {
  const raw = arg(name);
  if (raw == null || String(raw).trim() === "") return null;
  const clean = String(raw).trim().replace(/^0x/i, "").replace(/[\s:_-]/g, "");
  if (!/^[0-9a-f]+$/i.test(clean) || clean.length % 2 !== 0) {
    throw new Error(`--${name} 必须是偶数位十六进制字节，例如 1234 或 12:34`);
  }
  return Buffer.from(clean, "hex");
}

function bytesHex(values) {
  return Buffer.from(values ?? []).toString("hex").toUpperCase();
}

function confirmSafeWrite() {
  return String(arg("confirm-safe-write", "")).toUpperCase() === "YES";
}

function flushPassRecords() {
  if (passRecordLines.length === 0) return;
  try {
    const fs = require("node:fs");
    const stamp = new Date().toISOString().replace("T", " ").slice(0, 19);
    const block = `\n- **${stamp} 实机 PASS 记录**\n${passRecordLines.map((line) => `  - ${line}`).join("\n")}`;
    let contents = fs.readFileSync(NOTES_PATH, "utf8");
    if (contents.includes(NOTES_ANCHOR)) contents = contents.replace(NOTES_ANCHOR, NOTES_ANCHOR + "\n" + block);
    else contents += `\n### 真机执行记录\n${block}\n`;
    fs.writeFileSync(NOTES_PATH, contents);
    console.log(`  [记录] 已回填 implementation-notes（仅 PASS，${passRecordLines.length} 行）`);
  } catch (error) {
    console.log(`  [记录] 回填失败：${error.message}（PASS 结果仍保留在终端输出）`);
  }
}

async function listComPorts() {
  const { SerialPort } = require("serialport");
  return SerialPort.list();
}

function findPort(ports, requested) {
  if (!requested) return null;
  return ports.find((port) => String(port.path).toLowerCase() === String(requested).toLowerCase()) ?? null;
}

function describePort(port) {
  return `${port.path} ${port.friendlyName || port.manufacturer || ""}`.trim();
}

function localSubnetOk() {
  for (const interfaces of Object.values(os.networkInterfaces())) {
    for (const iface of interfaces ?? []) {
      if (iface.family === "IPv4" && iface.address.startsWith("192.168.1.")) return true;
    }
  }
  return false;
}

function createCore() {
  const { RustCoreClient } = require("../electron/rust-core-client.cjs");
  const binaryPath = process.env.NEXUS_RUST_CORE_PATH
    || path.join(__dirname, "..", "rust-core", "target", "release", "nexus-rust-core.exe");
  return new RustCoreClient({ binaryPath });
}

// 帧级终裁：TCP connect 后发真实 1E 读帧。TUN 代理假连通不会返回有效 81H 响应。
async function probeAdpFrame(host, port, timeoutMs = 2000) {
  const frame = Buffer.from("01FF0A0064000000442A0200", "hex"); // 1E 字读 D100×2
  return new Promise((resolve) => {
    let settled = false;
    let response = Buffer.alloc(0);
    const socket = net.createConnection({ host, port });
    const finish = (state, detail) => {
      if (settled) return;
      settled = true;
      socket.destroy();
      resolve({ state, detail });
    };
    socket.setTimeout(timeoutMs, () => finish(response.length ? "unknown" : "silent", "timeout"));
    socket.once("error", (error) => finish("unreachable", error.code || error.message));
    socket.once("connect", () => socket.write(frame));
    socket.on("data", (data) => {
      response = Buffer.concat([response, data]);
      if (response.length >= 2) {
        finish(response[0] === 0x81 ? "plc" : "unknown", response.subarray(0, 8).toString("hex"));
      }
    });
  });
}

function printAdpProblem(probe) {
  if (probe.state === "silent") {
    console.log("  [ADP ] TCP 可连接但 1E 帧无响应：疑似 TUN 代理假连通或端口不是 MC 协议");
  } else if (probe.state === "unknown") {
    console.log(`  [ADP ] 收到非 1E 响应（${probe.detail}）：核对端口与协议配置`);
  } else {
    console.log(`  [ADP ] 不可达（${probe.detail}）：检查参数下装、网线、静态 IP 与路由`);
  }
}

async function runCheck() {
  console.log("=== 实机前置检查（只读）===");
  const ports = await listComPorts();
  if (ports.length === 0) console.log("  [COM ] 无串口");
  else ports.forEach((port) => console.log(`  [COM ] ${describePort(port)}`));

  const requestedRs485 = arg("rs485-com", arg("com"));
  const rs485 = findPort(ports, requestedRs485);
  if (!requestedRs485) console.log("  [485 ] 未指定 --rs485-com；为避免把 SC09 当作 USB-485，不自动猜测");
  else if (!rs485) console.log(`  [485 ] 指定串口 ${requestedRs485} 不存在`);
  else console.log(`  [485 ] 已确认 ${describePort(rs485)} ✓`);

  const subnetReady = localSubnetOk();
  console.log(`  [NET ] ${subnetReady ? "已有" : "没有"} 192.168.1.x 地址${subnetReady ? " ✓" : "；请将 PLC 专用网卡设为 192.168.1.10/24"}`);
  const probe = await probeAdpFrame(ADP_HOST, ADP_PORT);
  if (probe.state === "plc") console.log(`  [ADP ] ${ADP_HOST}:${ADP_PORT} 1E 帧响应正常 ✓`);
  else printAdpProblem(probe);

  const ready = Boolean(rs485) && subnetReady && probe.state === "plc";
  console.log(ready ? "  结果：PASS —— 可执行安全 all" : "  结果：NOT READY —— 不进入后续实机步骤");
  return ready ? 0 : 2;
}

async function runAdpRead() {
  const expect = parseIntegerArg("expect", 0x1234, 0, 0xffff);
  console.log(`=== ADP 只读首连（A-1E ${ADP_HOST}:${ADP_PORT}，期望 D100=${hex(expect)}）===`);
  const probe = await probeAdpFrame(ADP_HOST, ADP_PORT);
  if (probe.state !== "plc") {
    printAdpProblem(probe);
    console.log("  结果：NOT READY —— 未发送任何写命令");
    return 2;
  }

  const core = createCore();
  const failures = [];
  let connectMs = null;
  let words = [];
  let bits = [];
  try {
    await core.start();
    const startedAt = Date.now();
    await core.request("open_mc_1e_tcp", { connectionId: "rehearse-read", host: ADP_HOST, port: ADP_PORT });
    connectMs = Date.now() - startedAt;
    const readWords = await core.request("mc_1e_read", { connectionId: "rehearse-read", address: "D100", points: 2 });
    words = readWords?.words ?? readWords?.values ?? [];
    const readBits = await core.request("mc_1e_read", { connectionId: "rehearse-read", address: "M0", points: 8 });
    bits = readBits?.bits ?? [];
    console.log(`  [1] 连接耗时 ${connectMs} ms`);
    console.log(`  [2] D100~D101 = ${words.map(hex).join(", ")}`);
    console.log(`  [3] M0~M7（只读）= ${bits.join("")}`);
    if (words.length < 2) failures.push("D100~D101 返回点数不足");
    else if (words[0] !== expect) failures.push(`D100=${hex(words[0])} ≠ 期望 ${hex(expect)}`);
  } catch (error) {
    failures.push(`通讯异常：${error.message || error}`);
  } finally {
    try { await core.request("shutdown", {}); } catch { /* 进程退出兜底 */ }
  }

  if (failures.length > 0) {
    console.log(`  结果：FAIL —— ${failures.join(" | ")}（全程未写 PLC）`);
    return 1;
  }
  passRecordLines.push(`ADP-READ PASS：${ADP_HOST}:${ADP_PORT}，${connectMs} ms，D100~D101=${words.map(hex).join("/")}，M0~M7=${bits.join("")}`);
  console.log("  结果：PASS —— A-1E 只读首连通过，全程未写 PLC");
  return 0;
}

async function runAdpWrite() {
  const address = String(arg("address", "")).toUpperCase();
  const addressMatch = /^D(\d+)$/.exec(address);
  const addressNumber = addressMatch ? Number(addressMatch[1]) : -1;
  if (!addressMatch || !Number.isInteger(addressNumber) || addressNumber < 0 || addressNumber > 7999) {
    console.log("  结果：拒绝执行 —— --address 必须显式指定 D0..D7999，例如 D300；禁止触碰 D8000+ 特殊寄存器");
    return 2;
  }
  if (!confirmSafeWrite()) {
    console.log("  结果：拒绝执行 —— 请先确认 PLC 程序、输出隔离和备用寄存器，再加 --confirm-safe-write YES");
    return 2;
  }
  const value = parseIntegerArg("value", 0x5a5a, 0, 0xffff);
  console.log(`=== ADP 受控写入（${address}=${hex(value)}，完成后恢复原值）===`);
  const probe = await probeAdpFrame(ADP_HOST, ADP_PORT);
  if (probe.state !== "plc") {
    printAdpProblem(probe);
    return 2;
  }

  const core = createCore();
  const failures = [];
  let originalValue;
  let writeAttempted = false;
  let restored = false;
  try {
    await core.start();
    await core.request("open_mc_1e_tcp", { connectionId: "rehearse-write", host: ADP_HOST, port: ADP_PORT });
    const original = await core.request("mc_1e_read", { connectionId: "rehearse-write", address, points: 1 });
    originalValue = (original?.words ?? original?.values ?? [])[0];
    if (!Number.isInteger(originalValue)) throw new Error(`无法读取 ${address} 原值，已中止写入`);
    console.log(`  [1] 原值 ${address}=${hex(originalValue)}`);

    writeAttempted = true;
    await core.request("mc_1e_write", { connectionId: "rehearse-write", address, values: [value] });
    const verify = await core.request("mc_1e_read", { connectionId: "rehearse-write", address, points: 1 });
    const writtenValue = (verify?.words ?? verify?.values ?? [])[0];
    console.log(`  [2] 写后回读 ${address}=${hex(writtenValue)}`);
    if (writtenValue !== value) failures.push(`写后回读 ${hex(writtenValue)} ≠ ${hex(value)}`);
  } catch (error) {
    failures.push(`受控写入异常：${error.message || error}`);
  } finally {
    if (writeAttempted && Number.isInteger(originalValue)) {
      try {
        await core.request("mc_1e_write", { connectionId: "rehearse-write", address, values: [originalValue] });
        const restoredRead = await core.request("mc_1e_read", { connectionId: "rehearse-write", address, points: 1 });
        const restoredValue = (restoredRead?.words ?? restoredRead?.values ?? [])[0];
        restored = restoredValue === originalValue;
        console.log(`  [3] 恢复核对 ${address}=${hex(restoredValue)} ${restored ? "✓" : "✗"}`);
        if (!restored) failures.push(`原值恢复失败：期望 ${hex(originalValue)}，实际 ${hex(restoredValue)}`);
      } catch (error) {
        failures.push(`原值恢复异常：${error.message || error}`);
      }
    }
    try { await core.request("shutdown", {}); } catch { /* 进程退出兜底 */ }
  }

  if (failures.length > 0 || !restored) {
    console.log(`  结果：FAIL —— ${failures.join(" | ") || "原值未恢复"}`);
    return 1;
  }
  passRecordLines.push(`ADP-WRITE PASS：${address} 写入 ${hex(value)} 并恢复原值 ${hex(originalValue)}`);
  console.log("  结果：PASS —— 写读核对通过且原值已恢复");
  return 0;
}

async function runSmartRead() {
  const host = String(arg("smart-host", "")).trim();
  const address = String(arg("smart-address", "")).trim().toUpperCase();
  if (!host || !address) {
    console.log("  结果：NOT READY —— 必须显式指定 --smart-host 和 --smart-address，不自动猜测 PLC IP 或内存区");
    return 2;
  }
  const expected = parseHexBytesArg("smart-expect-hex");
  if (!expected) {
    console.log("  结果：NOT READY —— 必须用 --smart-expect-hex 给出 Micro/WIN SMART 当前监视值，避免把任意返回误记为验收 PASS");
    return 2;
  }
  const port = parseIntegerArg("smart-port", 102, 1, 65535);
  const rack = parseIntegerArg("smart-rack", 0, 0, 7);
  const count = parseIntegerArg("smart-count", 1, 1, 65535);
  const slotRaw = arg("smart-slot");
  const slots = slotRaw == null ? [0, 1] : [parseIntegerArg("smart-slot", 0, 0, 31)];
  console.log(`=== S7-200 SMART 只读首连（${host}:${port}，${address}×${count}，期望 HEX ${expected.toString("hex").toUpperCase()}）===`);
  console.log(`  [前置] V3 CPU 须在 STEP 7-Micro/WIN SMART V3 启用 Put/Get Server 并下载；首轮脚本不写 PLC`);

  const core = createCore();
  const attemptErrors = [];
  let connectedSlot = null;
  let pduSize = null;
  let data = [];
  try {
    await core.start();
    await core.request("s7_parse_address", { address });
    for (const slot of slots) {
      const connectionId = `rehearse-smart-${slot}`;
      const startedAt = Date.now();
      try {
        const connected = await core.request("open_s7_connection", {
          connectionId,
          host,
          port,
          rack,
          slot,
          connType: 1,
        });
        connectedSlot = slot;
        pduSize = connected.pduSize;
        console.log(`  [连接] rack ${rack}/slot ${slot}，COTP + S7 PDU ${pduSize}B，${Date.now() - startedAt} ms ✓`);
        const read = await core.request("s7_read", {
          connectionId,
          items: [{ address, count }],
        });
        const item = read?.items?.[0];
        if (!item) throw new Error("S7 读取无返回项");
        if (item.returnCode !== 0xff) {
          throw new Error(`S7 读取被拒 0x${Number(item.returnCode).toString(16).toUpperCase().padStart(2, "0")}：${item.returnCodeMessage || "未知错误"}`);
        }
        data = item.data ?? [];
        break;
      } catch (error) {
        attemptErrors.push(`rack ${rack}/slot ${slot}: ${error.message || error}`);
        try { await core.request("close_connection", { connectionId }); } catch { /* 失败连接可能未注册 */ }
      }
    }
  } catch (error) {
    attemptErrors.push(error.message || String(error));
  } finally {
    try { await core.request("shutdown", {}); } catch { /* 进程退出兜底 */ }
  }

  if (connectedSlot == null || data.length === 0) {
    console.log(`  结果：FAIL —— ${attemptErrors.join(" | ") || "未完成 S7 读取"}`);
    console.log("  排查：CPU 型号/固件、Micro/WIN SMART 版本、Put/Get Server、实际 IP、TUN 直连、rack/slot 与读取地址");
    return 1;
  }
  const actualHex = bytesHex(data);
  console.log(`  [读取] ${address}×${count} = HEX ${actualHex}`);
  if (!Buffer.from(data).equals(expected)) {
    console.log(`  结果：FAIL —— PLC 返回 ${actualHex}，与 Micro/WIN SMART 监视值 ${expected.toString("hex").toUpperCase()} 不一致（全程未写 PLC）`);
    return 1;
  }
  passRecordLines.push(`SMART-READ PASS：${host}:${port}，rack ${rack}/slot ${connectedSlot}，PDU ${pduSize}B，${address}×${count}=HEX ${actualHex}`);
  console.log("  结果：PASS —— SMART 协议握手与已知值只读核对通过，全程未写 PLC");
  return 0;
}

async function runScan() {
  const requestedCom = arg("rs485-com", arg("com"));
  const stationStart = parseIntegerArg("station-start", 1, 1, 247);
  const stationEnd = parseIntegerArg("station-end", 16, 1, 247);
  const startAddress = parseIntegerArg("start-address", 0, 0, 65535);
  const quantity = parseIntegerArg("quantity", 8, 1, 125);
  const scaleRaw = arg("scale");
  const scale = scaleRaw == null ? null : parseNumberArg("scale", null);
  console.log("=== 485 Modbus RTU 全矩阵扫描 ===");
  const ports = await listComPorts();
  if (!requestedCom) {
    console.log("  结果：NOT READY —— 必须用 --rs485-com COMx 明确 USB-485，避免误用 SC09");
    ports.forEach((port) => console.log(`    ${describePort(port)}`));
    return 2;
  }
  const selected = findPort(ports, requestedCom);
  if (!selected) {
    console.log(`  结果：NOT READY —— 指定串口 ${requestedCom} 不存在`);
    ports.forEach((port) => console.log(`    ${describePort(port)}`));
    return 2;
  }
  console.log(`  [COM ] ${describePort(selected)}`);

  const { SerialService } = require("../electron/serial-service.cjs");
  const { readHoldingRegistersOnce } = require("../electron/modbus-master-service.cjs");
  const { createModbusScanService } = require("../electron/modbus-scan-service.cjs");
  const core = createCore();
  const serialService = new SerialService();
  const ctx = { rustCore: core, serialService, ensureRustCore: async () => core };
  const scan = createModbusScanService({ serialService, readHoldingRegistersOnce });

  try {
    await core.start();
    const result = await scan.scanAll(ctx, {
      comPort: selected.path,
      stationStart,
      stationEnd,
      bauds: [9600, 19200, 38400, 115200, 4800],
      parities: ["none", "even", "odd"],
      timeoutMs: 200,
      mode: "firstHit",
    }, (progress) => {
      const parity = progress.parity === "none" ? "8N1" : progress.parity === "even" ? "8E1" : "8O1";
      process.stdout.write(`\r  扫描 ${progress.baud}·${parity} 档 ${progress.comboIndex + 1}/${progress.totalCombos} · 站 ${progress.stationId} · ${(progress.elapsedMs / 1000).toFixed(1)}s   `);
    });
    console.log("");

    if (!result.found) {
      console.log(`  结果：FAIL —— 已探测 ${result.triedProbes}/${result.totalProbes} 个组合，未发现从站`);
      console.log("  排查：供电、A/B 反接、GND、站号范围；未命中结果不会写入 PASS 记录");
      return 1;
    }

    const hit = result.hit;
    console.log(`  [命中] 站号 ${hit.stationId} · ${hit.baudRate} · ${hit.parityLabel} · 首响应 ${hit.firstResponseMs ?? "—"} ms`);
    await serialService.open({
      portName: selected.path,
      baudRate: hit.baudRate,
      dataBits: 8,
      parity: hit.parity,
      stopBits: "1",
      flowControl: "none",
      readTimeoutMs: 1000,
      writeTimeoutMs: 1000,
      dtrMode: "preserve",
      rtsMode: "preserve",
    });
    const read = await readHoldingRegistersOnce(ctx, {
      unitId: hit.stationId,
      startAddress,
      quantity,
      timeoutMs: 1000,
    });
    const registers = read.registers ?? [];
    if (registers.length !== quantity) {
      console.log(`  结果：FAIL —— 从站在线，但 FC03 地址 ${startAddress} 返回 ${registers.length}/${quantity} 点`);
      console.log("  请按温度模块手册确认 FC03/FC04、寄存器起点和通道数；不写入 PASS 记录");
      return 1;
    }
    console.log(`  [读取] 地址 ${startAddress} 起 ${quantity} 点 = ${registers.join(", ")}`);
    if (scale != null) console.log(`  [换算] scale=${scale} → ${registers.map((value) => value * scale).join(", ")}`);
    passRecordLines.push(`RS485-SCAN PASS：${selected.path}，站号 ${hit.stationId}，${hit.baudRate}·${hit.parityLabel}，FC03 ${startAddress} 起 ${quantity} 点=${registers.join("/")}`);
    console.log("  结果：PASS —— 扫描、连接和指定寄存器读取全链路通过");
    return 0;
  } catch (error) {
    console.log(`\n  结果：FAIL —— ${error.message || error}`);
    console.log("  未通过的结果不会写入 PASS 记录");
    return 1;
  } finally {
    await serialService.close().catch(() => {});
    try { await core.request("shutdown", {}); } catch { /* 进程退出兜底 */ }
  }
}

async function runPreset() {
  const requestedCom = arg("sc09-com");
  if (!requestedCom) {
    console.log("  结果：拒绝执行 —— preset 必须显式指定 --sc09-com COMx");
    return 2;
  }
  if (!confirmSafeWrite()) {
    console.log("  结果：拒绝执行 —— preset 会写 D100，请确认后加 --confirm-safe-write YES");
    return 2;
  }
  const value = parseIntegerArg("value", 0x1234, 0, 0xffff);
  const ports = await listComPorts();
  const selected = findPort(ports, requestedCom);
  if (!selected) {
    console.log(`  结果：NOT READY —— SC09 串口 ${requestedCom} 不存在`);
    return 2;
  }
  console.log(`=== SC09 预置 D100=${hex(value)}（${describePort(selected)}）===`);
  console.log("  注意：GX Works2 与本脚本不能同时占用同一 SC09 串口");
  const { SerialService } = require("../electron/serial-service.cjs");
  const { createFxSerialService } = require("../electron/fx-serial-service.cjs");
  const core = createCore();
  const serialService = new SerialService();
  try {
    await core.start();
    await serialService.open({
      portName: selected.path,
      baudRate: 9600,
      dataBits: 7,
      parity: "even",
      stopBits: "1",
      flowControl: "none",
      readTimeoutMs: 1000,
      writeTimeoutMs: 1000,
      dtrMode: "preserve",
      rtsMode: "preserve",
    });
    const fx = createFxSerialService({
      request: (command, payload) => core.request(command, payload),
      transact: (args) => serialService.transact({ ...args, framing: "fx" }),
    });
    const write = await fx.progWrite({ device: "D", address: 100, values: [value], timeoutMs: 1500 });
    if (!write.ok) throw new Error(`写被拒：${write.errorCode ?? ""} ${write.errorMessage ?? ""}`);
    const read = await fx.progRead({ device: "D", address: 100, words: 1, timeoutMs: 1500 });
    if (!read.ok || read.values[0] !== value) throw new Error(`回读 D100=${hex(read.values?.[0])}，期望 ${hex(value)}`);
    console.log(`  结果：PASS —— D100 写入并回读为 ${hex(value)}`);
    return 0;
  } catch (error) {
    console.log(`  结果：FAIL —— ${error.message || error}`);
    return 1;
  } finally {
    await serialService.close().catch(() => {});
    try { await core.request("shutdown", {}); } catch { /* 进程退出兜底 */ }
  }
}

async function runAll() {
  console.log("=== 安全实机全流程：check → adp-read → scan（不包含任何写操作）===\n");
  const checkCode = await runCheck();
  if (checkCode !== 0) return checkCode;
  console.log("\n>>> [2/3] ADP 只读首连");
  const adpCode = await runAdpRead();
  if (adpCode !== 0) return adpCode;
  console.log("\n>>> [3/3] 485 全矩阵扫描");
  return runScan();
}

async function runAllField() {
  console.log("=== 现场三硬件通讯对象：check → adp-read → smart-read → scan（两台 PLC 均不写入）===");
  console.log("    上位机软件闭环请先单独运行 npm run rehearse:desktop，证据不与真机混写。\n");
  const checkCode = await runCheck();
  if (checkCode !== 0) return checkCode;
  console.log("\n>>> [2/4] FX3U ADP 只读首连");
  const adpCode = await runAdpRead();
  if (adpCode !== 0) return adpCode;
  console.log("\n>>> [3/4] S7-200 SMART 只读首连");
  const smartCode = await runSmartRead();
  if (smartCode !== 0) return smartCode;
  console.log("\n>>> [4/4] 485 全矩阵扫描");
  return runScan();
}

const sleepMs = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function hardwareReadyForWatch(rs485Com) {
  const ports = await listComPorts();
  if (!findPort(ports, rs485Com)) return null;
  if (!localSubnetOk()) return null;
  const probe = await probeAdpFrame(ADP_HOST, ADP_PORT, 1200);
  if (probe.state !== "plc") return null;
  return `ADP 1E 正常 + USB-485 ${rs485Com} 已出现`;
}

async function runWatch() {
  const rs485Com = arg("rs485-com");
  if (!rs485Com) {
    console.log("  结果：拒绝执行 —— watch 必须指定 --rs485-com COMx，避免任意 COM 触发");
    return 2;
  }
  const timeoutMin = parseIntegerArg("timeout", 30, 1, 1440);
  console.log(`=== watch：等待 ADP 与 ${rs485Com} 同时就绪（最长 ${timeoutMin} 分钟）===`);
  const startedAt = Date.now();
  const deadline = startedAt + timeoutMin * 60_000;
  while (Date.now() < deadline) {
    const ready = await hardwareReadyForWatch(rs485Com);
    if (ready) {
      console.log(`\n  ${ready}，执行安全 all...`);
      return runAll();
    }
    process.stdout.write(`\r  等待中... ${Math.round((Date.now() - startedAt) / 1000)}s   `);
    await sleepMs(10_000);
  }
  console.log(`\n  超时：${timeoutMin} 分钟内硬件未同时就绪`);
  return 2;
}

function showUsage() {
  console.log(`用法：
  node scripts/rehearse-hardware.cjs check --rs485-com COM4
  node scripts/rehearse-hardware.cjs adp-read [--expect 0x1234]
  node scripts/rehearse-hardware.cjs adp-write --address D300 --value 0x5A5A --confirm-safe-write YES
  node scripts/rehearse-hardware.cjs smart-read --smart-host 192.168.1.30 --smart-address VW100 --smart-expect-hex 1234 [--smart-slot 0]
  node scripts/rehearse-hardware.cjs scan --rs485-com COM4 [--station-start 1 --station-end 16 --start-address 0 --quantity 8 --scale 0.1]
  node scripts/rehearse-hardware.cjs preset --sc09-com COM3 --value 0x1234 --confirm-safe-write YES
  node scripts/rehearse-hardware.cjs all --rs485-com COM4
  node scripts/rehearse-hardware.cjs all-field --rs485-com COM4 --smart-host 192.168.1.30 --smart-address VW100 --smart-expect-hex 1234
  node scripts/rehearse-hardware.cjs watch --rs485-com COM4 [--timeout 30]`);
}

(async () => {
  const mode = process.argv[2] || "check";
  try {
    if (mode === "check") process.exitCode = await runCheck();
    else if (mode === "adp" || mode === "adp-read") {
      if (mode === "adp") console.log("[兼容提示] adp 现等同 adp-read，默认不再写 PLC");
      process.exitCode = await runAdpRead();
    } else if (mode === "adp-write") process.exitCode = await runAdpWrite();
    else if (mode === "smart-read") process.exitCode = await runSmartRead();
    else if (mode === "scan") process.exitCode = await runScan();
    else if (mode === "preset") process.exitCode = await runPreset();
    else if (mode === "all") process.exitCode = await runAll();
    else if (mode === "all-field") process.exitCode = await runAllField();
    else if (mode === "watch") process.exitCode = await runWatch();
    else {
      showUsage();
      process.exitCode = 2;
    }
  } catch (error) {
    console.error(`脚本异常：${error.message || error}`);
    process.exitCode = 1;
  } finally {
    flushPassRecords();
  }
})();
