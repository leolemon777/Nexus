/**
 * Modbus 主站 UI 功能码 + 传输方式到产品命令的唯一映射。
 *
 * - 串口 RTU/ASCII 走 Electron 持 COM 的 `*_once`。
 * - TCP / RTU-over-TCP / ASCII-over-TCP 走 `tcp_*`；framing 在连接时选定，禁止读写时改走另一套命令。
 * - UDP 必须走 `udp_*`，不得调用 `tcp_*`（Rust 会 CONNECTION_TYPE_MISMATCH）。
 * - 未知传输或功能码返回 null，由调用方 fail-closed，不得默认为 FC03/tcp。
 */

const TCP_LIKE = new Set(["tcp", "rtu-over-tcp", "ascii-over-tcp"]);

const SERIAL_READ = Object.freeze({
  1: "read_coils_once",
  2: "read_discrete_inputs_once",
  3: "read_holding_registers_once",
  4: "read_input_registers_once",
});

const SERIAL_WRITE = Object.freeze({
  5: "write_single_coil_once",
  6: "write_single_register_once",
  15: "write_multiple_coils_once",
  16: "write_multiple_registers_once",
});

const NETWORK_READ = Object.freeze({
  1: "read_coils",
  2: "read_discrete_inputs",
  3: "read_holding_registers",
  4: "read_input_registers",
});

const NETWORK_WRITE = Object.freeze({
  5: "write_single_coil",
  6: "write_single_register",
  15: "write_multiple_coils",
  16: "write_multiple_registers",
});

export const MODBUS_TRANSPORTS = Object.freeze([
  "rtu",
  "ascii",
  "tcp",
  "udp",
  "rtu-over-tcp",
  "ascii-over-tcp",
]);

export const MODBUS_READ_FUNCTION_CODES = Object.freeze([1, 2, 3, 4]);
export const MODBUS_WRITE_FUNCTION_CODES = Object.freeze([5, 6, 15, 16]);

export function modbusNetworkPrefix(transport) {
  const key = String(transport || "").trim().toLowerCase();
  if (key === "udp") return "udp";
  if (TCP_LIKE.has(key)) return "tcp";
  return null;
}

export function resolveModbusConnect(transport) {
  const key = String(transport || "").trim().toLowerCase();
  if (key === "udp") {
    return Object.freeze({ command: "open_udp_connection", framing: "standard" });
  }
  if (key === "tcp") {
    return Object.freeze({ command: "open_tcp_connection", framing: "standard" });
  }
  if (key === "rtu-over-tcp") {
    return Object.freeze({ command: "open_tcp_connection", framing: "rtu-over-tcp" });
  }
  if (key === "ascii-over-tcp") {
    return Object.freeze({ command: "open_tcp_connection", framing: "ascii-over-tcp" });
  }
  return Object.freeze({ command: null, framing: null });
}

export function resolveModbusReadCommand(transport, functionCode) {
  const fc = Number(functionCode);
  const prefix = modbusNetworkPrefix(transport);
  if (prefix) {
    const tail = NETWORK_READ[fc];
    return tail ? `${prefix}_${tail}` : null;
  }
  const key = String(transport || "").trim().toLowerCase();
  if (key !== "rtu" && key !== "ascii") return null;
  return SERIAL_READ[fc] || null;
}

export function resolveModbusWriteCommand(transport, functionCode) {
  const fc = Number(functionCode);
  const prefix = modbusNetworkPrefix(transport);
  if (prefix) {
    const tail = NETWORK_WRITE[fc];
    return tail ? `${prefix}_${tail}` : null;
  }
  const key = String(transport || "").trim().toLowerCase();
  if (key !== "rtu" && key !== "ascii") return null;
  return SERIAL_WRITE[fc] || null;
}

export function isModbusBitFunction(functionCode) {
  return [1, 2, 5, 15].includes(Number(functionCode));
}
