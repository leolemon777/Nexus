"use strict";

/**
 * 纯桌面演示前自检：零硬件完成
 * Nexus 虚拟 MC/S7 从站 → SMART 只读 → C# 黄金向量/E2E → C# 数据桥 → Nexus Modbus TCP 回读。
 */

const fs = require("node:fs");
const net = require("node:net");
const path = require("node:path");
const { spawn, spawnSync } = require("node:child_process");
const { RustCoreClient } = require("../electron/rust-core-client.cjs");

const projectRoot = path.resolve(__dirname, "..");
const workspaceRoot = path.resolve(projectRoot, "..");
const corePath = process.env.NEXUS_RUST_CORE_PATH
  || path.join(projectRoot, "rust-core", "target", "release", "nexus-rust-core.exe");
const codecCheckPath = path.join(
  workspaceRoot,
  "NexusDemoCSharp",
  "CodecCheck",
  "bin",
  "Release",
  "net10.0",
  "CodecCheck.exe",
);

function arg(name, fallback) {
  const argv = process.argv.slice(2);
  const index = argv.indexOf(`--${name}`);
  return index >= 0 && argv[index + 1] != null ? argv[index + 1] : fallback;
}

function integerArg(name, fallback, min, max) {
  const value = Number(arg(name, fallback));
  if (!Number.isInteger(value) || value < min || value > max) {
    throw new Error(`--${name} 必须是 ${min}..${max} 的整数`);
  }
  return value;
}

