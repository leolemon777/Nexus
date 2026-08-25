const { spawn } = require("node:child_process");
const { redactLogText } = require("./log-redaction-service.cjs");

const PROTOCOL_VERSION = 1;
const MAX_LINE_BYTES = 1024 * 1024;
const DEFAULT_REQUEST_TIMEOUT_MS = 5_000;
const DEFAULT_SHUTDOWN_GRACE_MS = 1_000;
const DEFAULT_MAX_STDERR_BUFFER_BYTES = 64 * 1024;
const MAX_PAYLOAD_NODES = 20_000;
const MAX_PAYLOAD_DEPTH = 8;

const COMMANDS = Object.freeze({
  HELLO: "hello",
  VALIDATE_SERIAL_CONFIG: "validate_serial_config",
  // RTU 读寄存器(向后兼容)
  BUILD_READ_HOLDING_REGISTERS: "build_read_holding_registers",
  PARSE_READ_HOLDING_REGISTERS: "parse_read_holding_registers",
  BUILD_READ_INPUT_REGISTERS: "build_read_input_registers",
  PARSE_READ_INPUT_REGISTERS: "parse_read_input_registers",
  // RTU 读位(FC01/FC02)
  BUILD_READ_COILS: "build_read_coils",
  PARSE_READ_COILS: "parse_read_coils",
  BUILD_READ_DISCRETE_INPUTS: "build_read_discrete_inputs",
  PARSE_READ_DISCRETE_INPUTS: "parse_read_discrete_inputs",
  // RTU 写操作(FC05/06/15/16)
  BUILD_WRITE_SINGLE_COIL: "build_write_single_coil",
  PARSE_WRITE_SINGLE_COIL: "parse_write_single_coil",
  BUILD_WRITE_SINGLE_REGISTER: "build_write_single_register",
  PARSE_WRITE_SINGLE_REGISTER: "parse_write_single_register",
  BUILD_WRITE_MULTIPLE_COILS: "build_write_multiple_coils",
  PARSE_WRITE_MULTIPLE_COILS: "parse_write_multiple_coils",
  BUILD_WRITE_MULTIPLE_REGISTERS: "build_write_multiple_registers",
  PARSE_WRITE_MULTIPLE_REGISTERS: "parse_write_multiple_registers",
  // ASCII 串口(FC01-06,15,16)
  BUILD_ASCII_READ_HOLDING_REGISTERS: "build_ascii_read_holding_registers",
  PARSE_ASCII_READ_HOLDING_REGISTERS: "parse_ascii_read_holding_registers",
  BUILD_ASCII_READ_INPUT_REGISTERS: "build_ascii_read_input_registers",
  PARSE_ASCII_READ_INPUT_REGISTERS: "parse_ascii_read_input_registers",
  BUILD_ASCII_READ_COILS: "build_ascii_read_coils",
  PARSE_ASCII_READ_COILS: "parse_ascii_read_coils",
  BUILD_ASCII_READ_DISCRETE_INPUTS: "build_ascii_read_discrete_inputs",
  PARSE_ASCII_READ_DISCRETE_INPUTS: "parse_ascii_read_discrete_inputs",
  BUILD_ASCII_WRITE_SINGLE_COIL: "build_ascii_write_single_coil",
  PARSE_ASCII_WRITE_SINGLE_COIL: "parse_ascii_write_single_coil",
  BUILD_ASCII_WRITE_SINGLE_REGISTER: "build_ascii_write_single_register",
  PARSE_ASCII_WRITE_SINGLE_REGISTER: "parse_ascii_write_single_register",
  BUILD_ASCII_WRITE_MULTIPLE_COILS: "build_ascii_write_multiple_coils",
  PARSE_ASCII_WRITE_MULTIPLE_COILS: "parse_ascii_write_multiple_coils",
  BUILD_ASCII_WRITE_MULTIPLE_REGISTERS: "build_ascii_write_multiple_registers",
  PARSE_ASCII_WRITE_MULTIPLE_REGISTERS: "parse_ascii_write_multiple_registers",
  // TCP/UDP 连接管理
  OPEN_TCP_CONNECTION: "open_tcp_connection",
  OPEN_UDP_CONNECTION: "open_udp_connection",
  CLOSE_CONNECTION: "close_connection",
  // TCP 端到端读写
  TCP_READ_COILS: "tcp_read_coils",
  TCP_READ_DISCRETE_INPUTS: "tcp_read_discrete_inputs",
  TCP_READ_HOLDING_REGISTERS: "tcp_read_holding_registers",
  TCP_READ_INPUT_REGISTERS: "tcp_read_input_registers",
  TCP_WRITE_SINGLE_COIL: "tcp_write_single_coil",
  TCP_WRITE_SINGLE_REGISTER: "tcp_write_single_register",
  TCP_WRITE_MULTIPLE_COILS: "tcp_write_multiple_coils",
  TCP_WRITE_MULTIPLE_REGISTERS: "tcp_write_multiple_registers",
  // UDP 端到端读写(framing 由 open_udp_connection 决定)
  UDP_READ_COILS: "udp_read_coils",
  UDP_READ_DISCRETE_INPUTS: "udp_read_discrete_inputs",
  UDP_READ_HOLDING_REGISTERS: "udp_read_holding_registers",
  UDP_READ_INPUT_REGISTERS: "udp_read_input_registers",
  UDP_WRITE_SINGLE_COIL: "udp_write_single_coil",
  UDP_WRITE_SINGLE_REGISTER: "udp_write_single_register",
  UDP_WRITE_MULTIPLE_COILS: "udp_write_multiple_coils",
  UDP_WRITE_MULTIPLE_REGISTERS: "udp_write_multiple_registers",
  DECODE_VALUES: "decode_values",
  SCAN_STATION_IDS: "scan_station_ids",
  // 从站模拟
  START_TCP_SLAVE: "start_tcp_slave",
  STOP_SLAVE: "stop_slave",
  SLAVE_SET_VALUE: "slave_set_value",
  SLAVE_SET_COIL: "slave_set_coil",
  SLAVE_CLEAR: "slave_clear",
  SLAVE_GET_MEMORY: "slave_get_memory",
  // 串口调试
  COMPUTE_CRC16: "compute_crc16",
  COMPUTE_LRC: "compute_lrc",
  PARSE_FRAME_ONLINE: "parse_frame_online",
  PARSE_FRAME_OFFLINE: "parse_frame_offline",
  // 流式轮询(v2)
  START_POLL_STREAM: "start_poll_stream",
  STOP_POLL_STREAM: "stop_poll_stream",
  // 三菱 MC 协议族(与 main.cjs 的 mc 转发循环、preload 白名单一一对应)
  MC_PARSE_ADDRESS: "mc_parse_address",
  MC_BUILD_READ: "mc_build_read",
  MC_BUILD_WRITE: "mc_build_write",
  MC_PARSE_RESPONSE: "mc_parse_response",
  OPEN_MC_TCP_CONNECTION: "open_mc_tcp_connection",
  MC_TCP_READ: "mc_tcp_read",
  MC_TCP_WRITE: "mc_tcp_write",
  START_MC_TCP_SLAVE: "start_mc_tcp_slave",
  STOP_MC_SLAVE: "stop_mc_slave",
  MC_SLAVE_SET: "mc_slave_set",
  MC_TCP_READ_RANDOM: "mc_tcp_read_random",
  MC_TCP_WRITE_RANDOM: "mc_tcp_write_random",
  MC_TCP_READ_BLOCKS: "mc_tcp_read_blocks",
  MC_REMOTE_RUN: "mc_remote_run",
  MC_REMOTE_STOP: "mc_remote_stop",
  MC_REMOTE_RESET: "mc_remote_reset",
  MC_REMOTE_PAUSE: "mc_remote_pause",
  MC_READ_CLOCK: "mc_read_clock",
  MC_ECHO_TEST: "mc_echo_test",
  MC_READ_CPU_TYPE: "mc_read_cpu_type",
  MC_READ_CPU_STATUS: "mc_read_cpu_status",
  MC_BUILD_ASCII_READ: "mc_build_ascii_read",
  OPEN_MC_ASCII_CONNECTION: "open_mc_ascii_connection",
  MC_ASCII_READ: "mc_ascii_read",
  MC_ASCII_WRITE: "mc_ascii_write",
  MC_SERIAL_BUILD_3C: "mc_serial_build_3c",
  MC_SERIAL_PARSE_3C: "mc_serial_parse_3c",
  MC_1E_BUILD_READ: "mc_1e_build_read",
  MC_1E_BUILD_WRITE: "mc_1e_build_write",
  MC_1E_PARSE: "mc_1e_parse",
  OPEN_MC_UDP_CONNECTION: "open_mc_udp_connection",
  MC_UDP_READ: "mc_udp_read",
  MC_UDP_WRITE: "mc_udp_write",
  MC_C24_READ: "mc_c24_read",
  MC_C24_PARSE_READ: "mc_c24_parse_read",
  OPEN_MC_1E_TCP: "open_mc_1e_tcp",
  MC_1E_READ: "mc_1e_read",
  MC_1E_WRITE: "mc_1e_write",
  // 三菱 FX 串口协议族
  FX_LINKS_BUILD: "fx_links_build",
  FX_LINKS_PARSE: "fx_links_parse",
  FX_LINKS_READ: "fx_links_read",
  FX_LINKS_WRITE_BITS: "fx_links_write_bits",
  FX_LINKS_WRITE_WORDS: "fx_links_write_words",
  FX_PROG_BUILD_READ: "fx_prog_build_read",
  FX_PROG_BUILD_WRITE: "fx_prog_build_write",
  FX_PROG_PARSE: "fx_prog_parse",
  // 高级功能码(TCP 透传)
  TCP_MASK_WRITE_REGISTER: "tcp_mask_write_register",
  TCP_READ_WRITE_MULTIPLE: "tcp_read_write_multiple",
  TCP_READ_DEVICE_ID: "tcp_read_device_id",
  TCP_DIAGNOSTICS: "tcp_diagnostics",
  TCP_READ_EXCEPTION_STATUS: "tcp_read_exception_status",
  TCP_GET_COMM_EVENT_COUNTER: "tcp_get_comm_event_counter",
  TCP_GET_COMM_EVENT_LOG: "tcp_get_comm_event_log",
  TCP_REPORT_SLAVE_ID: "tcp_report_slave_id",
  // 串口从站模拟(Electron 持 COM 句柄)
  START_SERIAL_SLAVE: "start_serial_slave",
  STOP_SERIAL_SLAVE: "stop_serial_slave",
  SLAVE_HANDLE_SERIAL_BYTES: "slave_handle_serial_bytes",
  SERIAL_SLAVE_SET_VALUE: "serial_slave_set_value",
  SERIAL_SLAVE_GET_MEMORY: "serial_slave_get_memory",
  // 品牌地址映射 + 欧姆龙 FINS
  BRAND_PARSE_ADDRESS: "brand_parse_address",
  FINS_PARSE_ADDRESS: "fins_parse_address",
  OPEN_FINS_TCP: "open_fins_tcp",
  OPEN_FINS_UDP: "open_fins_udp",
  FINS_READ: "fins_read",
  FINS_WRITE: "fins_write",
  START_FINS_SLAVE: "start_fins_slave",
  STOP_FINS_SLAVE: "stop_fins_slave",
  FINS_SLAVE_SET: "fins_slave_set",
  FINS_SLAVE_GET: "fins_slave_get",
  HOSTLINK_BUILD_FINS: "hostlink_build_fins",
  HOSTLINK_PARSE_FINS: "hostlink_parse_fins",
  HOSTLINK_BUILD_CMODE_READ: "hostlink_build_cmode_read",
  HOSTLINK_PARSE_CMODE_READ: "hostlink_parse_cmode_read",
  // 西门子 S7comm
  S7_PARSE_ADDRESS: "s7_parse_address",
  OPEN_S7_CONNECTION: "open_s7_connection",
  S7_READ: "s7_read",
  S7_WRITE: "s7_write",
  START_S7_SLAVE: "start_s7_slave",
  STOP_S7_SLAVE: "stop_s7_slave",
  S7_SLAVE_SET: "s7_slave_set",
  S7_SLAVE_GET: "s7_slave_get",
  S7_CPU_CONTROL: "s7_cpu_control",
  S7_READ_STATUS: "s7_read_status",
  S7_PASSWORD: "s7_password",
  OPEN_FW_TCP: "open_fw_tcp",
  FW_READ: "fw_read",
  FW_WRITE: "fw_write",
  START_FW_SLAVE: "start_fw_slave",
  STOP_FW_SLAVE: "stop_fw_slave",
  OPEN_PPI_TCP: "open_ppi_tcp",
  PPI_READ: "ppi_read",
  PPI_WRITE: "ppi_write",
  PPI_BUILD_READ: "ppi_build_read",
  PPI_BUILD_SA_CONFIRM: "ppi_build_sa_confirm",
  PPI_PARSE_READ_RESPONSE: "ppi_parse_read_response",
  START_PPI_SLAVE: "start_ppi_slave",
  STOP_PPI_SLAVE: "stop_ppi_slave",
  USS_BUILD_REQUEST: "uss_build_request",
  USS_PARSE_RESPONSE: "uss_parse_response",
  RK512_BUILD_READ: "rk512_build_read",
  RK512_BUILD_WRITE: "rk512_build_write",
  RK512_PARSE_RESPONSE: "rk512_parse_response",
  // Allen-Bradley EtherNet/IP + CIP（首轮 explicit 只读编解码）
  OPEN_ENIP_CONNECTION: "open_enip_connection",
  ENIP_READ_TAG: "enip_read_tag",
  ENIP_BUILD_REGISTER_SESSION: "enip_build_register_session",
  ENIP_BUILD_UNREGISTER_SESSION: "enip_build_unregister_session",
  ENIP_BUILD_READ_TAG: "enip_build_read_tag",
  ENIP_PARSE_FRAME: "enip_parse_frame",
  ENIP_PARSE_CIP_RESPONSE: "enip_parse_cip_response",
  // Beckhoff ADS/AMS（TCP 只读会话 + 编解码）
  OPEN_ADS_CONNECTION: "open_ads_connection",
  ADS_READ: "ads_read",
  ADS_READ_DEVICE_INFO: "ads_read_device_info",
  ADS_READ_STATE: "ads_read_state",
  ADS_BUILD_READ: "ads_build_read",
  ADS_BUILD_WRITE: "ads_build_write",
  ADS_BUILD_READWRITE: "ads_build_readwrite",
  ADS_BUILD_READ_DEVICE_INFO: "ads_build_read_device_info",
  ADS_BUILD_READ_STATE: "ads_build_read_state",
  ADS_PARSE_FRAME: "ads_parse_frame",
  ADS_PARSE_RESPONSE: "ads_parse_response",
  // MQTT 3.1.1（TCP 只读订阅 + 编解码）
  OPEN_MQTT_CONNECTION: "open_mqtt_connection",
  MQTT_SUBSCRIBE: "mqtt_subscribe",
  MQTT_READ_PUBLISH: "mqtt_read_publish",
  MQTT_PING: "mqtt_ping",
  MQTT_BUILD_CONNECT: "mqtt_build_connect",
  MQTT_PARSE_CONNACK: "mqtt_parse_connack",
  MQTT_BUILD_SUBSCRIBE: "mqtt_build_subscribe",
  MQTT_PARSE_SUBACK: "mqtt_parse_suback",
  MQTT_PARSE_PUBLISH: "mqtt_parse_publish",
  MQTT_BUILD_PINGREQ: "mqtt_build_pingreq",
  MQTT_PARSE_PINGRESP: "mqtt_parse_pingresp",
  MQTT_BUILD_DISCONNECT: "mqtt_build_disconnect",
  // IEC 60870-5-104（TCP 只读 Client/Master + 总召）
  OPEN_IEC104_CONNECTION: "open_iec104_connection",
  IEC104_GENERAL_INTERROGATION: "iec104_general_interrogation",
  IEC104_TEST_FRAME: "iec104_test_frame",
  IEC104_BUILD_I_FRAME: "iec104_build_i_frame",
  IEC104_BUILD_S_FRAME: "iec104_build_s_frame",
  IEC104_BUILD_U_FRAME: "iec104_build_u_frame",
  IEC104_PARSE_APDU: "iec104_parse_apdu",
  IEC104_BUILD_GENERAL_INTERROGATION: "iec104_build_general_interrogation",
  IEC104_PARSE_ASDU: "iec104_parse_asdu",
  // DNP3（TCP 只读 Master + Class 0/1/2/3）
  OPEN_DNP3_CONNECTION: "open_dnp3_connection",
  DNP3_INTEGRITY_POLL: "dnp3_integrity_poll",
  DNP3_CLASS_SCAN: "dnp3_class_scan",
  DNP3_READ: "dnp3_read",
  DNP3_BUILD_LINK_FRAME: "dnp3_build_link_frame",
  DNP3_PARSE_LINK_FRAME: "dnp3_parse_link_frame",
  DNP3_BUILD_CLASS_SCAN: "dnp3_build_class_scan",
  DNP3_BUILD_READ_REQUEST: "dnp3_build_read_request",
  DNP3_PARSE_APPLICATION_RESPONSE: "dnp3_parse_application_response",
  DNP3_BUILD_CONFIRM: "dnp3_build_confirm",
  // DL/T 645-1997/2007（RS-485 只读电表编解码）
  DLT645_PARSE_ADDRESS: "dlt645_parse_address",
  DLT645_PARSE_DATA_ID: "dlt645_parse_data_id",
  DLT645_BUILD_READ_REQUEST: "dlt645_build_read_request",
  DLT645_PARSE_FRAME: "dlt645_parse_frame",
  DLT645_PARSE_READ_RESPONSE: "dlt645_parse_read_response",
  // CJ/T 188-2004（水/气/热表离线只读编解码）
  CJT188_PARSE_METER_TYPE: "cjt188_parse_meter_type",
  CJT188_PARSE_ADDRESS: "cjt188_parse_address",
  CJT188_PARSE_DATA_ID: "cjt188_parse_data_id",
  CJT188_BUILD_READ_REQUEST: "cjt188_build_read_request",
  CJT188_PARSE_FRAME: "cjt188_parse_frame",
  CJT188_PARSE_READ_RESPONSE: "cjt188_parse_read_response",
  // BACnet/IP（Who-Is/I-Am 离线只读编解码）
  BACNET_IP_BUILD_WHOIS: "bacnet_ip_build_whois",
  BACNET_IP_BUILD_IAM: "bacnet_ip_build_iam",
  BACNET_IP_PARSE_FRAME: "bacnet_ip_parse_frame",
  BACNET_IP_BUILD_READ_PROPERTY_REQUEST: "bacnet_ip_build_read_property_request",
  BACNET_IP_PARSE_READ_PROPERTY_REQUEST: "bacnet_ip_parse_read_property_request",
  BACNET_IP_PARSE_READ_PROPERTY_ACK: "bacnet_ip_parse_read_property_ack",
  OPEN_BACNET_IP_CONNECTION: "open_bacnet_ip_connection",
  BACNET_IP_WHOIS: "bacnet_ip_whois",
  BACNET_IP_READ_PROPERTY_LIVE: "bacnet_ip_read_property_live",
  // KNXnet/IP Tunneling v1（离线只读编解码）
  KNX_PARSE_GROUP_ADDRESS: "knx_parse_group_address",
  KNX_BUILD_CONNECT_REQUEST: "knx_build_connect_request",
  KNX_PARSE_CONNECT_RESPONSE: "knx_parse_connect_response",
  KNX_BUILD_GROUP_READ_REQUEST: "knx_build_group_read_request",
  KNX_PARSE_TUNNELING_REQUEST: "knx_parse_tunneling_request",
  KNX_PARSE_GROUP_VALUE_RESPONSE: "knx_parse_group_value_response",
  KNX_PARSE_TUNNELING_ACK: "knx_parse_tunneling_ack",
  KNX_BUILD_TUNNELING_ACK: "knx_build_tunneling_ack",
  OPEN_KNX_CONNECTION: "open_knx_connection",
  KNX_GROUP_READ: "knx_group_read",
  KNX_DISCONNECT: "knx_disconnect",
  KNX_CONNECTION_STATE: "knx_connection_state",
  KNX_START_KEEPALIVE: "knx_start_keepalive",
  KNX_STOP_KEEPALIVE: "knx_stop_keepalive",
  KNX_KEEPALIVE_STATUS: "knx_keepalive_status",
  // Keyence KV Host Link ASCII（TCP 只读会话 + 编解码）
  OPEN_KEYENCE_CONNECTION: "open_keyence_connection",
  KEYENCE_READ_WORDS: "keyence_read_words",
  KEYENCE_READ_BITS: "keyence_read_bits",
  KEYENCE_PARSE_ADDRESS: "keyence_parse_address",
  KEYENCE_BUILD_CONNECT: "keyence_build_connect",
  KEYENCE_BUILD_READ_WORDS: "keyence_build_read_words",
  KEYENCE_BUILD_READ_BITS: "keyence_build_read_bits",
  KEYENCE_BUILD_WRITE_WORDS: "keyence_build_write_words",
  KEYENCE_BUILD_WRITE_BIT: "keyence_build_write_bit",
  KEYENCE_PARSE_CONNECT: "keyence_parse_connect",
  KEYENCE_PARSE_WORDS: "keyence_parse_words",
  KEYENCE_PARSE_BITS: "keyence_parse_bits",
  KEYENCE_PARSE_WRITE: "keyence_parse_write",
  // Delta DVP/AS Modbus 地址 profile
  DELTA_PARSE_ADDRESS: "delta_parse_address",
  // Inovance H3U/H5U Modbus 地址 profile
  INOVANCE_PARSE_ADDRESS: "inovance_parse_address",
  // Xinje XC/XD Modbus 地址 profile（首轮仅确认 D）
  XINJIE_PARSE_ADDRESS: "xinjie_parse_address",
  // FATEK FBs native ASCII first-round codec
  OPEN_FATEK_CONNECTION: "open_fatek_connection",
  FATEK_READ_WORDS: "fatek_read_words",
  FATEK_READ_DISCRETE: "fatek_read_discrete",
  FATEK_PARSE_ADDRESS: "fatek_parse_address",
  FATEK_PACK_COMMAND: "fatek_pack_command",
  FATEK_BUILD_READ_DISCRETE: "fatek_build_read_discrete",
  FATEK_BUILD_WRITE_DISCRETE: "fatek_build_write_discrete",
  FATEK_BUILD_READ_WORDS: "fatek_build_read_words",
  FATEK_BUILD_WRITE_WORDS: "fatek_build_write_words",
  FATEK_PARSE_RESPONSE: "fatek_parse_response",
  OPEN_FUJI_SPH_CONNECTION: "open_fuji_sph_connection",
  FUJI_SPH_READ: "fuji_sph_read",
  FUJI_SPH_PARSE_ADDRESS: "fuji_sph_parse_address",
  FUJI_SPH_BUILD_READ: "fuji_sph_build_read",
  FUJI_SPH_BUILD_WRITE: "fuji_sph_build_write",
  FUJI_SPH_PARSE_RESPONSE: "fuji_sph_parse_response",
  // GE Series 90 / PACSystems SRTP（离线编解码 + TCP 只读会话）
  OPEN_GE_SRTP_CONNECTION: "open_ge_srtp_connection",
  GE_SRTP_READ: "ge_srtp_read",
  GE_SRTP_PARSE_ADDRESS: "ge_srtp_parse_address",
  GE_SRTP_BUILD_HANDSHAKE: "ge_srtp_build_handshake",
  GE_SRTP_PARSE_HANDSHAKE: "ge_srtp_parse_handshake",
  GE_SRTP_BUILD_READ: "ge_srtp_build_read",
  GE_SRTP_BUILD_WRITE: "ge_srtp_build_write",
  GE_SRTP_PARSE_RESPONSE: "ge_srtp_parse_response",
  // LS Electric XGT FEnet（TCP 只读会话 + 编解码）
  OPEN_LS_XGT_CONNECTION: "open_ls_xgt_connection",
  LS_XGT_READ: "ls_xgt_read",
  LS_XGT_READ_CONTINUOUS: "ls_xgt_read_continuous",
  LS_XGT_PARSE_ADDRESS: "ls_xgt_parse_address",
  LS_XGT_BUILD_READ: "ls_xgt_build_read",
  LS_XGT_BUILD_CONTINUOUS_READ: "ls_xgt_build_continuous_read",
  LS_XGT_BUILD_WRITE: "ls_xgt_build_write",
  LS_XGT_BUILD_CONTINUOUS_WRITE: "ls_xgt_build_continuous_write",
  LS_XGT_PARSE_RESPONSE: "ls_xgt_parse_response",
  // Panasonic MEWTOCOL-COM（首轮离线编解码）
  PANASONIC_PARSE_DATA_ADDRESS: "panasonic_parse_data_address",
  PANASONIC_PARSE_CONTACT_ADDRESS: "panasonic_parse_contact_address",
  PANASONIC_BUILD_READ: "panasonic_build_read",
  PANASONIC_BUILD_WRITE: "panasonic_build_write",
  PANASONIC_BUILD_READ_CONTACT: "panasonic_build_read_contact",
  PANASONIC_BUILD_WRITE_CONTACT: "panasonic_build_write_contact",
  PANASONIC_PARSE_RESPONSE: "panasonic_parse_response",
  SHUTDOWN: "shutdown",
});
const ALLOWED_COMMANDS = new Set(Object.values(COMMANDS));

