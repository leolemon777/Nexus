/**
 * 三菱 MELSEC / FX / MC 变体到产品命令族的唯一映射。
 *
 * C24 本阶段只开放共享 COM 只读；写入不得走 fx_serial_transact("c24")。
 * 未知或空变体 fail-closed，不得回落到 3E Binary。
 */

const ROUTES = Object.freeze({
  "3e": Object.freeze({
    variant: "3e",
    kind: "mc-tcp",
    online: true,
    connectCommand: "open_mc_tcp_connection",
    readCommand: "mc_tcp_read",
    writeCommand: "mc_tcp_write",
    disconnectCommand: "close_connection",
    label: "MC Binary 3E",
  }),
  "4e": Object.freeze({
    variant: "4e",
    kind: "mc-tcp",
    online: true,
    connectCommand: "open_mc_tcp_connection",
    readCommand: "mc_tcp_read",
    writeCommand: "mc_tcp_write",
    disconnectCommand: "close_connection",
    label: "MC Binary 4E",
  }),
  "ascii-3e": Object.freeze({
    variant: "ascii-3e",
    kind: "mc-ascii",
    online: true,
    connectCommand: "open_mc_ascii_connection",
    readCommand: "mc_ascii_read",
    writeCommand: "mc_ascii_write",
    disconnectCommand: "close_connection",
    label: "MC ASCII 3E",
  }),
  "ascii-4e": Object.freeze({
    variant: "ascii-4e",
    kind: "mc-ascii",
    online: true,
    connectCommand: "open_mc_ascii_connection",
    readCommand: "mc_ascii_read",
    writeCommand: "mc_ascii_write",
    disconnectCommand: "close_connection",
    label: "MC ASCII 4E",
  }),
  "mc-udp-3e": Object.freeze({
    variant: "mc-udp-3e",
    kind: "mc-udp",
    online: true,
    connectCommand: "open_mc_udp_connection",
    readCommand: "mc_udp_read",
    writeCommand: "mc_udp_write",
    disconnectCommand: "close_connection",
    label: "MC UDP 3E",
  }),
  "mc-udp-4e": Object.freeze({
    variant: "mc-udp-4e",
    kind: "mc-udp",
    online: true,
    connectCommand: "open_mc_udp_connection",
    readCommand: "mc_udp_read",
    writeCommand: "mc_udp_write",
    disconnectCommand: "close_connection",
    label: "MC UDP 4E",
  }),
  "mc-1e": Object.freeze({
    variant: "mc-1e",
    kind: "mc-1e",
    online: true,
    connectCommand: "open_mc_1e_tcp",
    readCommand: "mc_1e_read",
    writeCommand: "mc_1e_write",
    disconnectCommand: "close_connection",
    label: "A-1E / SLMP-1E",
  }),
  "mc-c24": Object.freeze({
    variant: "mc-c24",
    kind: "mc-c24-serial",
    online: true,
    connectCommand: "get_serial_status",
    readCommand: "mc_c24_serial_read",
    writeCommand: null,
    disconnectCommand: null,
    label: "MC-C24 串口",
    reason: "本阶段仅开放 C24 3C 帧只读；写入未实现，禁止走 FX 串口命令。",
  }),
  "fx-links": Object.freeze({
    variant: "fx-links",
    kind: "fx-links",
    online: true,
    connectCommand: "get_serial_status",
    readCommand: "fx_serial_transact",
    writeCommand: "fx_serial_transact",
    disconnectCommand: null,
    label: "FX Computer Link",
  }),
  "fx-prog": Object.freeze({
    variant: "fx-prog",
    kind: "fx-prog",
    online: true,
    connectCommand: "get_serial_status",
    readCommand: "fx_serial_transact",
    writeCommand: "fx_serial_transact",
    disconnectCommand: null,
    label: "FX 编程口",
  }),
});

function unknownMelsecRoute(variant) {
  const key = String(variant || "").trim().toLowerCase();
  return Object.freeze({
    variant: key,
    kind: "unknown",
    online: false,
    connectCommand: null,
    readCommand: null,
    writeCommand: null,
    disconnectCommand: null,
    label: key ? `未知三菱变体 (${key})` : "未选择三菱变体",
    reason: "未知或空变体不得回退到 MC 3E。",
  });
}

export function resolveMelsecRoute(variant) {
  const key = String(variant || "").trim().toLowerCase();
  return Object.prototype.hasOwnProperty.call(ROUTES, key) ? ROUTES[key] : unknownMelsecRoute(key);
}

export function listMelsecRoutes() {
  return Object.values(ROUTES).map((route) => ({ ...route }));
}