function runCodec(args, label) {
  const result = spawnSync(codecCheckPath, args, {
    cwd: path.dirname(codecCheckPath),
    encoding: "utf8",
    windowsHide: true,
  });
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${label} 失败（exit ${result.status}）`);
}

function portIsFree(port) {
  return new Promise((resolve) => {
    const server = net.createServer();
    server.unref();
    server.once("error", () => resolve(false));
    server.listen({ host: "127.0.0.1", port, exclusive: true }, () => {
      server.close(() => resolve(true));
    });
  });
}

function waitForPort(port, timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve, reject) => {
    const attempt = () => {
      const socket = net.createConnection({ host: "127.0.0.1", port });
      let settled = false;
      const finish = (ok) => {
        if (settled) return;
        settled = true;
        socket.destroy();
        if (ok) resolve();
        else if (Date.now() >= deadline) reject(new Error(`等待端口 ${port} 超时`));
        else setTimeout(attempt, 100);
      };
      socket.setTimeout(300, () => finish(false));
      socket.once("connect", () => finish(true));
      socket.once("error", () => finish(false));
    };
    attempt();
  });
}

function waitForExit(child) {
  return new Promise((resolve, reject) => {
    child.once("error", reject);
    child.once("exit", (code) => resolve(code));
  });
}

async function preflight(mcPort, s7Port, modbusPort) {
  const missing = [corePath, codecCheckPath].filter((file) => !fs.existsSync(file));
  if (missing.length > 0) {
    missing.forEach((file) => console.log(`  [缺失] ${file}`));
    return false;
  }
  console.log(`  [CORE] ${corePath}`);
  console.log(`  [C#  ] ${codecCheckPath}`);
  const mcFree = await portIsFree(mcPort);
  const s7Free = s7Port === mcPort ? false : await portIsFree(s7Port);
  const modbusFree = [mcPort, s7Port].includes(modbusPort) ? false : await portIsFree(modbusPort);
  console.log(`  [PORT] MC ${mcPort} ${mcFree ? "空闲 ✓" : "被占用 ✗"}`);
  console.log(`  [PORT] S7 ${s7Port} ${s7Free ? "空闲 ✓" : "被占用或与 MC 重复 ✗"}`);
  console.log(`  [PORT] Modbus ${modbusPort} ${modbusFree ? "空闲 ✓" : "被占用或与 MC/S7 重复 ✗"}`);
  return mcFree && s7Free && modbusFree;
}

async function runAll() {
  const mcPort = integerArg("mc-port", 5000, 1, 65535);
  const s7Port = integerArg("s7-port", 1102, 1, 65535);
  const modbusPort = integerArg("modbus-port", 502, 1, 65535);
  const duration = integerArg("duration", 5, 3, 60);
  console.log("=== 纯桌面闭环自检（零硬件）===");
  if (!await preflight(mcPort, s7Port, modbusPort)) {
    console.log("  结果：NOT READY —— 先释放端口或补齐构建产物");
    return 2;
  }

  const core = new RustCoreClient({ binaryPath: corePath });
  let bridge = null;
  try {
    await core.start();
    await core.request("start_mc_tcp_slave", { slaveId: "desktop-rehearsal", port: mcPort, seed: true });
    console.log(`\n[1/5] Nexus 虚拟 MC 从站已启动：127.0.0.1:${mcPort}`);

    console.log(`\n[2/5] S7-200 SMART 虚拟 CPU：127.0.0.1:${s7Port}`);
    await core.request("start_s7_slave", { slaveId: "desktop-smart", port: s7Port, seed: true });
    await core.request("s7_slave_set", {
      slaveId: "desktop-smart",
      address: "DB1.DBB100",
      values: [0x56, 0x78],
    });
    const smartConnect = await core.request("open_s7_connection", {
      connectionId: "desktop-smart-client",
      host: "127.0.0.1",
      port: s7Port,
      rack: 0,
      slot: 0,
      connType: 1,
    });
    const smartRead = await core.request("s7_read", {
      connectionId: "desktop-smart-client",
      items: ["MW0", "VW100"],
    });
    const marker = smartRead?.items?.[0];
    const vWord = smartRead?.items?.[1];
    if (marker?.returnCode !== 0xff || vWord?.returnCode !== 0xff
      || marker.data?.[0] !== 0x12 || marker.data?.[1] !== 0x34
      || vWord.data?.[0] !== 0x56 || vWord.data?.[1] !== 0x78) {
      throw new Error("SMART 虚拟 CPU 的 MW0/VW100 回读不一致");
    }
    console.log(`  COTP + S7 PDU ${smartConnect.pduSize}B；MW0=0x1234；VW100=0x5678 ✓`);

    console.log("\n[3/5] C# 黄金向量");
    runCodec([], "C# 黄金向量");

    console.log("\n[4/5] C# ↔ Nexus MC E2E");
    runCodec(["e2e", "--host", "127.0.0.1", "--port", String(mcPort)], "MC E2E");

    console.log("\n[5/5] C# 数据桥 → Nexus Modbus TCP 回读");
    bridge = spawn(codecCheckPath, [
      "bridge",
      "--host", "127.0.0.1",
      "--mc-port", String(mcPort),
      "--srv-port", String(modbusPort),
      "--duration", String(duration),
    ], {
      cwd: path.dirname(codecCheckPath),
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
    });
    bridge.stdout.on("data", (data) => process.stdout.write(data));
    bridge.stderr.on("data", (data) => process.stderr.write(data));
    await waitForPort(modbusPort);
    await core.openTcpConnection({
      connectionId: "desktop-modbus",
      host: "127.0.0.1",
      port: modbusPort,
      unitId: 1,
      framing: "standard",
    });
    const response = await core.tcpReadHoldingRegisters({
      connectionId: "desktop-modbus",
      startAddress: 0,
      quantity: 9,
    });
    const registers = response?.registers ?? response?.values ?? [];
    console.log(`  Nexus 回读 40001~40009 = ${registers.map((value) => `0x${value.toString(16).toUpperCase().padStart(4, "0")}`).join(", ")}`);
    if (registers[0] !== 0x1234 || registers[1] !== 0xabcd || registers[8] !== 0x0555) {
      throw new Error("Modbus 镜像值与 MC seed 不一致");
    }
    await core.closeConnection({ connectionId: "desktop-modbus" });
    const bridgeCode = await waitForExit(bridge);
    bridge = null;
    if (bridgeCode !== 0) throw new Error(`C# 数据桥失败（exit ${bridgeCode}）`);

    console.log("\n结果：PASS —— 纯桌面 MC 3E/A-1E、S7-200 SMART、C# 对照、数据桥和 Modbus 回读全部通过");
    console.log(`演示参数：MC 虚拟 PLC 127.0.0.1:${mcPort}；SMART 虚拟 CPU 127.0.0.1:${s7Port}；大屏 Modbus TCP 127.0.0.1:${modbusPort}`);
    return 0;
  } catch (error) {
    console.log(`\n结果：FAIL —— ${error.message || error}`);
    return 1;
  } finally {
    if (bridge && !bridge.killed) bridge.kill();
    try { await core.request("shutdown", {}); } catch { /* 进程退出兜底 */ }
  }
}

(async () => {
  const mode = process.argv[2] || "all";
  try {
    const mcPort = integerArg("mc-port", 5000, 1, 65535);
    const s7Port = integerArg("s7-port", 1102, 1, 65535);
    const modbusPort = integerArg("modbus-port", 502, 1, 65535);
    if (mode === "check") {
      console.log("=== 纯桌面演示前置检查 ===");
      process.exitCode = await preflight(mcPort, s7Port, modbusPort) ? 0 : 2;
    } else if (mode === "all") process.exitCode = await runAll();
    else {
      console.log("用法：node scripts/rehearse-desktop.cjs [check|all] [--mc-port 5000 --s7-port 1102 --modbus-port 502 --duration 5]");
      process.exitCode = 2;
    }
  } catch (error) {
    console.error(`脚本异常：${error.message || error}`);
    process.exitCode = 1;
  }
})();