function validatePayloadShape(payload) {
  if (payload === undefined) return {};
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    return { error: "Rust core request payload must be an object" };
  }

  const stack = [[payload, 1]];
  let nodes = 0;
  while (stack.length) {
    const [value, depth] = stack.pop();
    nodes += 1;
    if (nodes > MAX_PAYLOAD_NODES) {
      return { error: `Rust core request payload exceeds ${MAX_PAYLOAD_NODES} nodes` };
    }
    if (depth > MAX_PAYLOAD_DEPTH) {
      return { error: `Rust core request payload exceeds depth ${MAX_PAYLOAD_DEPTH}` };
    }
    if (!value || typeof value !== "object") continue;
    for (const [key, nested] of Object.entries(value)) {
      if (key === "__proto__" || key === "constructor") {
        return { error: `Rust core request payload contains unsafe key: ${key}` };
      }
      stack.push([nested, depth + 1]);
    }
  }
  return { value: payload };
}

class RustCoreClientError extends Error {
  constructor(message, { code = "RUST_CORE_CLIENT_ERROR", cause, details } = {}) {
    super(message, cause ? { cause } : undefined);
    this.name = "RustCoreClientError";
    this.code = code;
    if (details !== undefined) this.details = details;
  }
}

class RustCoreRemoteError extends RustCoreClientError {
  constructor(error, { requestId, command } = {}) {
    super(error.message, {
      code: error.code,
      details: error.details,
    });
    this.name = "RustCoreRemoteError";
    this.requestId = requestId;
    this.command = command;
  }
}

