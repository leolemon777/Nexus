const fs = require("node:fs");
const path = require("node:path");
const { app, BrowserWindow, ipcMain, Menu } = require("electron");
const { RustCoreClient } = require("./rust-core-client.cjs");
const { recoverRustCoreClient } = require("./rust-core-lifecycle.cjs");
const { SerialService } = require("./serial-service.cjs");
const { PollScheduler } = require("./poll-scheduler.cjs");
const { SerialDebugService } = require("./serial-debug-service.cjs");
const { DataExportService } = require("./data-export-service.cjs");
const { SlaveSerialBridge } = require("./slave-serial-bridge.cjs");
const { RealtimePushService } = require("./realtime-push-service.cjs");
const { createFxSerialService } = require("./fx-serial-service.cjs");
const {
  readHoldingRegistersOnce,
  readInputRegistersOnce,
  readCoilsOnce,
  readDiscreteInputsOnce,
  writeSingleCoilOnce,
  writeSingleRegisterOnce,
  writeMultipleCoilsOnce,
  writeMultipleRegistersOnce,
  tcpReadCoils,
  tcpReadDiscreteInputs,
  tcpReadHoldingRegisters,
  tcpReadInputRegisters,
  tcpWriteSingleCoil,
  tcpWriteSingleRegister,
  tcpWriteMultipleCoils,
  tcpWriteMultipleRegisters,
  udpReadCoils,
  udpReadDiscreteInputs,
  udpReadHoldingRegisters,
  udpReadInputRegisters,
  udpWriteSingleCoil,
  udpWriteSingleRegister,
  udpWriteMultipleCoils,
  udpWriteMultipleRegisters,
  openTcpConnection,
  openUdpConnection,
  closeConnection,
} = require("./modbus-master-service.cjs");
const {
  fileEntryUrl,
  isAllowedNavigation,
  resolveDevelopmentUrl,
  resolveRustCoreBinaryPath,
} = require("./runtime-policy.cjs");

const serialService = new SerialService();
const pollScheduler = new PollScheduler();
const serialDebugService = new SerialDebugService();
const dataExportService = new DataExportService();
const slaveSerialBridge = new SlaveSerialBridge();
const realtimePushService = new RealtimePushService();
pollScheduler.onError((pollId, error) => {
  mainWindow?.webContents.send("nexus:poll_error", { pollId, ...error });
});
// 轮询数据同时推送到 UI + SSE
pollScheduler.onData((pollId, data) => {
  mainWindow?.webContents.send("nexus:poll_data", { pollId, ...data });
  realtimePushService.push({ type: "poll_data", pollId, ...data, timestamp: Date.now() });
});
// debug frame 同时推送到 UI + SSE
serialDebugService.onFrame((record) => {
  mainWindow?.webContents.send("nexus:debug_frame", record);
  realtimePushService.push({ type: "debug_frame", ...record });
});
const smokeTest = process.env.NEXUS_SMOKE_TEST === "1";
let mainWindow;
let rustCoreLastError = null;

const projectRoot = path.join(__dirname, "..");
const rustCoreBinaryPath = resolveRustCoreBinaryPath({
  isPackaged: app.isPackaged,
  resourcesPath: process.resourcesPath,
  projectRoot,
  envPath: process.env.NEXUS_RUST_CORE_PATH,
});
function createRustCoreClient() {
  return new RustCoreClient({ binaryPath: rustCoreBinaryPath });
}

let rustCore = createRustCoreClient();

function backendStatus() {
  const serial = serialService.getStatus();
  const rustReady = rustCore.state === "ready";
  return {
    mode: rustReady ? "full" : "serial-only",
    electronTransport: "ready",
    serialTransport: {
      state: serial.isOpen ? "open" : "closed",
      config: serial.config,
    },
    rustCore: {
      state: rustCore.state,
      binaryAvailable: fs.existsSync(rustCoreBinaryPath),
      protocolVersion: rustCore.helloResult?.protocolVersion ?? null,
      serviceVersion: rustCore.helloResult?.serviceVersion ?? null,
      lastError: rustCoreLastError,
    },
  };
}

async function ensureRustCore() {
  if (!fs.existsSync(rustCoreBinaryPath)) {
    const error = new Error("Rust Core 尚未构建；Electron 串口传输层仍可独立运行。");
    error.code = "RUST_CORE_BINARY_MISSING";
    throw error;
  }
  try {
    rustCore = recoverRustCoreClient(rustCore, createRustCoreClient);
    const hello = await rustCore.start();
    rustCoreLastError = null;
    return rustCore;
  } catch (error) {
    rustCoreLastError = { code: error.code ?? "RUST_CORE_START_FAILED", message: error.message };
    throw error;
  }
}

async function startRustCoreIfPresent() {
  if (!fs.existsSync(rustCoreBinaryPath)) return;
  try {
    await ensureRustCore();
  } catch (error) {
    console.error(`[rust-core] startup failed: ${error.message}`);
  }
}

