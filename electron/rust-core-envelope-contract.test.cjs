/**
 * rust-core 信封契约回归测试 —— 守卫 JS 调用层与 rust-core deny_unknown_fields 载荷结构的一致性。
 *
 * 背景(2026-08-25 真机):fx_prog_parse 的 JS 侧照 links 惯例发 {response} 而契约是 {frame},
 * INVALID_ENVELOPE 导致 FX 编程口整路不可用——两侧单测各自全绿、唯独中间契约无人把守。
 * 本测试把产品全部 JS→rust-core 调用点的载荷矩阵回放到真实二进制:
 *   1) 覆盖率:src/main.js 的 callBackend 命令 + electron 服务的 request 命令必须都有矩阵条目
 *      (新增命令不补夹具 → 红,强制维护);
 *   2) 回放:任何 INVALID_ENVELOPE = 契约破坏 → 红。域错误(连接拒绝/地址无效等)证明信封已通过,视为绿。
 *
 * 矩阵值只需类型正确(信封只校验字段名与类型);真实数值语义由各协议自己的测试负责。
 */

const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");

const MATRIX = require("./rust-core-envelope-contract.fixtures.json");

// 纯主进程本地 IPC(不经 rust-core),不参与信封契约
const NON_CORE = new Set([
  "debug_attach", "debug_clear_log", "debug_send", "debug_set_crc", "debug_set_receive", "debug_set_send",
  "execute_commands", "export_csv", "export_diagnostics", "export_json", "export_trace", "fx_serial_transact",
  "get_serial_status", "gx3_analyze_project", "gx3_query_device", "gx3_status", "gx3_select_project",
  "list_network_interfaces", "list_serial_ports", "list_usb_devices", "mc_c24_serial_read",
  "omron_hostlink_serial_read", "open_serial_port", "close_serial_port",
  "panasonic_serial_read", "ping_host", "project_export_sanitized", "project_open", "project_new",
  "project_restore_last", "record_write_audit", "scan_all", "scan_all_cancel", "scan_baud_rate",
  "scan_serial_stations", "set_interface_ip", "start_poll", "start_realtime_push", "stop_realtime_push",
  "stop_poll", "dlt645_serial_read", "delta_modbus_plan", "delta_modbus_read",
]);

function resolveBinary() {
  const envPath = process.env.NEXUS_RUST_CORE_PATH;
  if (envPath && fs.existsSync(envPath)) return envPath;
  const dev = path.join(__dirname, "..", "rust-core", "target", "debug", "nexus-rust-core.exe");
  return fs.existsSync(dev) ? dev : null;
}

function sendAll(binaryPath, entries) {
  return new Promise((resolve, reject) => {
    const child = spawn(binaryPath, [], { stdio: ["pipe", "pipe", "pipe"] });
    let buf = "";
    const responses = new Map();
    const timer = setTimeout(() => {
      child.kill();
      resolve(responses); // 超时按已收响应裁决,让用例报"无响应"而非挂死
    }, 30000);
    child.stdout.on("data", (chunk) => {
      buf += chunk.toString("utf8");
      let nl;
      while ((nl = buf.indexOf("\n")) !== -1) {
        const lineStr = buf.slice(0, nl).trim();
        buf = buf.slice(nl + 1);
        if (!lineStr) continue;
        try {
          const parsed = JSON.parse(lineStr);
          responses.set(parsed.requestId, parsed);
        } catch { /* 忽略非 JSON 行 */ }
      }
      if (responses.size >= entries.length) {
        clearTimeout(timer);
        child.kill();
        resolve(responses);
      }
    });
    child.stderr.on("data", () => {});
    child.on("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    entries.forEach(([cmd, payload], i) => {
      child.stdin.write(JSON.stringify({ protocolVersion: 1, requestId: "ec" + i, command: cmd, payload }) + "\n");
    });
    child.stdin.end();
  });
}

test("载荷矩阵覆盖全部 JS→rust-core 产品调用点", () => {
  const root = path.join(__dirname, "..");
  const renderer = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const rendererCmds = new Set();
  for (const m of renderer.matchAll(/callBackend\("([a-z0-9_]+)"/g)) rendererCmds.add(m[1]);

  const serviceCmds = new Set();
  const serviceFiles = fs.readdirSync(path.join(root, "electron"))
    .filter((f) => f.endsWith(".cjs") && !f.endsWith(".test.cjs") && f !== "main.cjs" && f !== "preload.cjs");
  for (const f of serviceFiles) {
    const text = fs.readFileSync(path.join(root, "electron", f), "utf8");
    for (const m of text.matchAll(/request\("([a-z0-9_]+)"/g)) serviceCmds.add(m[1]);
  }

  const missing = [];
  for (const cmd of new Set([...rendererCmds, ...serviceCmds])) {
    if (NON_CORE.has(cmd) || cmd === "shutdown") continue;
    if (!MATRIX[cmd]) missing.push(cmd);
  }
  assert.deepStrictEqual(missing, [],
    "以下命令缺少信封矩阵条目(新增命令必须在 rust-core-envelope-contract.fixtures.json 补一条类型正确的载荷):");
});

test("载荷矩阵回放真实 rust-core 无信封拒绝", { timeout: 60000 }, async (t) => {
  const binary = resolveBinary();
  if (!binary) return t.skip("rust-core 二进制不存在,跳过信封回放");
  const entries = Object.entries(MATRIX);
  const responses = await sendAll(binary, entries);
  const rejected = [];
  const noResponse = [];
  entries.forEach(([cmd], i) => {
    const r = responses.get("ec" + i);
    if (!r) { noResponse.push(cmd); return; }
    if (r.ok === false && r.error && r.error.code === "INVALID_ENVELOPE") {
      rejected.push(cmd + " — " + (r.error.message || ""));
    }
  });
  assert.deepStrictEqual(noResponse, [], "以下命令无响应(可能阻塞或进程提前退出):");
  assert.deepStrictEqual(rejected, [], "以下命令被 rust-core 信封拒绝(契约破坏,参照 fx_prog_parse 2026-08-25 缺陷):");
});