class RustCoreClient {
  constructor({
    binaryPath = process.env.NEXUS_RUST_CORE_PATH,
    spawnImpl = spawn,
    requestTimeoutMs = DEFAULT_REQUEST_TIMEOUT_MS,
    shutdownGraceMs = DEFAULT_SHUTDOWN_GRACE_MS,
    maxLineBytes = MAX_LINE_BYTES,
    maxStderrBufferBytes = DEFAULT_MAX_STDERR_BUFFER_BYTES,
    logger = console,
  } = {}) {
    if (typeof spawnImpl !== "function") throw new TypeError("spawnImpl must be a function");
    assertPositiveInteger(requestTimeoutMs, "requestTimeoutMs");
    assertPositiveInteger(shutdownGraceMs, "shutdownGraceMs");
    assertPositiveInteger(maxLineBytes, "maxLineBytes");
    assertPositiveInteger(maxStderrBufferBytes, "maxStderrBufferBytes");

    this.binaryPath = binaryPath;
    this.spawnImpl = spawnImpl;
    this.requestTimeoutMs = requestTimeoutMs;
    this.shutdownGraceMs = shutdownGraceMs;
    this.maxLineBytes = maxLineBytes;
    this.maxStderrBufferBytes = maxStderrBufferBytes;
    this.logger = logger;

    this.child = null;
    this.state = "idle";
    this.pending = new Map();
    this.subscriptions = new Map(); // streamId → { onData, onError }
    this.nextRequestId = 1;
    this.stdoutBuffer = Buffer.alloc(0);
    this.stderrBuffer = "";
    this.startPromise = null;
    this.shutdownPromise = null;
    this.helloResult = null;
    this.shutdownResult = null;
    this.closePromise = null;
    this.resolveClose = null;
  }