async function waitForRendererUiReady(window, timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  let lastProbe = null;
  while (Date.now() < deadline) {
    lastProbe = await window.webContents.executeJavaScript(`(() => {
      const appRoot = document.querySelector("#app");
      return {
        appDisplay: appRoot ? getComputedStyle(appRoot).display : null,
        stylesheetCount: document.styleSheets.length,
        uiReady: document.documentElement.dataset.nexusUiReady === "true"
      };
    })()`);
    if ((lastProbe.appDisplay === "grid" || lastProbe.appDisplay === "flex") && lastProbe.stylesheetCount >= 1 && lastProbe.uiReady) {
      return lastProbe;
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`界面资源未完整加载：${JSON.stringify(lastProbe)}`);
}

function registerDesktopCommands() {
  ipcMain.handle("nexus:list_serial_ports", () => serialService.listPorts());
  // 本机接口体检:网卡信息(纯 Node,不经 Rust)
  ipcMain.handle("nexus:list_network_interfaces", () => {
    const os = require("node:os");
    const raw = os.networkInterfaces();
    const list = [];
    for (const [name, addrs] of Object.entries(raw)) {
      const v4 = (addrs ?? []).filter((a) => a.family === "IPv4" || a.family === 4);
      const v6 = (addrs ?? []).filter((a) => a.family === "IPv6" || a.family === 6);
      const mac = (addrs ?? [])[0]?.mac ?? "";
      list.push({
        name,
        internal: (addrs ?? [])[0]?.internal ?? false,
        mac,
        ipv4: v4.map((a) => ({ address: a.address, netmask: a.netmask, cidr: a.cidr })),
        ipv6: v6.map((a) => a.address),
      });
    }
    return { ok: true, hostname: os.hostname(), interfaces: list };
  });
  // 全部已连接 USB 设备枚举(Windows: Get-PnpDevice,含键鼠/U盘/Hub,非仅串口)
  ipcMain.handle("nexus:list_usb_devices", async () => {
    const { execFile } = require("node:child_process");
    const run = (args) => new Promise((resolve) => {
      execFile("powershell.exe", ["-NoProfile", "-Command", args], { timeout: 15000 },
        (error, stdout) => resolve(error ? null : stdout));
    });
    const out = await run(
      "Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB*' } | " +
      "Select-Object FriendlyName, Class, Status, InstanceId | ConvertTo-Json -Compress"
    );
    if (out == null) return { ok: false, message: "USB 枚举失败(PowerShell 不可用)" };
    let items;
    try {
      const parsed = JSON.parse(out);
      items = Array.isArray(parsed) ? parsed : [parsed];
    } catch { items = []; }
    const devices = items.map((d) => {
      const m = /VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})/i.exec(d.InstanceId ?? "");
      return {
        name: d.FriendlyName ?? "(未命名设备)",
        class: d.Class ?? "",
        status: d.Status ?? "",
        vid: m ? m[1].toLowerCase() : null,
        pid: m ? m[2].toLowerCase() : null,
        instanceId: d.InstanceId ?? "",
      };
    });
    return { ok: true, devices };
  });
  // 网卡 IP 在线修改(netsh;需管理员权限,失败时透传 netsh 错误)
  ipcMain.handle("nexus:set_interface_ip", async (_event, args) => {
    const { execFile } = require("node:child_process");
    const name = String(args?.name ?? "").trim();
    if (!name || /[&|<>^"]/u.test(name)) {
      return { ok: false, message: "网卡名不合法" };
    }
    const mode = args?.mode === "dhcp" ? "dhcp" : "static";
    const run = (cmdline) => new Promise((resolve) => {
      execFile("netsh", ["interface", "ipv4"].concat(cmdline), { timeout: 20000 },
        (error, _so, se) => resolve({ error, stderr: String(se ?? "") }));
    });
    const wrap = (label, r) => {
      if (r.error) {
        const denied = /拒绝访问|Access is denied|requires elevation/i.test(r.stderr);
        return {
          ok: false,
          message: denied
            ? `${label}失败:需要管理员权限——请关闭本软件,右键「Nexus 2.0.exe → 以管理员身份运行」后重试`
            : `${label}失败:${r.stderr || r.error.message}`,
        };
      }
      return null;
    };
    if (mode === "dhcp") {
      let r = await run(["set", "address", `name=${name}`, "source=dhcp"]);
      let err = wrap("恢复自动获取(DHCP)", r);
      if (err) return err;
      await run(["set", "dnsservers", `name=${name}`, "source=dhcp"]);
      return { ok: true, mode };
    }
    // 静态 IP:基本格式校验
    const ipv4 = (s) => /^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$/.test(String(s ?? ""));
    if (!ipv4(args?.ip)) return { ok: false, message: "IP 地址格式不合法(示例 192.168.1.100)" };
    if (!ipv4(args?.mask)) return { ok: false, message: "子网掩码格式不合法(示例 255.255.255.0)" };
    const gwArgs = ipv4(args?.gateway) ? [args.gateway, "1"] : [];
    let r = await run(["set", "address", `name=${name}`, "source=static", "address=" + args.ip, "mask=" + args.mask].concat(gwArgs));
    let err = wrap("设置静态 IP", r);
    if (err) return err;
    if (ipv4(args?.dns)) {
      await run(["set", "dnsservers", `name=${name}`, "source=static", "address=" + args.dns, "validate=no", "primary"]);
    }
    return { ok: true, mode, ip: args.ip };
  });
  ipcMain.handle("nexus:get_serial_status", () => serialService.getStatus());
  ipcMain.handle("nexus:open_serial_port", (_event, args) => serialService.open(args?.config));
  ipcMain.handle("nexus:close_serial_port", () => serialService.close());
  ipcMain.handle("nexus:get_backend_status", () => backendStatus());
  ipcMain.handle("nexus:validate_serial_config_core", async (_event, args) => {
    const liveRustCore = await ensureRustCore();
    return liveRustCore.validateSerialConfig(args?.config);
  });
  ipcMain.handle("nexus:read_holding_registers_once", (_event, args) =>
    readHoldingRegistersOnce({ rustCore, serialService, ensureRustCore }, args),
  );
  ipcMain.handle("nexus:read_input_registers_once", (_event, args) =>
    readInputRegistersOnce({ rustCore, serialService, ensureRustCore }, args),
  );
  // 读位(FC01/FC02)
  ipcMain.handle("nexus:read_coils_once", (_event, args) =>
    readCoilsOnce({ rustCore, serialService, ensureRustCore }, args),
  );
  ipcMain.handle("nexus:read_discrete_inputs_once", (_event, args) =>
    readDiscreteInputsOnce({ rustCore, serialService, ensureRustCore }, args),
  );
  // 写操作(FC05/FC06/FC15/FC16)
  ipcMain.handle("nexus:write_single_coil_once", (_event, args) =>
    writeSingleCoilOnce({ rustCore, serialService, ensureRustCore }, args),
  );
  ipcMain.handle("nexus:write_single_register_once", (_event, args) =>
    writeSingleRegisterOnce({ rustCore, serialService, ensureRustCore }, args),
  );
  ipcMain.handle("nexus:write_multiple_coils_once", (_event, args) =>
    writeMultipleCoilsOnce({ rustCore, serialService, ensureRustCore }, args),
  );
  ipcMain.handle("nexus:write_multiple_registers_once", (_event, args) =>
    writeMultipleRegistersOnce({ rustCore, serialService, ensureRustCore }, args),
  );
  // TCP/UDP 连接管理
  ipcMain.handle("nexus:open_tcp_connection", (_event, args) =>
    openTcpConnection({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:open_udp_connection", (_event, args) =>
    openUdpConnection({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:close_connection", (_event, args) =>
    closeConnection({ ensureRustCore }, args),
  );
  // TCP 端到端读写
  ipcMain.handle("nexus:tcp_read_coils", (_event, args) =>
    tcpReadCoils({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:tcp_read_discrete_inputs", (_event, args) =>
    tcpReadDiscreteInputs({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:tcp_read_holding_registers", (_event, args) =>
    tcpReadHoldingRegisters({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:tcp_read_input_registers", (_event, args) =>
    tcpReadInputRegisters({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:tcp_write_single_coil", (_event, args) =>
    tcpWriteSingleCoil({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:tcp_write_single_register", (_event, args) =>
    tcpWriteSingleRegister({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:tcp_write_multiple_coils", (_event, args) =>
    tcpWriteMultipleCoils({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:tcp_write_multiple_registers", (_event, args) =>
    tcpWriteMultipleRegisters({ ensureRustCore }, args),
  );
  // UDP 端到端读写
  ipcMain.handle("nexus:udp_read_coils", (_event, args) =>
    udpReadCoils({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:udp_read_discrete_inputs", (_event, args) =>
    udpReadDiscreteInputs({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:udp_read_holding_registers", (_event, args) =>
    udpReadHoldingRegisters({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:udp_read_input_registers", (_event, args) =>
    udpReadInputRegisters({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:udp_write_single_coil", (_event, args) =>
    udpWriteSingleCoil({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:udp_write_single_register", (_event, args) =>
    udpWriteSingleRegister({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:udp_write_multiple_coils", (_event, args) =>
    udpWriteMultipleCoils({ ensureRustCore }, args),
  );
  ipcMain.handle("nexus:udp_write_multiple_registers", (_event, args) =>
    udpWriteMultipleRegisters({ ensureRustCore }, args),
  );
  // 值解码(纯计算)
  ipcMain.handle("nexus:decode_values", async (_event, args) => {
    const core = await ensureRustCore();
    return core.decodeValues(args);
  });
  // 扫描站号(TCP/UDP 连接)
  ipcMain.handle("nexus:scan_station_ids", async (_event, args) => {
    const core = await ensureRustCore();
    return core.scanStationIds(args);
  });
  // 高级功能码(TCP 专属):FC22/23/43/08/07/11/12/17 —— 直接透传到 Rust core
  for (const cmd of [
    "tcp_mask_write_register",
    "tcp_read_write_multiple",
    "tcp_read_device_id",
    "tcp_diagnostics",
    "tcp_read_exception_status",
    "tcp_get_comm_event_counter",
    "tcp_get_comm_event_log",
    "tcp_report_slave_id",
  ]) {
    ipcMain.handle(`nexus:${cmd}`, async (_event, args) => {
      const core = await ensureRustCore();
      return core.request(cmd, args ?? {});
    });
  }
  // 轮询(setInterval 驱动)
  ipcMain.handle("nexus:start_poll", async (_event, args) => {
    const { transport, connectionId, unitId, fc, startAddress, quantity, intervalMs } = args;
    const isTcp = ["tcp", "udp", "rtu-over-tcp", "ascii-over-tcp"].includes(transport);

    if (isTcp) {
      // TCP/UDP 路径:用 Rust 侧 start_poll_stream(Rust 推送,消除 IPC 往返)
      const core = await ensureRustCore();
      const streamId = `poll-${Date.now()}`;
      await core.startPollStream(
        {
          streamId,
          connectionId: connectionId || "default",
          fc: fc || 3,
          startAddress,
          quantity,
          intervalMs: intervalMs || 1000,
        },
        (data) => {
          // Rust 推送的轮询数据 → 转发给渲染层
          mainWindow?.webContents.send("nexus:poll_data", {
            pollId: streamId,
            registers: data?.registers,
            coils: data?.coils,
            address: startAddress,
            fc: fc || 3,
          });
        },
        (error) => {
          mainWindow?.webContents.send("nexus:poll_error", { pollId: streamId, ...error });
        },
      );
      return { pollId: streamId };
    }

    // 串口路径:保持 PollScheduler(setInterval,Rust 不持串口句柄)
    const invokeFn = async () => {
      const ctx = { rustCore, serialService, ensureRustCore };
      const fcNum = fc || 3;
      const readArgs = { unitId, startAddress, quantity, transport };
      if (fcNum === 1) return readCoilsOnce(ctx, readArgs);
      if (fcNum === 2) return readDiscreteInputsOnce(ctx, readArgs);
      if (fcNum === 4) return readInputRegistersOnce(ctx, readArgs);
      return readHoldingRegistersOnce(ctx, readArgs);
    };
    const pollId = pollScheduler.start(invokeFn, { ...args, intervalMs });
    return { pollId };
  });
  ipcMain.handle("nexus:stop_poll", async (_event, args) => {
    const pollId = args?.pollId;
    // 先尝试 stop_poll_stream(Rust 推送模式)
    if (pollId?.startsWith("poll-") && !pollScheduler.getStatus(pollId)) {
      try {
        const core = await ensureRustCore();
        await core.stopPollStream({ streamId: pollId });
        return { stopped: true, pollId };
      } catch {
        // 可能不是流式,继续尝试 PollScheduler
      }
    }
    // PollScheduler 模式
    const stopped = pollScheduler.stop(pollId);
    return { stopped, pollId };
  });
  ipcMain.handle("nexus:get_poll_status", (_event, args) => {
    return pollScheduler.getStatus(args?.pollId);
  });
  // 扫描波特率(串口路径:切换波特率 → 重开 → 探测)
  ipcMain.handle("nexus:scan_baud_rate", async (_event, args) => {
    return scanBaudRate({ rustCore, serialService, ensureRustCore }, args);
  });

  ipcMain.handle("nexus:scan_serial_stations", async (_event, args) => {
    return scanSerialStations({ rustCore, serialService, ensureRustCore }, args);
  });
  // 三菱 MC 协议:直接透传到 Rust core(JSONL 命令名与 IPC 名一一对应)
  for (const mcCmd of [
    "mc_parse_address",
    "mc_build_read",
    "mc_build_write",
    "mc_parse_response",
    "open_mc_tcp_connection",
    "mc_tcp_read",
    "mc_tcp_write",
    "start_mc_tcp_slave",
    "stop_mc_slave",
    "mc_slave_set",
    // MC 进阶(M2)
    "mc_tcp_read_random",
    "mc_tcp_write_random",
    "mc_tcp_read_blocks",
    "mc_remote_run",
    "mc_remote_stop",
    "mc_remote_reset",
    "mc_remote_pause",
    "mc_read_clock",
    "mc_echo_test",
    "mc_read_cpu_type",
    "mc_read_cpu_status",
    "mc_build_ascii_read",
    "open_mc_ascii_connection",
    "mc_ascii_read",
    "mc_ascii_write",
    // MC 串口 C24(3C/4C 离线组帧)
    "mc_serial_build_3c",
    "mc_serial_parse_3c",
    // A-1E / SLMP-1E 帧
    "mc_1e_build_read",
    "mc_1e_build_write",
    "mc_1e_parse",
    "open_mc_udp_connection",
    "mc_udp_read",
    "mc_udp_write",
    "mc_c24_read",
    "mc_c24_parse_read",
    "open_mc_1e_tcp",
    "mc_1e_read",
    "mc_1e_write",
  ]) {
    ipcMain.handle(`nexus:${mcCmd}`, async (_event, args) => {
      const core = await ensureRustCore();
      return core.request(mcCmd, args ?? {});
    });
  }
  // 西门子 S7comm:直接透传到 Rust core(JSONL 命令名与 IPC 名一一对应)
  for (const s7Cmd of [
    "brand_parse_address",
    "fins_parse_address",
    "open_fins_tcp",
    "open_fins_udp",
    "fins_read",
    "fins_write",
    "start_fins_slave",
    "stop_fins_slave",
    "fins_slave_set",
    "fins_slave_get",
    "s7_parse_address",
    "open_s7_connection",
    "s7_read",
    "s7_write",
    "start_s7_slave",
    "stop_s7_slave",
    "s7_slave_set",
    "s7_slave_get",
    "s7_cpu_control",
    "s7_read_status",
    "s7_password",
    "open_fw_tcp",
    "fw_read",
    "fw_write",
    "start_fw_slave",
    "stop_fw_slave",
    "open_ppi_tcp",
    "ppi_read",
    "ppi_write",
    "start_ppi_slave",
    "stop_ppi_slave",
  "uss_build_request",
  "uss_parse_response",
  "rk512_build_read",
  "rk512_build_write",
  "rk512_parse_response",
  ]) {
    ipcMain.handle(`nexus:${s7Cmd}`, async (_event, args) => {
      const core = await ensureRustCore();
      return core.request(s7Cmd, args ?? {});
    });
  }
  // 三菱 FX 串口协议:直接透传到 Rust core(JSONL 命令名与 IPC 名一一对应)
  for (const fxCmd of [
    "fx_links_build",
    "fx_links_parse",
    "fx_links_read",
    "fx_links_write_bits",
    "fx_links_write_words",
    "fx_prog_build_read",
    "fx_prog_build_write",
    "fx_prog_parse",
  ]) {
    ipcMain.handle(`nexus:${fxCmd}`, async (_event, args) => {
      const core = await ensureRustCore();
      return core.request(fxCmd, args ?? {});
    });
  }
  // 三菱 FX 串口在线事务:组帧(Rust)→串口(fx 收帧)→解析(Rust)
  // S7-1500 Web API(JSON-RPC over HTTPS):Node fetch 默认拒绝自签证书——
  // per-request agent 隔离,不动进程级 TLS 设置
  // 注意:不设 NODE_TLS_REJECT_UNAUTHORIZED(进程级禁用 TLS 校验有安全面风险)
  // s7-webapi-service.cjs 的 fetch 自带 per-request 超时/AbortSignal,自签证书由 PLC 侧引导用户信任
  const { createS7WebApiService } = require("./s7-webapi-service.cjs");
  const s7WebApi = createS7WebApiService();
  ipcMain.handle("nexus:export_diagnostics", async () => {
    const os = require("node:os"); const fs = require("node:fs"); const path = require("node:path");
    const { app } = require("electron");
    const ts = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
    const lines = [
      "Nexus 2.0 诊断报告", "时间: " + new Date().toLocaleString(), "版本: " + app.getVersion(),
      "系统: " + os.type() + " " + os.arch() + " " + os.release(), "主机: " + os.hostname(),
      "内存: " + Math.round(os.freemem()/1048576) + "MB 可用 / " + Math.round(os.totalmem()/1048576) + "MB 总计",
      "运行: " + Math.round(process.uptime()) + "秒", "",
      "=== Rust Core ===", rustCore ? "运行中" : "未启动",
      rustCoreLastError ? "错误: " + JSON.stringify(rustCoreLastError) : "无错误", "",
      "=== 串口 ===", JSON.stringify(serialService.getStatus(), null, 2),
    ];
    const desktop = path.join(os.homedir(), "Desktop");
    const fname = "Nexus诊断_" + ts + ".txt";
    try {
      fs.writeFileSync(path.join(desktop, fname), lines.join("\n"), "utf-8");
      return { ok: true, path: path.join(desktop, fname) };
    } catch (e) { return { ok: false, message: e.message }; }
  });

  ipcMain.handle("nexus:s7web_connect", (_e, args) => s7WebApi.connect(args || {}));
  ipcMain.handle("nexus:s7web_disconnect", () => s7WebApi.disconnect());
  ipcMain.handle("nexus:s7web_is_connected", () => s7WebApi.isConnected());
  ipcMain.handle("nexus:s7web_read", (_e, args) => s7WebApi.readVariable(args?.varName, args?.mode));
  ipcMain.handle("nexus:s7web_write", (_e, args) => s7WebApi.writeVariable(args?.varName, args?.value, args?.mode));
  ipcMain.handle("nexus:s7web_ping", () => s7WebApi.ping());

  const fxSerial = createFxSerialService({
    request: async (cmd, payload) => {
      const core = await ensureRustCore();
      return core.request(cmd, payload);
    },
    transact: async ({ request, timeoutMs }) =>
      serialService.transact({ request, timeoutMs, framing: "fx" }),
  });
  ipcMain.handle("nexus:fx_serial_transact", async (_event, args) => {
    const { op, protocol, params } = args ?? {};
    try {
      if (!serialService.getStatus()?.isOpen) {
        return { ok: false, error: { code: "SERIAL_NOT_OPEN", message: "请先在主站页打开串口" } };
      }
      let result;
      if (protocol === "links") {
        result = op === "write"
          ? await fxSerial.linksWrite(params)
          : await fxSerial.linksRead(params);
      } else if (protocol === "prog") {
        result = op === "write"
          ? await fxSerial.progWrite(params)
          : await fxSerial.progRead(params);
      } else {
        return { ok: false, error: { code: "FX_BAD_PROTOCOL", message: `未知 FX 协议「${protocol}」` } };
      }
      return result;
    } catch (error) {
      return { ok: false, error: { code: "FX_SERIAL_ERROR", message: error.message } };
    }
  });
  // MC C24 串口读(复用主站页串口,3C 帧格式1)
  ipcMain.handle("nexus:mc_c24_serial_read", async (_event, args) => {
    try {
      if (!serialService.getStatus()?.isOpen) {
        return { ok: false, error: { code: "SERIAL_NOT_OPEN", message: "请先在主站页打开串口" } };
      }
      return await fxSerial.mcC24Read(args ?? {});
    } catch (error) {
      return { ok: false, error: { code: "MC_C24_SERIAL_ERROR", message: error.message } };
    }
  });
  // 指令列表执行(顺序执行多条指令)
  ipcMain.handle("nexus:execute_commands", async (_event, args) => {
    return executeCommandList({ rustCore, serialService, ensureRustCore }, args);
  });
  // 从站模拟
  ipcMain.handle("nexus:start_tcp_slave", async (_event, args) => {
    const core = await ensureRustCore();
    return core.startTcpSlave(args);
  });
  ipcMain.handle("nexus:stop_slave", async (_event, args) => {
    const core = await ensureRustCore();
    return core.stopSlave(args);
  });
  ipcMain.handle("nexus:slave_set_value", async (_event, args) => {
    const core = await ensureRustCore();
    return core.slaveSetValue(args);
  });
  ipcMain.handle("nexus:slave_set_coil", async (_event, args) => {
    const core = await ensureRustCore();
    return core.slaveSetCoil(args);
  });
  ipcMain.handle("nexus:slave_clear", async (_event, args) => {
    const core = await ensureRustCore();
    return core.slaveClear(args);
  });
  ipcMain.handle("nexus:slave_get_memory", async (_event, args) => {
    const core = await ensureRustCore();
    return core.slaveGetMemory(args);
  });
  // 串口调试
  ipcMain.handle("nexus:debug_send", async (_event, args) => {
    const core = await ensureRustCore();
    serialDebugService.setRustCoreRequest((cmd, payload) => core.request(cmd, payload));
    return serialDebugService.send(args);
  });
  ipcMain.handle("nexus:debug_set_receive", (_event, args) => {
    serialDebugService.allowReceive = args?.enabled !== false;
    return { allowReceive: serialDebugService.allowReceive };
  });
  ipcMain.handle("nexus:debug_set_send", (_event, args) => {
    serialDebugService.allowSend = args?.enabled !== false;
    return { allowSend: serialDebugService.allowSend };
  });
  ipcMain.handle("nexus:debug_set_crc", (_event, args) => {
    serialDebugService.appendCrc = args?.enabled === true;
    return { appendCrc: serialDebugService.appendCrc };
  });
  ipcMain.handle("nexus:debug_clear_log", () => {
    serialDebugService.clearLog();
    return { cleared: true };
  });
  ipcMain.handle("nexus:debug_get_log", () => {
    return { log: serialDebugService.getLog() };
  });
  ipcMain.handle("nexus:debug_attach", () => {
    // 把 serialDebugService 绑定到 serialService 的 port
    if (serialService.current?.port?.isOpen) {
      serialDebugService.attach(serialService.current.port);
      return { attached: true };
    }
    return { attached: false, error: "串口未打开" };
  });
  ipcMain.handle("nexus:debug_detach", () => {
    serialDebugService.detach();
    return { detached: true };
  });
  // CRC/LRC 校验 + 报文解析(纯计算,通过 Rust core)
  ipcMain.handle("nexus:compute_crc16", async (_event, args) => {
    const core = await ensureRustCore();
    return core.request("compute_crc16", args);
  });
  ipcMain.handle("nexus:compute_lrc", async (_event, args) => {
    const core = await ensureRustCore();
    return core.request("compute_lrc", args);
  });
  ipcMain.handle("nexus:parse_frame_online", async (_event, args) => {
    const core = await ensureRustCore();
    return core.request("parse_frame_online", args);
  });
  ipcMain.handle("nexus:parse_frame_offline", async (_event, args) => {
    const core = await ensureRustCore();
    return core.request("parse_frame_offline", args);
  });
  // 流式轮询(v2)
  ipcMain.handle("nexus:start_poll_stream", async (_event, args) => {
    const core = await ensureRustCore();
    return core.startPollStream(
      args,
      (data) => mainWindow?.webContents.send("nexus:stream_data", data),
      (error) => mainWindow?.webContents.send("nexus:stream_error", error),
    );
  });
  ipcMain.handle("nexus:stop_poll_stream", async (_event, args) => {
    const core = await ensureRustCore();
    return core.stopPollStream(args);
  });
  // 数据导出
  ipcMain.handle("nexus:export_csv", (_event, args) => dataExportService.exportCsv(args));
  ipcMain.handle("nexus:export_json", (_event, args) => dataExportService.exportJson(args));
  ipcMain.handle("nexus:export_trace", (_event, args) => dataExportService.exportTraceLog(args));
  // 串口从站桥接
  ipcMain.handle("nexus:start_serial_slave", async (_event, args) => {
    const core = await ensureRustCore();
    await slaveSerialBridge.start(serialService, (cmd, payload) => core.request(cmd, payload), args?.slaveId || "serial-default");
    return { running: true };
  });
  ipcMain.handle("nexus:stop_serial_slave", async () => {
    await slaveSerialBridge.stop();
    return { stopped: true };
  });
  ipcMain.handle("nexus:serial_slave_set_value", async (_event, args) => {
    const core = await ensureRustCore();
    return core.request("serial_slave_set_value", args);
  });
  ipcMain.handle("nexus:serial_slave_get_memory", async (_event, args) => {
    const core = await ensureRustCore();
    return core.request("serial_slave_get_memory", args);
  });
  // G6: 实时数据推送(SSE)
  ipcMain.handle("nexus:start_realtime_push", async (_event, args) => {
    return realtimePushService.start(args ?? {});
  });
  ipcMain.handle("nexus:stop_realtime_push", async () => {
    return realtimePushService.stop();
  });
  ipcMain.handle("nexus:get_realtime_status", () => {
    return { running: !!realtimePushService.server, connectedClients: realtimePushService.connectedClients };
  });
}

const COMMON_BAUD_RATES = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

async function scanBaudRate(ctx, args) {
  const { serialService } = ctx;
  const { comPort, stationId = 1, baudRates, timeoutMs = 500 } = args ?? {};
  const rates = baudRates?.length ? baudRates : COMMON_BAUD_RATES;
  if (!comPort) {
    return { ok: false, error: { code: "INVALID_PARAM", message: "缺少 comPort 参数" } };
  }
  // 保存当前串口配置,扫描后恢复
  const originalConfig = serialService.getStatus()?.config;
  const wasOpen = serialService.getStatus()?.isOpen ?? false;

  for (const baud of rates) {
    // 关闭当前串口(如果开着)
    try {
      if (serialService.getStatus()?.isOpen) await serialService.close();
    } catch {
      // 忽略关闭错误
    }
    // 用新波特率打开
    const testConfig = {
      ...originalConfig,
      portName: comPort,
      baudRate: baud,
      readTimeoutMs: timeoutMs,
      writeTimeoutMs: timeoutMs,
    };
    try {
      await serialService.open(testConfig);
    } catch {
      continue; // 打不开就跳过
    }
    // 发 FC03 探测
    try {
      const result = await readHoldingRegistersOnce(
        ctx,
        { unitId: stationId, startAddress: 0, quantity: 1, timeoutMs },
      );
      if (result.ok) {
        // 找到了!恢复原配置(或关闭)
        try {
          await serialService.close();
          if (wasOpen && originalConfig) await serialService.open(originalConfig);
        } catch {
          // 忽略
        }
        return { ok: true, foundBaudRate: baud, stationId };
      }
    } catch {
      // 超时,继续下一个
    }
  }
  // 全部试完,恢复原配置
  try {
    if (serialService.getStatus()?.isOpen) await serialService.close();
    if (wasOpen && originalConfig) await serialService.open(originalConfig);
  } catch {
    // 忽略
  }
  return { ok: false, error: { code: "BAUD_NOT_FOUND", message: "所有波特率均无响应" } };
}

/**
 * 串口扫描站号(1-247)——对每个站号发 FC03 读 1 个保持寄存器,
 * 有响应(正常或异常响应都算在线)则记录,超时跳过。
 * 串口保持打开,扫描期间复用同一连接。
 */
async function scanSerialStations(ctx, args) {
  const { serialService } = ctx;
  const { rangeStart = 1, rangeEnd = 247, timeoutMs = 300 } = args ?? {};
  if (!serialService.getStatus()?.isOpen) {
    return { ok: false, error: { code: "PORT_NOT_OPEN", message: "请先打开串口再扫描站号" } };
  }
  const found = [];
  let scanned = 0;
  for (let unitId = rangeStart; unitId <= rangeEnd; unitId++) {
    scanned++;
    try {
      const result = await readHoldingRegistersOnce(ctx, {
        unitId,
        startAddress: 0,
        quantity: 1,
        timeoutMs,
      });
      // ok=true 正常响应; ok=false 但收到了异常帧也算在线(从站拒绝但存在)
      if (result.ok) {
        found.push({ stationId: unitId, baudRate: serialService.getStatus()?.config?.baudRate, format: "RTU", firstResponse: "FC03 OK", functionCode: 3, status: "在线" });
      } else if (result.error?.code === "MODBUS_EXCEPTION" || result.exceptionCode != null) {
        found.push({ stationId: unitId, baudRate: serialService.getStatus()?.config?.baudRate, format: "RTU", firstResponse: `异常码 ${result.exceptionCode}`, functionCode: 3, status: "在线(异常响应)" });
      }
    } catch {
      // 超时或 CRC 错误 → 该站号无响应,跳过
    }
  }
  return { ok: true, found, scanned };
}

async function executeCommandList(ctx, args) {
  const commands = args?.commands ?? [];
  if (!Array.isArray(commands) || commands.length === 0) {
    return { ok: false, results: [], error: { code: "EMPTY", message: "指令列表为空" } };
  }
  const results = [];
  for (let i = 0; i < commands.length; i++) {
    const cmd = commands[i];
    try {
      let result;
      const fc = cmd.fc || 3;
      const isWrite = [5, 6, 15, 16].includes(fc);
      const ctxArgs = { unitId: cmd.unitId, startAddress: cmd.address, address: cmd.address, quantity: cmd.quantity, timeoutMs: cmd.timeoutMs, transport: cmd.transport };
      if (isWrite) {
        const writeArgs = { ...ctxArgs, value: cmd.value, values: cmd.values };
        if (fc === 5) result = await writeSingleCoilOnce(ctx, writeArgs);
        else if (fc === 6) result = await writeSingleRegisterOnce(ctx, writeArgs);
        else if (fc === 15) result = await writeMultipleCoilsOnce(ctx, writeArgs);
        else if (fc === 16) result = await writeMultipleRegistersOnce(ctx, writeArgs);
      } else {
        if (fc === 1) result = await readCoilsOnce(ctx, ctxArgs);
        else if (fc === 2) result = await readDiscreteInputsOnce(ctx, ctxArgs);
        else if (fc === 4) result = await readInputRegistersOnce(ctx, ctxArgs);
        else result = await readHoldingRegistersOnce(ctx, ctxArgs);
      }
      results.push({ index: i, fc, ok: result.ok, error: result.error ?? null });
    } catch (error) {
      results.push({ index: i, fc, ok: false, error: { code: "EXEC_ERROR", message: error.message } });
    }
  }
  return { ok: results.every((r) => r.ok), results };
}

async function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1360,
    height: 900,
    minWidth: 980,
    minHeight: 720,
    show: !smokeTest,
    autoHideMenuBar: true,
    backgroundColor: "#eef1f4",
    title: "Nexus 2.0 · 串口实验室",
    webPreferences: {
      preload: path.join(__dirname, "preload.cjs"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  const distEntry = path.join(projectRoot, "dist", "index.html");
  const developmentUrl = resolveDevelopmentUrl(process.env.NEXUS_DEV_SERVER_URL, {
    isPackaged: app.isPackaged,
  });
  const allowedEntryUrl = developmentUrl || fileEntryUrl(distEntry);

  mainWindow.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  mainWindow.webContents.on("will-navigate", (event, targetUrl) => {
    if (!isAllowedNavigation(targetUrl, allowedEntryUrl)) event.preventDefault();
  });

  if (developmentUrl) await mainWindow.loadURL(developmentUrl);
  else await mainWindow.loadFile(distEntry);

  if (smokeTest) {
    try {
      const uiProbe = await waitForRendererUiReady(mainWindow);
      if (mainWindow.isMenuBarVisible()) {
        throw new Error("默认 Electron 菜单仍然可见。");
      }
      console.log(`NEXUS_UI_SMOKE_OK stylesheets=${uiProbe.stylesheetCount}`);
      const ports = await serialService.listPorts();
      console.log(`NEXUS_ELECTRON_SMOKE_OK ports=${ports.length}`);
    } catch (error) {
      console.error(`NEXUS_ELECTRON_SMOKE_FAILED ${error.message}`);
      process.exitCode = 1;
    } finally {
      await Promise.allSettled([serialService.close(), rustCore.shutdown()]);
      mainWindow.destroy();
      app.exit(process.exitCode ?? 0);
    }
  }
}

app.whenReady().then(async () => {
  Menu.setApplicationMenu(null);
  registerDesktopCommands();
  void startRustCoreIfPresent();
  await createWindow();

  app.on("activate", () => {
    if (BrowserWindow.getAllWindows().length === 0) void createWindow();
  });
});

app.on("window-all-closed", () => {
  mainWindow = null;
  void Promise.allSettled([serialService.close(), rustCore.shutdown()]).finally(() => {
    if (process.platform !== "darwin") app.quit();
  });
});
