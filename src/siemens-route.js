/**
 * 西门子协议变体到产品命令族的唯一映射。
 *
 * 这里描述的是产品路由，不代表每个变体都已经完成真实设备验证：
 * - s7comm/smart 走 ISO-on-TCP S7comm。
 * - ppi 和 fw 只能调用各自的在线命令。
 * - webapi 走 Electron 的 HTTPS 服务。
 * - uss/rk512/ppi-serial 复用已打开的共享 COM，不能回退到 S7comm。
 * - 未知或空变体 fail-closed，不得回落到 S7comm。
 */

const ROUTES = Object.freeze({
  s7comm: Object.freeze({
    variant: "s7comm",
    kind: "s7comm",
    online: true,
    connectCommand: "open_s7_connection",
    readCommand: "s7_read",
    writeCommand: "s7_write",
    disconnectCommand: "close_connection",
    label: "S7comm",
  }),
  smart: Object.freeze({
    variant: "smart",
    kind: "s7comm",
    online: true,
    connectCommand: "open_s7_connection",
    readCommand: "s7_read",
    writeCommand: "s7_write",
    disconnectCommand: "close_connection",
    label: "S7comm/SMART",
  }),
  ppi: Object.freeze({
    variant: "ppi",
    kind: "ppi",
    online: true,
    connectCommand: "open_ppi_tcp",
    readCommand: "ppi_read",
    writeCommand: "ppi_write",
    disconnectCommand: "close_connection",
    label: "PPI",
  }),
  "ppi-serial": Object.freeze({
    variant: "ppi-serial",
    kind: "ppi-serial",
    online: true,
    connectCommand: "get_serial_status",
    readCommand: "ppi_serial_read",
    writeCommand: null,
    disconnectCommand: null,
    label: "PPI 原生 COM（只读）",
    reason: "当前只开放原生 COM PPI 只读双拍；写入需完成独立安全验收。",
  }),
  fw: Object.freeze({
    variant: "fw",
    kind: "fetchwrite",
    online: true,
    connectCommand: "open_fw_tcp",
    readCommand: "fw_read",
    writeCommand: "fw_write",
    disconnectCommand: "close_connection",
    label: "Fetch/Write",
  }),
  webapi: Object.freeze({
    variant: "webapi",
    kind: "webapi",
    online: true,
    connectCommand: "s7web_connect",
    readCommand: "s7web_read",
    writeCommand: "s7web_write",
    disconnectCommand: "s7web_disconnect",
    label: "S7 Web API",
  }),
  uss: Object.freeze({
    variant: "uss",
    kind: "uss-serial",
    online: true,
    connectCommand: "get_serial_status",
    readCommand: "uss_serial_read",
    writeCommand: null,
    disconnectCommand: null,
    label: "USS",
    reason: "当前仅开放 USS 参数只读串口事务，控制字/写参数仍关闭。",
  }),
  rk512: Object.freeze({
    variant: "rk512",
    kind: "rk512-serial",
    online: true,
    connectCommand: "get_serial_status",
    readCommand: "rk512_serial_read",
    writeCommand: null,
    disconnectCommand: null,
    label: "3964R/RK512",
    reason: "当前仅开放 3964R/RK512 指定数据区只读事务，写入仍关闭。",
  }),
});

function unknownSiemensRoute(variant) {
  const key = String(variant || "").trim().toLowerCase();
  return Object.freeze({
    variant: key,
    kind: "unknown",
    online: false,
    connectCommand: null,
    readCommand: null,
    writeCommand: null,
    disconnectCommand: null,
    label: key ? `未知西门子变体 (${key})` : "未选择西门子变体",
    reason: "未知或空变体不得回退到 S7comm。",
  });
}

export function resolveSiemensRoute(variant) {
  const key = String(variant || "").trim().toLowerCase();
  return Object.prototype.hasOwnProperty.call(ROUTES, key) ? ROUTES[key] : unknownSiemensRoute(key);
}

export function listSiemensRoutes() {
  return Object.values(ROUTES).map((route) => ({ ...route }));
}