  async start() {
    if (this.state === "ready") return this.helloResult;
    if (this.state === "starting") return this.startPromise;
    if (this.state !== "idle") {
      throw this._clientError(`Rust core cannot start while client is ${this.state}`, "INVALID_STATE");
    }
    if (typeof this.binaryPath !== "string" || this.binaryPath.trim() === "") {
      throw this._clientError(
        "Rust core binary path is required (binaryPath or NEXUS_RUST_CORE_PATH)",
        "BINARY_PATH_REQUIRED",
      );
    }

    this.state = "starting";
    this.startPromise = this._startAndHandshake();
    return this.startPromise;
  }

  async _startAndHandshake() {
    try {
      this._spawnChild();
      const result = await this._sendRequest(COMMANDS.HELLO, {}, this.requestTimeoutMs, ["starting"]);
      if (
        result &&
        Object.hasOwn(result, "protocolVersion") &&
        result.protocolVersion !== PROTOCOL_VERSION
      ) {
        throw this._clientError(
          `Rust core hello returned protocol version ${String(result.protocolVersion)}`,
          "PROTOCOL_VERSION_MISMATCH",
        );
      }
      this.helloResult = result;
      this.state = "ready";
      return result;
    } catch (error) {
      this._abort(error);
      throw error;
    }
  }

  _spawnChild() {
    try {
      this.child = this.spawnImpl(this.binaryPath, [], {
        stdio: ["pipe", "pipe", "pipe"],
        windowsHide: true,
      });
    } catch (error) {
      throw this._clientError(`Failed to spawn Rust core: ${error.message}`, "SPAWN_FAILED", error);
    }

    if (!this.child?.stdin || !this.child?.stdout || !this.child?.stderr) {
      throw this._clientError("Rust core child must expose stdin, stdout, and stderr", "INVALID_CHILD");
    }

    this.closePromise = new Promise((resolve) => {
      this.resolveClose = resolve;
    });
    this.child.stdin.on("error", (error) => {
      if (this.state === "failed" || this.state === "stopped") return;
      this._abort(this._clientError(`Rust core stdin failed: ${error.message}`, "STDIN_ERROR", error));
    });
    this.child.stdout.on("data", (chunk) => this._onStdout(chunk));
    this.child.stdout.on("error", (error) => {
      this._abort(this._clientError(`Rust core stdout failed: ${error.message}`, "STDOUT_ERROR", error));
    });
    this.child.stderr.on("data", (chunk) => this._onStderr(chunk));
    this.child.stderr.on("error", (error) => {
      this._log("error", `stderr failed: ${error.message}`);
    });
    this.child.once("error", (error) => {
      this._abort(this._clientError(`Rust core process failed: ${error.message}`, "PROCESS_ERROR", error));
    });
    this.child.once("close", (code, signal) => this._onClose(code, signal));
  }

  async request(command, payload = {}, { timeoutMs = this.requestTimeoutMs } = {}) {
    if (this.state !== "ready") {
      throw this._clientError(`Rust core is not ready (state: ${this.state})`, "NOT_READY");
    }
    return this._sendRequest(command, payload, timeoutMs, ["ready"]);
  }

  async hello() {
    if (this.state === "idle" || this.state === "starting") return this.start();
    // 发 clientVersion=2 进行版本协商
    return this.request(COMMANDS.HELLO, { clientVersion: 2 });
  }

  async validateSerialConfig(config) {
    return this.request(COMMANDS.VALIDATE_SERIAL_CONFIG, { config });
  }

  async buildReadHoldingRegisters({ unitId, startAddress, quantity }) {
    return this.request(COMMANDS.BUILD_READ_HOLDING_REGISTERS, {
      unitId,
      startAddress,
      quantity,
    });
  }

  async parseReadHoldingRegisters({ response, unitId, quantity }) {
    return this.request(COMMANDS.PARSE_READ_HOLDING_REGISTERS, {
      response,
      unitId,
      quantity,
    });
  }

  async buildReadInputRegisters({ unitId, startAddress, quantity }) {
    return this.request(COMMANDS.BUILD_READ_INPUT_REGISTERS, {
      unitId,
      startAddress,
      quantity,
    });
  }

  async parseReadInputRegisters({ response, unitId, quantity }) {
    return this.request(COMMANDS.PARSE_READ_INPUT_REGISTERS, {
      response,
      unitId,
      quantity,
    });
  }

  async buildPpiRead(payload) {
    return this.request(COMMANDS.PPI_BUILD_READ, payload);
  }

  async buildPpiSaConfirm(payload) {
    return this.request(COMMANDS.PPI_BUILD_SA_CONFIRM, payload);
  }

  async parsePpiReadResponse(payload) {
    return this.request(COMMANDS.PPI_PARSE_READ_RESPONSE, payload);
  }

  async buildHostLinkCmodeRead(payload) {
    return this.request(COMMANDS.HOSTLINK_BUILD_CMODE_READ, payload);
  }

  async parseHostLinkCmodeRead(payload) {
    return this.request(COMMANDS.HOSTLINK_PARSE_CMODE_READ, payload);
  }

  // === RTU 读位(FC01/FC02)===
  async buildReadCoils({ unitId, startAddress, quantity }) {
    return this.request(COMMANDS.BUILD_READ_COILS, { unitId, startAddress, quantity });
  }
  async parseReadCoils({ response, unitId, quantity }) {
    return this.request(COMMANDS.PARSE_READ_COILS, { response, unitId, quantity });
  }
  async buildReadDiscreteInputs({ unitId, startAddress, quantity }) {
    return this.request(COMMANDS.BUILD_READ_DISCRETE_INPUTS, { unitId, startAddress, quantity });
  }
  async parseReadDiscreteInputs({ response, unitId, quantity }) {
    return this.request(COMMANDS.PARSE_READ_DISCRETE_INPUTS, { response, unitId, quantity });
  }

  // === RTU 写操作(FC05/06/15/16)===
  async buildWriteSingleCoil({ unitId, address, value }) {
    return this.request(COMMANDS.BUILD_WRITE_SINGLE_COIL, { unitId, address, value });
  }
  async parseWriteSingleCoil({ response, unitId }) {
    return this.request(COMMANDS.PARSE_WRITE_SINGLE_COIL, { response, unitId });
  }
  async buildWriteSingleRegister({ unitId, address, value }) {
    return this.request(COMMANDS.BUILD_WRITE_SINGLE_REGISTER, { unitId, address, value });
  }
  async parseWriteSingleRegister({ response, unitId }) {
    return this.request(COMMANDS.PARSE_WRITE_SINGLE_REGISTER, { response, unitId });
  }
  async buildWriteMultipleCoils({ unitId, address, values }) {
    return this.request(COMMANDS.BUILD_WRITE_MULTIPLE_COILS, { unitId, address, values });
  }
  async parseWriteMultipleCoils({ response, unitId }) {
    return this.request(COMMANDS.PARSE_WRITE_MULTIPLE_COILS, { response, unitId });
  }
  async buildWriteMultipleRegisters({ unitId, address, values }) {
    return this.request(COMMANDS.BUILD_WRITE_MULTIPLE_REGISTERS, { unitId, address, values });
  }
  async parseWriteMultipleRegisters({ response, unitId }) {
    return this.request(COMMANDS.PARSE_WRITE_MULTIPLE_REGISTERS, { response, unitId });
  }

  // === ASCII 串口(FC01-06,15,16)===
  async buildAsciiReadHoldingRegisters({ unitId, startAddress, quantity }) {
    return this.request(COMMANDS.BUILD_ASCII_READ_HOLDING_REGISTERS, {
      unitId,
      startAddress,
      quantity,
    });
  }
  async parseAsciiReadHoldingRegisters({ response, unitId, quantity }) {
    return this.request(COMMANDS.PARSE_ASCII_READ_HOLDING_REGISTERS, {
      response,
      unitId,
      quantity,
    });
  }
  async buildAsciiReadInputRegisters({ unitId, startAddress, quantity }) {
    return this.request(COMMANDS.BUILD_ASCII_READ_INPUT_REGISTERS, {
      unitId,
      startAddress,
      quantity,
    });
  }
  async parseAsciiReadInputRegisters({ response, unitId, quantity }) {
    return this.request(COMMANDS.PARSE_ASCII_READ_INPUT_REGISTERS, {
      response,
      unitId,
      quantity,
    });
  }
  async buildAsciiReadCoils({ unitId, startAddress, quantity }) {
    return this.request(COMMANDS.BUILD_ASCII_READ_COILS, { unitId, startAddress, quantity });
  }
  async parseAsciiReadCoils({ response, unitId, quantity }) {
    return this.request(COMMANDS.PARSE_ASCII_READ_COILS, { response, unitId, quantity });
  }
  async buildAsciiReadDiscreteInputs({ unitId, startAddress, quantity }) {
    return this.request(COMMANDS.BUILD_ASCII_READ_DISCRETE_INPUTS, {
      unitId,
      startAddress,
      quantity,
    });
  }
  async parseAsciiReadDiscreteInputs({ response, unitId, quantity }) {
    return this.request(COMMANDS.PARSE_ASCII_READ_DISCRETE_INPUTS, { response, unitId, quantity });
  }
  async buildAsciiWriteSingleCoil({ unitId, address, value }) {
    return this.request(COMMANDS.BUILD_ASCII_WRITE_SINGLE_COIL, { unitId, address, value });
  }
  async parseAsciiWriteSingleCoil({ response, unitId }) {
    return this.request(COMMANDS.PARSE_ASCII_WRITE_SINGLE_COIL, { response, unitId });
  }
  async buildAsciiWriteSingleRegister({ unitId, address, value }) {
    return this.request(COMMANDS.BUILD_ASCII_WRITE_SINGLE_REGISTER, { unitId, address, value });
  }
  async parseAsciiWriteSingleRegister({ response, unitId }) {
    return this.request(COMMANDS.PARSE_ASCII_WRITE_SINGLE_REGISTER, { response, unitId });
  }
  async buildAsciiWriteMultipleCoils({ unitId, address, values }) {
    return this.request(COMMANDS.BUILD_ASCII_WRITE_MULTIPLE_COILS, { unitId, address, values });
  }
  async parseAsciiWriteMultipleCoils({ response, unitId }) {
    return this.request(COMMANDS.PARSE_ASCII_WRITE_MULTIPLE_COILS, { response, unitId });
  }
  async buildAsciiWriteMultipleRegisters({ unitId, address, values }) {
    return this.request(COMMANDS.BUILD_ASCII_WRITE_MULTIPLE_REGISTERS, { unitId, address, values });
  }
  async parseAsciiWriteMultipleRegisters({ response, unitId }) {
    return this.request(COMMANDS.PARSE_ASCII_WRITE_MULTIPLE_REGISTERS, { response, unitId });
  }

  // === TCP/UDP 连接管理(framing: standard | rtu-over-tcp | ascii-over-tcp) ===
  async openTcpConnection({ connectionId, host, port, unitId, framing = "standard" }) {
    return this.request(COMMANDS.OPEN_TCP_CONNECTION, {
      connectionId,
      host,
      port,
      unitId,
      framing,
    });
  }
  async openUdpConnection({ connectionId, host, port, unitId, framing = "standard" }) {
    return this.request(COMMANDS.OPEN_UDP_CONNECTION, {
      connectionId,
      host,
      port,
      unitId,
      framing,
    });
  }
  async closeConnection({ connectionId }) {
    return this.request(COMMANDS.CLOSE_CONNECTION, { connectionId });
  }

  // === TCP 端到端读写 ===
  async tcpReadCoils({ connectionId, startAddress, quantity }) {
    return this.request(COMMANDS.TCP_READ_COILS, { connectionId, startAddress, quantity });
  }
  async tcpReadDiscreteInputs({ connectionId, startAddress, quantity }) {
    return this.request(COMMANDS.TCP_READ_DISCRETE_INPUTS, {
      connectionId,
      startAddress,
      quantity,
    });
  }
  async tcpReadHoldingRegisters({ connectionId, startAddress, quantity }) {
    return this.request(COMMANDS.TCP_READ_HOLDING_REGISTERS, {
      connectionId,
      startAddress,
      quantity,
    });
  }
  async tcpReadInputRegisters({ connectionId, startAddress, quantity }) {
    return this.request(COMMANDS.TCP_READ_INPUT_REGISTERS, {
      connectionId,
      startAddress,
      quantity,
    });
  }
  async tcpWriteSingleCoil({ connectionId, address, value }) {
    return this.request(COMMANDS.TCP_WRITE_SINGLE_COIL, { connectionId, address, value });
  }
  async tcpWriteSingleRegister({ connectionId, address, value }) {
    return this.request(COMMANDS.TCP_WRITE_SINGLE_REGISTER, { connectionId, address, value });
  }
  async tcpWriteMultipleCoils({ connectionId, address, values }) {
    return this.request(COMMANDS.TCP_WRITE_MULTIPLE_COILS, { connectionId, address, values });
  }
  async tcpWriteMultipleRegisters({ connectionId, address, values }) {
    return this.request(COMMANDS.TCP_WRITE_MULTIPLE_REGISTERS, {
      connectionId,
      address,
      values,
    });
  }

  // === UDP 端到端读写 ===
  async udpReadCoils({ connectionId, startAddress, quantity }) {
    return this.request(COMMANDS.UDP_READ_COILS, { connectionId, startAddress, quantity });
  }
  async udpReadDiscreteInputs({ connectionId, startAddress, quantity }) {
    return this.request(COMMANDS.UDP_READ_DISCRETE_INPUTS, {
      connectionId,
      startAddress,
      quantity,
    });
  }
  async udpReadHoldingRegisters({ connectionId, startAddress, quantity }) {
    return this.request(COMMANDS.UDP_READ_HOLDING_REGISTERS, {
      connectionId,
      startAddress,
      quantity,
    });
  }
  async udpReadInputRegisters({ connectionId, startAddress, quantity }) {
    return this.request(COMMANDS.UDP_READ_INPUT_REGISTERS, {
      connectionId,
      startAddress,
      quantity,
    });
  }
  async udpWriteSingleCoil({ connectionId, address, value }) {
    return this.request(COMMANDS.UDP_WRITE_SINGLE_COIL, { connectionId, address, value });
  }
  async udpWriteSingleRegister({ connectionId, address, value }) {
    return this.request(COMMANDS.UDP_WRITE_SINGLE_REGISTER, { connectionId, address, value });
  }
  async udpWriteMultipleCoils({ connectionId, address, values }) {
    return this.request(COMMANDS.UDP_WRITE_MULTIPLE_COILS, { connectionId, address, values });
  }
  async udpWriteMultipleRegisters({ connectionId, address, values }) {
    return this.request(COMMANDS.UDP_WRITE_MULTIPLE_REGISTERS, {
      connectionId,
      address,
      values,
    });
  }

  // === 值解码(纯计算,对标 28 种显示格式) ===
  async decodeValues({ registers, dataType, offset, count, scale, offsetValue }) {
    return this.request(COMMANDS.DECODE_VALUES, {
      registers,
      dataType,
      offset,
      count,
      scale,
      offsetValue,
    });
  }

  // === 扫描站号(仅 TCP/UDP 连接) ===
  async scanStationIds({ connectionId, rangeStart = 1, rangeEnd = 247, timeoutMs = 500 }) {
    return this.request(COMMANDS.SCAN_STATION_IDS, {
      connectionId,
      rangeStart,
      rangeEnd,
      timeoutMs,
    });
  }

  // === 从站模拟 ===
  async startTcpSlave({ slaveId, port, allowedStationIds = [] }) {
    return this.request(COMMANDS.START_TCP_SLAVE, { slaveId, port, allowedStationIds });
  }
  async stopSlave({ slaveId }) {
    return this.request(COMMANDS.STOP_SLAVE, { slaveId });
  }
  async slaveSetValue({ slaveId, area, address, values }) {
    return this.request(COMMANDS.SLAVE_SET_VALUE, { slaveId, area, address, values });
  }
  async slaveSetCoil({ slaveId, area, address, values }) {
    return this.request(COMMANDS.SLAVE_SET_COIL, { slaveId, area, address, values });
  }
  async slaveClear({ slaveId, area }) {
    return this.request(COMMANDS.SLAVE_CLEAR, { slaveId, area });
  }
  async slaveGetMemory({ slaveId, area, address, count }) {
    return this.request(COMMANDS.SLAVE_GET_MEMORY, { slaveId, area, address, count });
  }

  // === 流式轮询(v2 协议)===
  async startPollStream({ streamId, connectionId, fc, startAddress, quantity, intervalMs = 1000 }, onData, onError) {
    this.subscriptions.set(streamId, { onData, onError });
    try {
      return await this.request(COMMANDS.START_POLL_STREAM, {
        streamId,
        connectionId,
        fc,
        startAddress,
        quantity,
        intervalMs,
      });
    } catch (error) {
      this.subscriptions.delete(streamId);
      throw error;
    }
  }
  async stopPollStream({ streamId }) {
    const result = this.request(COMMANDS.STOP_POLL_STREAM, { streamId });
    this.subscriptions.delete(streamId);
    return result;
  }

  async shutdown() {
    if (this.state === "idle" || this.state === "stopped") return this.shutdownResult;
    if (this.state === "stopping") return this.shutdownPromise;
    if (this.state === "starting") await this.startPromise;
    if (this.state === "failed") return this.shutdownResult;
    if (this.state !== "ready") {
      throw this._clientError(`Rust core cannot shut down while client is ${this.state}`, "INVALID_STATE");
    }

    this.state = "stopping";
    this.shutdownPromise = this._shutdownGracefully();
    return this.shutdownPromise;
  }

  async _shutdownGracefully() {
    try {
      this.shutdownResult = await this._sendRequest(
        COMMANDS.SHUTDOWN,
        {},
        this.requestTimeoutMs,
        ["stopping"],
      );
      const closed = await waitWithTimeout(this.closePromise, this.shutdownGraceMs);
      if (!closed) {
        this._log("warn", "shutdown grace period elapsed; terminating Rust core");
        this._killChild();
        this._finalizeClose(null, "SIGTERM");
      }
      return this.shutdownResult;
    } catch (error) {
      this._abort(error);
      throw error;
    }
  }

  _sendRequest(command, payload, timeoutMs, allowedStates) {
    if (!ALLOWED_COMMANDS.has(command)) {
      return Promise.reject(this._clientError(`Unsupported Rust core command: ${command}`, "UNKNOWN_COMMAND"));
    }
    const payloadShape = validatePayloadShape(payload);
    if (payloadShape.error) {
      return Promise.reject(this._clientError(payloadShape.error, "INVALID_PAYLOAD"));
    }
    if (!allowedStates.includes(this.state)) {
      return Promise.reject(
        this._clientError(`Command ${command} is not allowed while client is ${this.state}`, "INVALID_STATE"),
      );
    }
    assertPositiveInteger(timeoutMs, "timeoutMs");

    const requestId = String(this.nextRequestId++);
    const envelope = {
      protocolVersion: PROTOCOL_VERSION,
      requestId,
      command,
      payload: payloadShape.value,
    };
    let frame;
    try {
      frame = `${JSON.stringify(envelope)}\n`;
    } catch (error) {
      return Promise.reject(
        this._clientError(`Request payload is not JSON serializable: ${error.message}`, "INVALID_PAYLOAD", error),
      );
    }
    if (Buffer.byteLength(frame) - 1 > this.maxLineBytes) {
      return Promise.reject(this._clientError("Rust core request exceeds the JSONL line limit", "LINE_TOO_LONG"));
    }

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        if (!this.pending.delete(requestId)) return;
        reject(
          this._clientError(
            `Rust core request ${command} timed out after ${timeoutMs}ms`,
            "REQUEST_TIMEOUT",
            undefined,
            { requestId, command },
          ),
        );
      }, timeoutMs);
      timer.unref?.();
      this.pending.set(requestId, { command, resolve, reject, timer });

      try {
        this.child.stdin.write(frame, (error) => {
          if (!error) return;
          this._rejectPending(
            requestId,
            this._clientError(`Failed to write to Rust core: ${error.message}`, "STDIN_ERROR", error),
          );
        });
      } catch (error) {
        this._rejectPending(
          requestId,
          this._clientError(`Failed to write to Rust core: ${error.message}`, "STDIN_ERROR", error),
        );
      }
    });
  }

  _onStdout(chunk) {
    if (this.state === "failed" || this.state === "stopped") return;
    const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    this.stdoutBuffer = Buffer.concat([this.stdoutBuffer, bytes]);

    while (true) {
      const newlineIndex = this.stdoutBuffer.indexOf(0x0a);
      if (newlineIndex < 0) break;
      const hasCarriageReturn = newlineIndex > 0 && this.stdoutBuffer[newlineIndex - 1] === 0x0d;
      const lineByteLength = newlineIndex - (hasCarriageReturn ? 1 : 0);
      if (lineByteLength > this.maxLineBytes) {
        this._abort(this._clientError("Rust core response exceeds the JSONL line limit", "LINE_TOO_LONG"));
        return;
      }
      let line = this.stdoutBuffer.subarray(0, newlineIndex);
      this.stdoutBuffer = this.stdoutBuffer.subarray(newlineIndex + 1);
      if (hasCarriageReturn) line = line.subarray(0, -1);
      if (line.length === 0) {
        this._abort(this._clientError("Rust core emitted an empty stdout line", "INVALID_PROTOCOL"));
        return;
      }
      try {
        this._handleResponse(JSON.parse(line.toString("utf8")));
      } catch (error) {
        const protocolError =
          error instanceof RustCoreClientError
            ? error
            : this._clientError(`Rust core emitted invalid JSON: ${error.message}`, "INVALID_JSON", error);
        this._abort(protocolError);
        return;
      }
    }

    if (this.stdoutBuffer.length === 0) this.stdoutBuffer = Buffer.alloc(0);

    const trailingCarriageReturn = this.stdoutBuffer.at(-1) === 0x0d ? 1 : 0;
    if (this.stdoutBuffer.length - trailingCarriageReturn > this.maxLineBytes) {
      this._abort(this._clientError("Rust core response exceeds the JSONL line limit", "LINE_TOO_LONG"));
    }
  }

  _handleResponse(response) {
    if (!response || typeof response !== "object" || Array.isArray(response)) {
      throw this._clientError("Rust core response must be a JSON object", "INVALID_PROTOCOL");
    }

    // Start/stop stream acknowledgements carry both requestId and streamId.
    // They must resolve the pending request; requestId-less frames are pushes.
    const hasRequestId = typeof response.requestId === "string" && response.requestId !== "";

    // === 流式推送帧路由(v2:无 requestId,有 streamId)===
    if (!hasRequestId && response.streamId && typeof response.streamId === "string") {
      const sub = this.subscriptions.get(response.streamId);
      if (sub) {
        if (response.ok) {
          sub.onData?.(response.result);
        } else {
          sub.onError?.(response.error);
        }
        // streamEnd 时清理订阅
        if (response.streamEnd === true) {
          this.subscriptions.delete(response.streamId);
        }
      }
      return; // 不走 pending 路径
    }

    if (response.protocolVersion !== PROTOCOL_VERSION) {
      throw this._clientError(
        `Rust core response uses protocol version ${String(response.protocolVersion)}`,
        "PROTOCOL_VERSION_MISMATCH",
      );
    }
    if (!hasRequestId) {
      throw this._clientError("Rust core response requestId must be a non-empty string", "INVALID_PROTOCOL");
    }
    if (typeof response.ok !== "boolean") {
      throw this._clientError("Rust core response ok field must be boolean", "INVALID_PROTOCOL");
    }

    const entry = this.pending.get(response.requestId);
    if (!entry) {
      this._log("warn", `ignored response for unknown requestId ${response.requestId}`);
      return;
    }

    if (response.ok) {
      if (!Object.hasOwn(response, "result")) {
        throw this._clientError("Successful Rust core response must contain result", "INVALID_PROTOCOL");
      }
      if (response.error != null) {
        throw this._clientError("Successful Rust core response cannot contain an error", "INVALID_PROTOCOL");
      }
      this._resolvePending(response.requestId, response.result);
      return;
    }

    if (response.result != null) {
      throw this._clientError("Failed Rust core response cannot contain a result", "INVALID_PROTOCOL");
    }
    if (
      !response.error ||
      typeof response.error !== "object" ||
      typeof response.error.code !== "string" ||
      typeof response.error.message !== "string"
    ) {
      throw this._clientError("Failed Rust core response must contain code and message", "INVALID_PROTOCOL");
    }
    this._rejectPending(
      response.requestId,
      new RustCoreRemoteError(response.error, {
        requestId: response.requestId,
        command: entry.command,
      }),
    );
  }

  _onStderr(chunk) {
    this.stderrBuffer += Buffer.isBuffer(chunk) ? chunk.toString("utf8") : String(chunk);
    const lines = this.stderrBuffer.split(/\r?\n/);
    this.stderrBuffer = lines.pop();
    for (const line of lines) {
      if (line) this._log("error", line);
    }
    if (Buffer.byteLength(this.stderrBuffer, "utf8") > this.maxStderrBufferBytes) {
      const bytes = Buffer.from(this.stderrBuffer, "utf8");
      this.stderrBuffer = bytes.subarray(bytes.length - this.maxStderrBufferBytes).toString("utf8");
      this._log("warn", `unterminated stderr output truncated to ${this.maxStderrBufferBytes} bytes`);
    }
  }

  _onClose(code, signal) {
    if (this.stderrBuffer) {
      this._log("error", this.stderrBuffer);
      this.stderrBuffer = "";
    }
    this._finalizeClose(code, signal);
  }

  _finalizeClose(code, signal) {
    if (this.state === "stopped") return;
    const expected = this.state === "stopping";
    const error = this._clientError(
      `Rust core exited${code == null ? "" : ` with code ${code}`}${signal ? ` (${signal})` : ""}`,
      expected ? "PROCESS_CLOSED" : "PROCESS_CRASHED",
      undefined,
      { code, signal },
    );
    this.state = expected ? "stopped" : "failed";
    this._rejectAllPending(error);
    this.resolveClose?.({ code, signal });
    this.resolveClose = null;
  }

  _resolvePending(requestId, value) {
    const entry = this.pending.get(requestId);
    if (!entry) return false;
    this.pending.delete(requestId);
    clearTimeout(entry.timer);
    entry.resolve(value);
    return true;
  }

  _rejectPending(requestId, error) {
    const entry = this.pending.get(requestId);
    if (!entry) return false;
    this.pending.delete(requestId);
    clearTimeout(entry.timer);
    entry.reject(error);
    return true;
  }

  _rejectAllPending(error) {
    for (const requestId of [...this.pending.keys()]) this._rejectPending(requestId, error);
  }

  _abort(error) {
    if (this.state === "failed" || this.state === "stopped") return;
    this.state = "failed";
    this._rejectAllPending(error);
    this._killChild();
    this.resolveClose?.({ code: null, signal: "SIGTERM" });
    this.resolveClose = null;
  }

  _killChild() {
    if (!this.child || this.child.killed) return;
    try {
      this.child.kill();
    } catch (error) {
      this._log("error", `failed to terminate Rust core: ${error.message}`);
    }
  }

  _log(level, message) {
    const writer = this.logger?.[level];
    if (typeof writer === "function") writer.call(this.logger, `[rust-core] ${redactLogText(message)}`);
  }

  _clientError(message, code, cause, details) {
    return new RustCoreClientError(message, { code, cause, details });
  }
}

function assertPositiveInteger(value, label) {
  if (!Number.isInteger(value) || value <= 0) {
    throw new TypeError(`${label} must be a positive integer`);
  }
}

function waitWithTimeout(promise, timeoutMs) {
  return new Promise((resolve) => {
    let settled = false;
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      resolve(false);
    }, timeoutMs);
    timer.unref?.();
    promise.then(() => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(true);
    });
  });
}

module.exports = {
  COMMANDS,
  DEFAULT_MAX_STDERR_BUFFER_BYTES,
  MAX_PAYLOAD_DEPTH,
  MAX_PAYLOAD_NODES,
  MAX_LINE_BYTES,
  PROTOCOL_VERSION,
  RustCoreClient,
  RustCoreClientError,
  RustCoreRemoteError,
};
