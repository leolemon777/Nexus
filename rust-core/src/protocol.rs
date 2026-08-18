use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use crate::{
    PROTOCOL_VERSION,
    error::CoreError,
    modbus_ascii,
    modbus_pdu as pdu,
    modbus_rtu::{
        self, RtuError, build_read_holding_registers_request, build_read_input_registers_request,
        crc16_modbus, modbus_exception_name, parse_read_holding_registers_response,
        parse_read_input_registers_response,
    },
    modbus_tcp,
    serial_config::SerialConfig,
    session::Session,
};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RequestEnvelope {
    protocol_version: u16,
    request_id: String,
    command: String,
    payload: Value,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResponseEnvelope {
    pub protocol_version: u16,
    pub request_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stream_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stream_end: Option<bool>,
    pub ok: bool,
    pub result: Option<Value>,
    pub error: Option<crate::error::ErrorBody>,
}

#[derive(Debug)]
pub struct CommandOutcome {
    pub response: ResponseEnvelope,
    pub shutdown: bool,
}

// =============================================================================
// Payload 结构(按命令分组)
// =============================================================================

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct ValidateSerialConfigPayload {
    config: Value,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildReadRegistersPayload {
    unit_id: u8,
    start_address: u16,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ParseReadRegistersPayload {
    response: Vec<u8>,
    unit_id: u8,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildReadBitsPayload {
    unit_id: u8,
    start_address: u16,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ParseReadBitsPayload {
    response: Vec<u8>,
    unit_id: u8,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildWriteSingleCoilPayload {
    unit_id: u8,
    address: u16,
    value: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildWriteSingleRegisterPayload {
    unit_id: u8,
    address: u16,
    value: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildWriteMultipleCoilsPayload {
    unit_id: u8,
    address: u16,
    values: Vec<bool>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildWriteMultipleRegistersPayload {
    unit_id: u8,
    address: u16,
    values: Vec<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ParseWriteResponsePayload {
    response: Vec<u8>,
    unit_id: u8,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenConnectionPayload {
    connection_id: String,
    host: String,
    port: u16,
    unit_id: u8,
    #[serde(default = "default_framing")]
    framing: String,
}

fn default_framing() -> String {
    "standard".to_string()
}

fn parse_framing(s: &str) -> Result<crate::session::TcpFraming, CoreError> {
    match s.to_lowercase().as_str() {
        "standard" | "tcp" => Ok(crate::session::TcpFraming::Standard),
        "rtu-over-tcp" | "rtuovertcp" => Ok(crate::session::TcpFraming::RtuOverTcp),
        "ascii-over-tcp" | "asciiovertcp" => Ok(crate::session::TcpFraming::AsciiOverTcp),
        _ => Err(CoreError::InvalidSerialConfig {
            field: "framing",
            message: format!("不支持的 framing 模式: {s}"),
        }),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CloseConnectionPayload {
    connection_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpReadPayload {
    connection_id: String,
    start_address: u16,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpWriteSinglePayload {
    connection_id: String,
    address: u16,
    value: Value,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpWriteMultiplePayload {
    connection_id: String,
    address: u16,
    values: Vec<Value>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpMaskWriteRegisterPayload {
    connection_id: String,
    address: u16,
    and_mask: u16,
    or_mask: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpReadWriteMultiplePayload {
    connection_id: String,
    read_address: u16,
    read_quantity: u16,
    write_address: u16,
    write_values: Vec<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpReadDeviceIdPayload {
    connection_id: String,
    #[serde(default = "default_read_dev_id_code")]
    read_device_id_code: u8,
    #[serde(default)]
    object_id: u8,
}

fn default_read_dev_id_code() -> u8 {
    1
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpDiagnosticsPayload {
    connection_id: String,
    sub_function: u8,
    #[serde(default = "default_diag_data")]
    data: u16,
}

fn default_diag_data() -> u16 {
    0
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DecodeValuesPayload {
    registers: Vec<u16>,
    data_type: String,
    #[serde(default)]
    offset: Option<usize>,
    #[serde(default)]
    count: Option<usize>,
    #[serde(default)]
    scale: Option<f64>,
    #[serde(default)]
    offset_value: Option<f64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ScanStationIdsPayload {
    connection_id: String,
    #[serde(default = "default_scan_start")]
    range_start: u8,
    #[serde(default = "default_scan_end")]
    range_end: u8,
    #[serde(default = "default_scan_timeout")]
    timeout_ms: u32,
}

fn default_scan_start() -> u8 {
    1
}
fn default_scan_end() -> u8 {
    247
}
fn default_scan_timeout() -> u32 {
    500
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StartTcpSlavePayload {
    slave_id: String,
    port: u16,
    #[serde(default)]
    allowed_station_ids: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveIdPayload {
    slave_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveSetValuePayload {
    slave_id: String,
    area: String,
    address: u16,
    values: Vec<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveSetCoilPayload {
    slave_id: String,
    area: String,
    address: u16,
    values: Vec<bool>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveClearPayload {
    slave_id: String,
    #[serde(default)]
    area: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveGetMemoryPayload {
    slave_id: String,
    area: String,
    address: u16,
    count: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveHandleSerialBytesPayload {
    slave_id: String,
    bytes: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SerialSlaveSetValuePayload {
    slave_id: String,
    area: String,
    address: u16,
    values: Vec<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SerialSlaveGetMemoryPayload {
    slave_id: String,
    area: String,
    address: u16,
    count: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ChecksumPayload {
    bytes: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ParseFrameOnlinePayload {
    bytes: Vec<u8>,
    #[serde(default = "default_transport")]
    transport: String,
}

fn default_transport() -> String {
    "rtu".to_string()
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StartPollStreamPayload {
    stream_id: String,
    connection_id: String,
    fc: u8,
    start_address: u16,
    quantity: u16,
    #[serde(default = "default_interval")]
    interval_ms: u32,
}

fn default_interval() -> u32 {
    1000
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StopPollStreamPayload {
    stream_id: String,
}

// =============================================================================
// 核心分发
// =============================================================================

pub fn handle_line(session: &mut Session, line: &str) -> CommandOutcome {
    let raw: Value = match serde_json::from_str(line) {
        Ok(value) => value,
        Err(_) => return failure(None, CoreError::InvalidJson),
    };
    let request_id = raw
        .get("requestId")
        .and_then(Value::as_str)
        .map(str::to_owned);
    let request: RequestEnvelope = match serde_json::from_value(raw) {
        Ok(request) => request,
        Err(_) => return failure(request_id, CoreError::InvalidEnvelope),
    };

    if request.request_id.is_empty()
        || request.request_id.len() > 128
        || request.request_id.chars().any(char::is_control)
    {
        return failure(Some(request.request_id), CoreError::InvalidEnvelope);
    }
    if request.protocol_version != PROTOCOL_VERSION {
        return failure(
            Some(request.request_id),
            CoreError::UnsupportedProtocolVersion {
                received: request.protocol_version,
                supported: PROTOCOL_VERSION,
            },
        );
    }

    dispatch(session, &request.request_id, &request.command, request.payload)
}

fn dispatch(
    session: &mut Session,
    request_id: &str,
    command: &str,
    payload: Value,
) -> CommandOutcome {
    match command {
        "hello" => {
            // 版本协商:v1 客户端发 protocolVersion=1;v2 客户端可发 clientVersion
            let client_version = payload.get("clientVersion").and_then(Value::as_u64).map(|v| v as u16);
            success(
                request_id.to_string(),
                json!({
                    "service": "nexus-rust-core",
                    "serviceVersion": env!("CARGO_PKG_VERSION"),
                    "protocolVersion": PROTOCOL_VERSION,
                    "supportedVersions": [1, 2],
                    "clientVersion": client_version,
                    "features": ["streaming"],
                    "capabilities": all_capabilities()
                }),
                false,
            )
        }
        "validate_serial_config" => handle_validate_serial_config(request_id, payload),
        // === 串口路径:RTU build/parse(向后兼容) ===
        "build_read_holding_registers" => {
            handle_build_read_registers_rtu(request_id, payload, 0x03)
        }
        "build_read_input_registers" => {
            handle_build_read_registers_rtu(request_id, payload, 0x04)
        }
        "parse_read_holding_registers" => {
            handle_parse_read_registers_rtu(request_id, payload, 0x03)
        }
        "parse_read_input_registers" => {
            handle_parse_read_registers_rtu(request_id, payload, 0x04)
        }
        // === 串口路径:RTU build/parse — 新增读位(FC01/FC02) ===
        "build_read_coils" => handle_build_read_bits_rtu(request_id, payload, 0x01),
        "build_read_discrete_inputs" => handle_build_read_bits_rtu(request_id, payload, 0x02),
        "parse_read_coils" => handle_parse_read_bits_rtu(request_id, payload, 0x01),
        "parse_read_discrete_inputs" => handle_parse_read_bits_rtu(request_id, payload, 0x02),
        // === 串口路径:RTU build/parse — 写操作(FC05/06/15/16) ===
        "build_write_single_coil" => handle_build_write_single_coil_rtu(request_id, payload),
        "build_write_single_register" => {
            handle_build_write_single_register_rtu(request_id, payload)
        }
        "build_write_multiple_coils" => {
            handle_build_write_multiple_coils_rtu(request_id, payload)
        }
        "build_write_multiple_registers" => {
            handle_build_write_multiple_registers_rtu(request_id, payload)
        }
        "parse_write_single_coil" => handle_parse_write_single_coil_rtu(request_id, payload),
        "parse_write_single_register" => {
            handle_parse_write_single_register_rtu(request_id, payload)
        }
        "parse_write_multiple_coils" => handle_parse_write_multiple_coils_rtu(request_id, payload),
        "parse_write_multiple_registers" => {
            handle_parse_write_multiple_registers_rtu(request_id, payload)
        }
        // === 串口路径:ASCII build/parse(FC01-06,15,16) ===
        "build_ascii_read_holding_registers" => {
            handle_build_read_registers_ascii(request_id, payload, 0x03)
        }
        "parse_ascii_read_holding_registers" => {
            handle_parse_read_registers_ascii(request_id, payload, 0x03)
        }
        "build_ascii_read_input_registers" => {
            handle_build_read_registers_ascii(request_id, payload, 0x04)
        }
        "parse_ascii_read_input_registers" => {
            handle_parse_read_registers_ascii(request_id, payload, 0x04)
        }
        "build_ascii_read_coils" => handle_build_read_bits_ascii(request_id, payload, 0x01),
        "parse_ascii_read_coils" => handle_parse_read_bits_ascii(request_id, payload, 0x01),
        "build_ascii_read_discrete_inputs" => {
            handle_build_read_bits_ascii(request_id, payload, 0x02)
        }
        "parse_ascii_read_discrete_inputs" => {
            handle_parse_read_bits_ascii(request_id, payload, 0x02)
        }
        "build_ascii_write_single_coil" => {
            handle_build_write_single_coil_ascii(request_id, payload)
        }
        "parse_ascii_write_single_coil" => {
            handle_parse_write_single_coil_ascii(request_id, payload)
        }
        "build_ascii_write_single_register" => {
            handle_build_write_single_register_ascii(request_id, payload)
        }
        "parse_ascii_write_single_register" => {
            handle_parse_write_single_register_ascii(request_id, payload)
        }
        "build_ascii_write_multiple_coils" => {
            handle_build_write_multiple_coils_ascii(request_id, payload)
        }
        "parse_ascii_write_multiple_coils" => {
            handle_parse_write_multiple_coils_ascii(request_id, payload)
        }
        "build_ascii_write_multiple_registers" => {
            handle_build_write_multiple_registers_ascii(request_id, payload)
        }
        "parse_ascii_write_multiple_registers" => {
            handle_parse_write_multiple_registers_ascii(request_id, payload)
        }
        // === TCP/UDP 路径:连接管理 ===
        "open_tcp_connection" => handle_open_tcp(session, request_id, payload),
        "open_udp_connection" => handle_open_udp(session, request_id, payload),
        "close_connection" => handle_close_connection(session, request_id, payload),
        // === TCP/UDP 路径:端到端读写 ===
        "tcp_read_coils" => handle_tcp_read_bits(session, request_id, payload, 0x01),
        "tcp_read_discrete_inputs" => handle_tcp_read_bits(session, request_id, payload, 0x02),
        "tcp_read_holding_registers" => {
            handle_tcp_read_registers(session, request_id, payload, 0x03)
        }
        "tcp_read_input_registers" => {
            handle_tcp_read_registers(session, request_id, payload, 0x04)
        }
        "tcp_write_single_coil" => {
            handle_tcp_write_single_coil(session, request_id, payload)
        }
        "tcp_write_single_register" => {
            handle_tcp_write_single_register(session, request_id, payload)
        }
        "tcp_write_multiple_coils" => {
            handle_tcp_write_multiple_coils(session, request_id, payload)
        }
        "tcp_write_multiple_registers" => {
            handle_tcp_write_multiple_registers(session, request_id, payload)
        }
        // === 高级 FC 端到端(FC22/23/43/08)===
        "tcp_mask_write_register" => {
            handle_tcp_mask_write_register(session, request_id, payload)
        }
        "tcp_read_write_multiple" => {
            handle_tcp_read_write_multiple(session, request_id, payload)
        }
        "tcp_read_device_id" => handle_tcp_read_device_id(session, request_id, payload),
        "tcp_diagnostics" => handle_tcp_diagnostics(session, request_id, payload),
        // === 诊断类 FC(FC07/11/12/17)— 仅串行线,但通过 TCP 也能发 ===
        "tcp_read_exception_status" => {
            handle_tcp_simple_fc(session, request_id, payload, 0x07)
        }
        "tcp_get_comm_event_counter" => {
            handle_tcp_simple_fc(session, request_id, payload, 0x0B)
        }
        "tcp_get_comm_event_log" => {
            handle_tcp_simple_fc(session, request_id, payload, 0x0C)
        }
        "tcp_report_slave_id" => {
            handle_tcp_simple_fc(session, request_id, payload, 0x11)
        }
        // === UDP 端到端读写(framing 由 open_udp_connection 决定)===
        "udp_read_coils" => handle_udp_read_bits(session, request_id, payload, 0x01),
        "udp_read_discrete_inputs" => handle_udp_read_bits(session, request_id, payload, 0x02),
        "udp_read_holding_registers" => {
            handle_udp_read_registers(session, request_id, payload, 0x03)
        }
        "udp_read_input_registers" => {
            handle_udp_read_registers(session, request_id, payload, 0x04)
        }
        "udp_write_single_coil" => {
            handle_udp_write_single_coil(session, request_id, payload)
        }
        "udp_write_single_register" => {
            handle_udp_write_single_register(session, request_id, payload)
        }
        "udp_write_multiple_coils" => {
            handle_udp_write_multiple_coils(session, request_id, payload)
        }
        "udp_write_multiple_registers" => {
            handle_udp_write_multiple_registers(session, request_id, payload)
        }
        // === 值解码(纯计算,对标 28 种显示格式)===
        "decode_values" => handle_decode_values(request_id, payload),
        // === 扫描(仅 TCP/UDP 连接,串口由 Electron 驱动)===
        "scan_station_ids" => handle_scan_station_ids(session, request_id, payload),
        // === 三菱 MC 协议 ===
        "mc_parse_address" => handle_mc_parse_address(request_id, payload),
        "mc_build_read" => handle_mc_build_read(request_id, payload),
        "mc_build_write" => handle_mc_build_write(request_id, payload),
        "mc_parse_response" => handle_mc_parse_response(request_id, payload),
        "open_mc_tcp_connection" => handle_open_mc_tcp(session, request_id, payload),
        "mc_tcp_read" => handle_mc_tcp_read(session, request_id, payload),
        "mc_tcp_write" => handle_mc_tcp_write(session, request_id, payload),
        "start_mc_tcp_slave" => handle_start_mc_tcp_slave(session, request_id, payload),
        "stop_mc_slave" => handle_stop_mc_slave(session, request_id, payload),
        "mc_slave_set" => handle_mc_slave_set(session, request_id, payload),
        // 三菱 MC 进阶(M2)
        "mc_tcp_read_random" => handle_mc_tcp_read_random(session, request_id, payload),
        "mc_tcp_write_random" => handle_mc_tcp_write_random(session, request_id, payload),
        "mc_tcp_read_blocks" => handle_mc_tcp_read_blocks(session, request_id, payload),
        "mc_remote_run" => handle_mc_remote(session, request_id, 0x1002, payload),
        "mc_remote_stop" => handle_mc_remote(session, request_id, 0x1006, payload),
        "mc_remote_reset" => handle_mc_remote(session, request_id, 0x1001, payload),
        "mc_remote_pause" => handle_mc_remote(session, request_id, 0x1003, payload),
        "mc_read_clock" => handle_mc_read_clock(session, request_id, payload),
        "mc_echo_test" => handle_mc_echo_test(session, request_id, payload),
        "mc_read_cpu_type" => handle_mc_cpu_info(session, request_id, "type", payload),
        "mc_read_cpu_status" => handle_mc_cpu_info(session, request_id, "status", payload),
        "mc_build_ascii_read" => handle_mc_build_ascii_read(request_id, payload),
        "open_mc_ascii_connection" => handle_open_mc_ascii(session, request_id, payload),
        "mc_ascii_read" => handle_mc_ascii_read(session, request_id, payload),
        "mc_ascii_write" => handle_mc_ascii_write(session, request_id, payload),
        // === 三菱 MC 串口 C24(3C/4C 离线组帧,§3.1)===
        "mc_serial_build_3c" => handle_mc_serial_build_3c(request_id, payload),
        "mc_serial_parse_3c" => handle_mc_serial_parse_3c(request_id, payload),
        "mc_c24_read" => handle_mc_c24_read(request_id, payload),
        "mc_c24_parse_read" => handle_mc_c24_parse_read(request_id, payload),
        // === 三菱 A-1E / SLMP-1E 帧(离线组帧+解析,§3.4)===
        "mc_1e_build_read" => handle_mc_1e_build_read(request_id, payload),
        "mc_1e_build_write" => handle_mc_1e_build_write(request_id, payload),
        "mc_1e_parse" => handle_mc_1e_parse(request_id, payload),
        "open_mc_udp_connection" => handle_open_mc_udp(session, request_id, payload),
        "mc_udp_read" => handle_mc_udp_read(session, request_id, payload),
        "mc_udp_write" => handle_mc_udp_write(session, request_id, payload),
        "open_mc_1e_tcp" => handle_open_mc_1e(session, request_id, payload),
        "mc_1e_read" => handle_mc_1e_read(session, request_id, payload),
        "mc_1e_write" => handle_mc_1e_write(session, request_id, payload),
        // === 三菱 FX 串口协议(Computer Link / 编程口)===
        "brand_parse_address" => handle_brand_parse_address(request_id, payload),
        "fins_parse_address" => handle_fins_parse_address(request_id, payload),
        "open_fins_tcp" => handle_open_fins_tcp(session, request_id, payload),
        "open_fins_udp" => handle_open_fins_udp(session, request_id, payload),
        "fins_read" => handle_fins_read(session, request_id, payload),
        "fins_write" => handle_fins_write(session, request_id, payload),
        "start_fins_slave" => handle_start_fins_slave(session, request_id, payload),
        "stop_fins_slave" => handle_stop_fins_slave(session, request_id, payload),
        "fins_slave_set" => handle_fins_slave_set(session, request_id, payload),
        "fins_slave_get" => handle_fins_slave_get(session, request_id, payload),
        "s7_parse_address" => handle_s7_parse_address(request_id, payload),
        "open_s7_connection" => handle_open_s7_connection(session, request_id, payload),
        "close_s7_connection" => handle_close_connection(session, request_id, payload),
        "s7_read" => handle_s7_read(session, request_id, payload),
        "s7_write" => handle_s7_write(session, request_id, payload),
        "start_s7_slave" => handle_start_s7_slave(session, request_id, payload),
        "stop_s7_slave" => handle_stop_s7_slave(session, request_id, payload),
        "s7_slave_set" => handle_s7_slave_set(session, request_id, payload),
        "s7_slave_get" => handle_s7_slave_get(session, request_id, payload),
        "s7_cpu_control" => handle_s7_cpu_control(session, request_id, payload),
        "s7_read_status" => handle_s7_read_status(session, request_id, payload),
        "s7_password" => handle_s7_password(session, request_id, payload),
        "open_fw_tcp" => handle_open_fw_tcp(session, request_id, payload),
        "fw_read" => handle_fw_read(session, request_id, payload),
        "fw_write" => handle_fw_write(session, request_id, payload),
        "start_fw_slave" => handle_start_fw_slave(session, request_id, payload),
        "stop_fw_slave" => handle_stop_fw_slave(session, request_id, payload),
        "open_ppi_tcp" => handle_open_ppi_tcp(session, request_id, payload),
        "ppi_read" => handle_ppi_read(session, request_id, payload),
        "ppi_write" => handle_ppi_write(session, request_id, payload),
        "start_ppi_slave" => handle_start_ppi_slave(session, request_id, payload),
        "stop_ppi_slave" => handle_stop_ppi_slave(session, request_id, payload),
        "hostlink_build_fins" => handle_hostlink_build_fins(request_id, payload),
        "hostlink_parse_fins" => handle_hostlink_parse_fins(request_id, payload),
        "hostlink_build_cmode_read" => handle_hostlink_build_cmode_read(request_id, payload),
        "hostlink_parse_cmode_read" => handle_hostlink_parse_cmode_read(request_id, payload),
        "uss_build_request" => handle_uss_build_request(request_id, payload),
        "uss_parse_response" => handle_uss_parse_response(request_id, payload),
        "rk512_build_read" => handle_rk512_build_read(request_id, payload),
        "rk512_build_write" => handle_rk512_build_write(request_id, payload),
        "rk512_parse_response" => handle_rk512_parse_response(request_id, payload),
        "fx_links_build" => handle_fx_links_build(request_id, payload),
        "fx_links_parse" => handle_fx_links_parse(request_id, payload),
        "fx_links_read" => handle_fx_links_read(request_id, payload),
        "fx_links_write_bits" => handle_fx_links_write_bits(request_id, payload),
        "fx_links_write_words" => handle_fx_links_write_words(request_id, payload),
        "fx_prog_build_read" => handle_fx_prog_build_read(request_id, payload),
        "fx_prog_build_write" => handle_fx_prog_build_write(request_id, payload),
        "fx_prog_parse" => handle_fx_prog_parse(request_id, payload),
        // === 从站模拟 ===
        "start_tcp_slave" => handle_start_tcp_slave(session, request_id, payload),
        "stop_slave" => handle_stop_slave(session, request_id, payload),
        "slave_set_value" => handle_slave_set_value(session, request_id, payload),
        "slave_set_coil" => handle_slave_set_coil(session, request_id, payload),
        "slave_clear" => handle_slave_clear(session, request_id, payload),
        "slave_get_memory" => handle_slave_get_memory(session, request_id, payload),
        // === 串口从站模拟(Electron 持 COM 句柄)===
        "start_serial_slave" => handle_start_serial_slave(session, request_id, payload),
        "stop_serial_slave" => handle_stop_serial_slave(session, request_id, payload),
        "slave_handle_serial_bytes" => {
            handle_slave_handle_serial_bytes(session, request_id, payload)
        }
        "serial_slave_set_value" => handle_serial_slave_set_value(session, request_id, payload),
        "serial_slave_get_memory" => handle_serial_slave_get_memory(session, request_id, payload),
        // === 校验 + 在线解析(串口调试) ===
        "compute_crc16" => handle_compute_crc16(request_id, payload),
        "compute_lrc" => handle_compute_lrc(request_id, payload),
        "parse_frame_online" => handle_parse_frame_online(request_id, payload),
        // === 离线解析器(对标 ModbusPacketParser)===
        "parse_frame_offline" => handle_parse_frame_offline(request_id, payload),
        // === 流式轮询(v2 协议)===
        "start_poll_stream" => handle_start_poll_stream(session, request_id, payload),
        "stop_poll_stream" => handle_stop_poll_stream(session, request_id, payload),
        "shutdown" => success(request_id.to_string(), json!({ "accepted": true }), true),
        unknown => failure(
            Some(request_id.to_string()),
            CoreError::UnknownCommand(unknown.to_owned()),
        ),
    }
}

fn all_capabilities() -> Vec<&'static str> {
    vec![
        "hello",
        "validate_serial_config",
        "build_read_holding_registers",
        "parse_read_holding_registers",
        "build_read_input_registers",
        "parse_read_input_registers",
        "build_read_coils",
        "parse_read_coils",
        "build_read_discrete_inputs",
        "parse_read_discrete_inputs",
        "build_write_single_coil",
        "parse_write_single_coil",
        "build_write_single_register",
        "parse_write_single_register",
        "build_write_multiple_coils",
        "parse_write_multiple_coils",
        "build_write_multiple_registers",
        "parse_write_multiple_registers",
        "build_ascii_read_holding_registers",
        "parse_ascii_read_holding_registers",
        "build_ascii_read_input_registers",
        "parse_ascii_read_input_registers",
        "build_ascii_read_coils",
        "parse_ascii_read_coils",
        "build_ascii_read_discrete_inputs",
        "parse_ascii_read_discrete_inputs",
        "build_ascii_write_single_coil",
        "parse_ascii_write_single_coil",
        "build_ascii_write_single_register",
        "parse_ascii_write_single_register",
        "build_ascii_write_multiple_coils",
        "parse_ascii_write_multiple_coils",
        "build_ascii_write_multiple_registers",
        "parse_ascii_write_multiple_registers",
        "open_tcp_connection",
        "open_udp_connection",
        "close_connection",
        "tcp_read_coils",
        "tcp_read_discrete_inputs",
        "tcp_read_holding_registers",
        "tcp_read_input_registers",
        "tcp_write_single_coil",
        "tcp_write_single_register",
        "tcp_write_multiple_coils",
        "tcp_write_multiple_registers",
        "tcp_mask_write_register",
        "tcp_read_write_multiple",
        "tcp_read_device_id",
        "tcp_diagnostics",
        "tcp_read_exception_status",
        "tcp_get_comm_event_counter",
        "tcp_get_comm_event_log",
        "tcp_report_slave_id",
        "udp_read_coils",
        "udp_read_discrete_inputs",
        "udp_read_holding_registers",
        "udp_read_input_registers",
        "udp_write_single_coil",
        "udp_write_single_register",
        "udp_write_multiple_coils",
        "udp_write_multiple_registers",
        "decode_values",
        "scan_station_ids",
        // 三菱 MC 协议
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
        // 三菱 MC 进阶(M2)
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
        // 三菱 MC 串口 C24(3C/4C 离线组帧)
        "mc_serial_build_3c",
        "mc_serial_parse_3c",
        "mc_c24_read",
        "mc_c24_parse_read",
        // 三菱 A-1E / SLMP-1E 帧
        "mc_1e_build_read",
        "mc_1e_build_write",
        "mc_1e_parse",
        "open_mc_udp_connection",
        "mc_udp_read",
        "mc_udp_write",
        "open_mc_1e_tcp",
        "mc_1e_read",
        "mc_1e_write",
        // 三菱 FX 串口协议(Computer Link / 编程口)
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
        "close_s7_connection",
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
        "hostlink_build_fins",
        "hostlink_parse_fins",
        "hostlink_build_cmode_read",
        "hostlink_parse_cmode_read",
        "uss_build_request",
        "uss_parse_response",
        "rk512_build_read",
        "rk512_build_write",
        "rk512_parse_response",
        "fx_links_build",
        "fx_links_parse",
        "fx_links_read",
        "fx_links_write_bits",
        "fx_links_write_words",
        "fx_prog_build_read",
        "fx_prog_build_write",
        "fx_prog_parse",
        "start_tcp_slave",
        "stop_slave",
        "slave_set_value",
        "slave_set_coil",
        "slave_clear",
        "slave_get_memory",
        "start_serial_slave",
        "stop_serial_slave",
        "slave_handle_serial_bytes",
        "serial_slave_set_value",
        "serial_slave_get_memory",
        "compute_crc16",
        "compute_lrc",
        "parse_frame_online",
        "parse_frame_offline",
        "start_poll_stream",
        "stop_poll_stream",
        "shutdown",
    ]
}

// =============================================================================
// 命令处理函数
// =============================================================================

fn handle_validate_serial_config(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ValidateSerialConfigPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let config: SerialConfig = match serde_json::from_value(payload.config) {
        Ok(config) => config,
        Err(error) => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "config",
                    message: error.to_string(),
                },
            );
        }
    };
    match config.validate_and_normalize() {
        Ok(config) => success(
            request_id.to_string(),
            serde_json::to_value(config).expect("SerialConfig must serialize"),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

// --- RTU 读寄存器(向后兼容) ---

fn handle_build_read_registers_rtu(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: BuildReadRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x03 {
        build_read_holding_registers_request
    } else {
        build_read_input_registers_request
    };
    match builder(payload.unit_id, payload.start_address, payload.quantity) {
        Ok(built) => success(
            request_id.to_string(),
            json!({
                "adu": built.adu,
                "requestHex": format_hex(&built.adu),
                "expectedResponseLength": built.expected_response_len,
                "exceptionResponseLength": built.exception_response_len,
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error.into()),
    }
}

fn handle_parse_read_registers_rtu(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: ParseReadRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let parser = if fc == 0x03 {
        parse_read_holding_registers_response
    } else {
        parse_read_input_registers_response
    };
    match parser(&payload.response, payload.unit_id, payload.quantity) {
        Ok(parsed) => success(
            request_id.to_string(),
            format_read_registers_result(&parsed),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error.into()),
    }
}

// --- RTU 读位(FC01/FC02,新增) ---

fn handle_build_read_bits_rtu(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: BuildReadBitsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if payload.unit_id == 0 {
        return failure(
            Some(request_id.to_string()),
            RtuError::BroadcastReadNotAllowed.into(),
        );
    }
    let builder = if fc == 0x01 {
        pdu::build_read_coils_pdu
    } else {
        pdu::build_read_discrete_inputs_pdu
    };
    let pdu_bytes = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    // pdu_bytes 已含 FC 首字节,RtuFrame::request 会再写一次 FC——不剥离会产出双 FC 帧
    //(从站把第二字节当地址,读错地址 256 倍偏移)。与 build_and_encode_rtu 的剥离逻辑一致。
    let data = if pdu_bytes.first() == Some(&fc) { &pdu_bytes[1..] } else { &pdu_bytes[..] };
    let adu = match modbus_rtu::RtuFrame::request(payload.unit_id, fc, data) {
        Ok(frame) => frame.encode(),
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let expected_response_len = 5 + usize::from(payload.quantity.div_ceil(8));
    success(
        request_id.to_string(),
        json!({
            "adu": adu,
            "requestHex": format_hex(&adu),
            "expectedResponseLength": expected_response_len,
            "exceptionResponseLength": 5,
        }),
        false,
    )
}

fn handle_parse_read_bits_rtu(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: ParseReadBitsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame = match modbus_rtu::RtuFrame::decode(&payload.response, modbus_rtu::RtuFrameRole::Response)
    {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    if frame.unit_id() != payload.unit_id {
        return failure(
            Some(request_id.to_string()),
            RtuError::UnitIdMismatch {
                expected: payload.unit_id,
                received: frame.unit_id(),
            }
            .into(),
        );
    }
    let base_fc = frame.function_code() & 0x7F;
    if base_fc != fc {
        return failure(
            Some(request_id.to_string()),
            RtuError::FunctionCodeMismatch {
                expected: fc,
                received: frame.function_code(),
            }
            .into(),
        );
    }
    if frame.is_exception() {
        return success(
            request_id.to_string(),
            json!({
                "status": "exception",
                "exceptionCode": frame.exception_code(),
                "exceptionName": frame.exception_code().map(modbus_exception_name),
                "coils": [],
            }),
            false,
        );
    }
    let parser = if fc == 0x01 {
        pdu::parse_read_coils_response
    } else {
        pdu::parse_read_discrete_inputs_response
    };
    match parser(frame.data(), payload.quantity) {
        Ok(bits) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "exceptionCode": null,
                "exceptionName": null,
                "coils": bits,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

// --- RTU 写操作(FC05/06/15/16,新增) ---

fn handle_build_write_single_coil_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteSingleCoilPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_single_coil_pdu(payload.address, payload.value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_and_encode_rtu(request_id, payload.unit_id, 0x05, &pdu_bytes)
}

fn handle_build_write_single_register_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteSingleRegisterPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_single_register_pdu(payload.address, payload.value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_and_encode_rtu(request_id, payload.unit_id, 0x06, &pdu_bytes)
}

fn handle_build_write_multiple_coils_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteMultipleCoilsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_multiple_coils_pdu(payload.address, &payload.values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_and_encode_rtu(request_id, payload.unit_id, 0x0F, &pdu_bytes)
}

fn handle_build_write_multiple_registers_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteMultipleRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes =
        match pdu::build_write_multiple_registers_pdu(payload.address, &payload.values) {
            Ok(p) => p,
            Err(e) => return failure(Some(request_id.to_string()), e.into()),
        };
    build_and_encode_rtu(request_id, payload.unit_id, 0x10, &pdu_bytes)
}

fn build_and_encode_rtu(
    request_id: &str,
    unit_id: u8,
    fc: u8,
    pdu_bytes: &[u8],
) -> CommandOutcome {
    // pdu_bytes 的第一个字节是 FC,但 RtuFrame 会自己加 FC,所以 data 要去掉首字节
    let data = if pdu_bytes.first() == Some(&fc) {
        &pdu_bytes[1..]
    } else {
        pdu_bytes
    };
    let frame = match modbus_rtu::RtuFrame::request(unit_id, fc, data) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let adu = frame.encode();
    let expect_response = unit_id != 0;
    success(
        request_id.to_string(),
        json!({
            "adu": adu,
            "requestHex": format_hex(&adu),
            "expectedResponseLength": if expect_response { adu.len() } else { 0 },
            "exceptionResponseLength": if expect_response { 5 } else { 0 },
            "expectResponse": expect_response,
        }),
        false,
    )
}

fn handle_parse_write_single_coil_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match decode_rtu_response(&payload.response, payload.unit_id, 0x05) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = unit_id;
    let (addr, value) = match pdu::parse_write_single_coil_response(&pdu_data) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "address": addr,
            "value": value,
            "exceptionCode": null,
        }),
        false,
    )
}

fn handle_parse_write_single_register_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match decode_rtu_response(&payload.response, payload.unit_id, 0x06) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = unit_id;
    let (addr, value) = match pdu::parse_write_single_register_response(&pdu_data) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "address": addr,
            "value": value,
            "exceptionCode": null,
        }),
        false,
    )
}

fn handle_parse_write_multiple_coils_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match decode_rtu_response(&payload.response, payload.unit_id, 0x0F) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = unit_id;
    let (addr, qty) = match pdu::parse_write_multiple_coils_response(&pdu_data) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "address": addr,
            "quantity": qty,
            "exceptionCode": null,
        }),
        false,
    )
}

fn handle_parse_write_multiple_registers_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match decode_rtu_response(&payload.response, payload.unit_id, 0x10) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = unit_id;
    let (addr, qty) = match pdu::parse_write_multiple_registers_response(&pdu_data) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "address": addr,
            "quantity": qty,
            "exceptionCode": null,
        }),
        false,
    )
}

/// 解码 RTU 响应帧,返回 (unit_id, pdu_data)。处理异常码。
fn decode_rtu_response(
    response: &[u8],
    expected_unit_id: u8,
    expected_fc: u8,
) -> Result<(u8, Vec<u8>), CoreError> {
    let frame = modbus_rtu::RtuFrame::decode(response, modbus_rtu::RtuFrameRole::Response)?;
    if frame.unit_id() != expected_unit_id {
        return Err(RtuError::UnitIdMismatch {
            expected: expected_unit_id,
            received: frame.unit_id(),
        }
        .into());
    }
    let base_fc = frame.function_code() & 0x7F;
    if base_fc != expected_fc {
        return Err(RtuError::FunctionCodeMismatch {
            expected: expected_fc,
            received: frame.function_code(),
        }
        .into());
    }
    let mut pdu = Vec::with_capacity(frame.data().len() + 1);
    pdu.push(frame.function_code());
    pdu.extend_from_slice(frame.data());
    Ok((frame.unit_id(), pdu))
}

// --- ASCII 读寄存器 ---

fn handle_build_read_registers_ascii(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: BuildReadRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_builder = if fc == 0x03 {
        pdu::build_read_holding_registers_pdu
    } else {
        pdu::build_read_input_registers_pdu
    };
    let pdu_bytes = match pdu_builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    if payload.unit_id == 0 {
        return failure(
            Some(request_id.to_string()),
            RtuError::BroadcastReadNotAllowed.into(),
        );
    }
    let frame = modbus_ascii::build_ascii_frame(payload.unit_id, &pdu_bytes);
    success(
        request_id.to_string(),
        json!({
            "adu": frame,
            "requestHex": format_hex(&frame),
            "expectedResponseLength": 0,
            "exceptionResponseLength": 0,
        }),
        false,
    )
}

fn handle_parse_read_registers_ascii(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: ParseReadRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match modbus_ascii::parse_ascii_frame(&payload.response) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    if unit_id != payload.unit_id {
        return failure(
            Some(request_id.to_string()),
            RtuError::UnitIdMismatch {
                expected: payload.unit_id,
                received: unit_id,
            }
            .into(),
        );
    }
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, fc) {
        return success(
            request_id.to_string(),
            json!({
                "status": "exception",
                "exceptionCode": exc,
                "exceptionName": modbus_exception_name(exc),
                "registers": [],
            }),
            false,
        );
    }
    let parser = if fc == 0x03 {
        pdu::parse_read_holding_registers_response
    } else {
        pdu::parse_read_input_registers_response
    };
    match parser(&pdu_data, payload.quantity) {
        Ok(registers) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "exceptionCode": null,
                "exceptionName": null,
                "registers": registers,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

// --- ASCII 读位(FC01/FC02) ---

fn handle_build_read_bits_ascii(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: BuildReadBitsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if payload.unit_id == 0 {
        return failure(
            Some(request_id.to_string()),
            RtuError::BroadcastReadNotAllowed.into(),
        );
    }
    let builder = if fc == 0x01 {
        pdu::build_read_coils_pdu
    } else {
        pdu::build_read_discrete_inputs_pdu
    };
    let pdu_bytes = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_parse_read_bits_ascii(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: ParseReadBitsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, fc) {
        return success(
            request_id.to_string(),
            json!({
                "status": "exception",
                "exceptionCode": exc,
                "exceptionName": modbus_exception_name(exc),
                "coils": [],
            }),
            false,
        );
    }
    let parser = if fc == 0x01 {
        pdu::parse_read_coils_response
    } else {
        pdu::parse_read_discrete_inputs_response
    };
    match parser(&pdu_data, payload.quantity) {
        Ok(coils) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "exceptionCode": null,
                "exceptionName": null,
                "coils": coils,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

// --- ASCII 写操作(FC05/06/0F/10) ---

fn handle_build_write_single_coil_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteSingleCoilPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_single_coil_pdu(payload.address, payload.value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_build_write_single_register_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteSingleRegisterPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_single_register_pdu(payload.address, payload.value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_build_write_multiple_coils_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteMultipleCoilsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_multiple_coils_pdu(payload.address, &payload.values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_build_write_multiple_registers_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteMultipleRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes =
        match pdu::build_write_multiple_registers_pdu(payload.address, &payload.values) {
            Ok(p) => p,
            Err(e) => return failure(Some(request_id.to_string()), e.into()),
        };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_parse_write_single_coil_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, 0x05) {
        return ascii_exception_outcome(request_id, exc);
    }
    match pdu::parse_write_single_coil_response(&pdu_data) {
        Ok((addr, value)) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "address": addr,
                "value": value,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_parse_write_single_register_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, 0x06) {
        return ascii_exception_outcome(request_id, exc);
    }
    match pdu::parse_write_single_register_response(&pdu_data) {
        Ok((addr, value)) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "address": addr,
                "value": value,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_parse_write_multiple_coils_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, 0x0F) {
        return ascii_exception_outcome(request_id, exc);
    }
    match pdu::parse_write_multiple_coils_response(&pdu_data) {
        Ok((addr, qty)) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "address": addr,
                "quantity": qty,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_parse_write_multiple_registers_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, 0x10) {
        return ascii_exception_outcome(request_id, exc);
    }
    match pdu::parse_write_multiple_registers_response(&pdu_data) {
        Ok((addr, qty)) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "address": addr,
                "quantity": qty,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

// --- ASCII 共用辅助 ---

/// 用 ASCII 帧包装 PDU(unit_id + pdu_bytes),返回标准 ADU 结果。
fn build_ascii_adu(request_id: &str, unit_id: u8, pdu_bytes: &[u8]) -> CommandOutcome {
    let frame = modbus_ascii::build_ascii_frame(unit_id, pdu_bytes);
    let expect_response = unit_id != 0;
    success(
        request_id.to_string(),
        json!({
            "adu": frame,
            "requestHex": format_hex(&frame),
            "expectedResponseLength": 0,
            "exceptionResponseLength": 0,
            "expectResponse": expect_response,
        }),
        false,
    )
}

/// 解码 ASCII 响应帧,校验站号,返回 PDU(含 FC 首字节)。
fn decode_ascii_response(response: &[u8], expected_unit_id: u8) -> Result<Vec<u8>, CoreError> {
    let (unit_id, pdu_data) = modbus_ascii::parse_ascii_frame(response)?;
    if unit_id != expected_unit_id {
        return Err(RtuError::UnitIdMismatch {
            expected: expected_unit_id,
            received: unit_id,
        }
        .into());
    }
    Ok(pdu_data)
}

/// 构造 ASCII 写操作的异常响应 outcome(写响应无数据载荷)。
fn ascii_exception_outcome(request_id: &str, exception_code: u8) -> CommandOutcome {
    success(
        request_id.to_string(),
        json!({
            "status": "exception",
            "exceptionCode": exception_code,
            "exceptionName": modbus_exception_name(exception_code),
        }),
        false,
    )
}

// --- TCP/UDP 连接管理 ---

fn handle_open_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let framing = match parse_framing(&payload.framing) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match session.open_tcp(
        &payload.connection_id,
        &payload.host,
        payload.port,
        payload.unit_id,
        framing,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connected": true, "connectionId": payload.connection_id, "framing": payload.framing }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_udp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let framing = match parse_framing(&payload.framing) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match session.open_udp(
        &payload.connection_id,
        &payload.host,
        payload.port,
        payload.unit_id,
        framing,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connected": true, "connectionId": payload.connection_id, "framing": payload.framing }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_close_connection(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: CloseConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.close_connection(&payload.connection_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "closed": true, "connectionId": payload.connection_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

// --- TCP 端到端读写 ---

fn handle_tcp_read_bits(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let payload: TcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x01 {
        pdu::build_read_coils_pdu
    } else {
        pdu::build_read_discrete_inputs_pdu
    };
    let request_pdu = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    // 检查异常
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    let parser = if fc == 0x01 {
        pdu::parse_read_coils_response
    } else {
        pdu::parse_read_discrete_inputs_response
    };
    match parser(&response_pdu, payload.quantity) {
        Ok(bits) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "coils": bits,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_read_registers(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let payload: TcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x03 {
        pdu::build_read_holding_registers_pdu
    } else {
        pdu::build_read_input_registers_pdu
    };
    let request_pdu = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    let parser = if fc == 0x03 {
        pdu::parse_read_holding_registers_response
    } else {
        pdu::parse_read_input_registers_response
    };
    match parser(&response_pdu, payload.quantity) {
        Ok(registers) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "registers": registers,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_write_single_coil(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteSinglePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let value = match payload.value.as_bool() {
        Some(v) => v,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "value",
                    message: "FC05 值必须是布尔".into(),
                },
            );
        }
    };
    let request_pdu = match pdu::build_write_single_coil_pdu(payload.address, value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x05) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "value": value }),
        false,
    )
}

fn handle_tcp_write_single_register(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteSinglePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let value = match payload.value.as_u64().and_then(|v| u16::try_from(v).ok()) {
        Some(v) => v,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "value",
                    message: "FC06 值必须是 0-65535".into(),
                },
            );
        }
    };
    let request_pdu = match pdu::build_write_single_register_pdu(payload.address, value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x06) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "value": value }),
        false,
    )
}

fn handle_tcp_write_multiple_coils(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let values: Vec<bool> = payload
        .values
        .iter()
        .map(|v| v.as_bool().unwrap_or(false))
        .collect();
    let request_pdu = match pdu::build_write_multiple_coils_pdu(payload.address, &values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x0F) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "quantity": values.len() }),
        false,
    )
}

fn handle_tcp_write_multiple_registers(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let values: Vec<u16> = payload
        .values
        .iter()
        .map(|v| v.as_u64().and_then(|n| u16::try_from(n).ok()).unwrap_or(0))
        .collect();
    let request_pdu = match pdu::build_write_multiple_registers_pdu(payload.address, &values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x10) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "quantity": values.len() }),
        false,
    )
}

// --- 高级 FC 端到端(FC22/23/43/08) ---

fn handle_tcp_mask_write_register(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpMaskWriteRegisterPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let request_pdu = match pdu::build_mask_write_register_pdu(
        payload.address,
        payload.and_mask,
        payload.or_mask,
    ) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x16) {
        return exc;
    }
    match pdu::parse_mask_write_register_response(&response_pdu) {
        Ok((addr, and_mask, or_mask)) => success(
            request_id.to_string(),
            json!({ "status": "ok", "address": addr, "andMask": and_mask, "orMask": or_mask }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_read_write_multiple(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpReadWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let request_pdu = match pdu::build_read_write_multiple_registers_pdu(
        payload.read_address,
        payload.read_quantity,
        payload.write_address,
        &payload.write_values,
    ) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x17) {
        return exc;
    }
    match pdu::parse_read_write_multiple_registers_response(&response_pdu, payload.read_quantity) {
        Ok(registers) => success(
            request_id.to_string(),
            json!({ "status": "ok", "registers": registers }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_read_device_id(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpReadDeviceIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };

    // FC43/14 续传循环:设备返回 moreFollows=0xFF 时,用 nextObjectId 继续请求,
    // 直到 moreFollows=0 或达到最大迭代次数(防死循环)。
    let mut all_responses: Vec<Vec<u8>> = Vec::new();
    let mut next_object_id = payload.object_id;
    const MAX_ITERATIONS: u8 = 32;

    for _ in 0..MAX_ITERATIONS {
        let request_pdu = pdu::build_read_device_id_pdu(payload.read_device_id_code, next_object_id);
        let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
            Ok(p) => p,
            Err(e) => return failure(Some(request_id.to_string()), e),
        };
        if let Some(exc) = check_pdu_exception(&response_pdu, 0x2B) {
            return exc;
        }

        // 检查 moreFollows 字节(响应 PDU 偏移 4)
        let more_follows = response_pdu.get(4).copied().unwrap_or(0);
        all_responses.push(response_pdu.clone());

        if more_follows == 0xFF {
            // 还有更多:用 nextObjectId 继续请求
            next_object_id = response_pdu.get(5).copied().unwrap_or(next_object_id);
        } else {
            break; // 数据已完整
        }
    }

    // 返回所有页的合并结果(前端可遍历 pages 数组逐页解析对象)
    let pages_json: Vec<Value> = all_responses
        .iter()
        .map(|resp| {
            json!({
                "rawResponse": resp,
                "functionCode": resp.first().copied().unwrap_or(0),
                "meiType": resp.get(1).copied(),
                "readDeviceIdCode": resp.get(2).copied(),
                "conformityLevel": resp.get(3).copied(),
                "moreFollows": resp.get(4).copied(),
                "nextObjectId": resp.get(5).copied(),
                "objectCount": resp.get(6).copied(),
            })
        })
        .collect();

    let first = all_responses.first();
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "pages": pages_json,
            "pageCount": all_responses.len(),
            "functionCode": first.and_then(|r| r.first().copied()).unwrap_or(0),
            "meiType": first.and_then(|r| r.get(1).copied()),
            "readDeviceIdCode": first.and_then(|r| r.get(2).copied()),
            "conformityLevel": first.and_then(|r| r.get(3).copied()),
            "moreFollows": 0, // 已完成续传,最终 moreFollows=0
        }),
        false,
    )
}

fn handle_tcp_diagnostics(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpDiagnosticsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let request_pdu = pdu::build_diagnostics_pdu(payload.sub_function, payload.data);
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x08) {
        return exc;
    }
    match pdu::parse_diagnostics_response(&response_pdu) {
        Ok((sub_function, data)) => success(
            request_id.to_string(),
            json!({ "status": "ok", "subFunction": sub_function, "data": data }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_simple_fc(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let p: OpenConnectionPayload = match serde_json::from_value(payload.clone()) {
        Ok(p) => p,
        Err(_) => {
            // FC07/11/12/17 可能只传 connectionId
            #[derive(Deserialize)]
            #[serde(rename_all = "camelCase", deny_unknown_fields)]
            struct ConnOnly { connection_id: String }
            match serde_json::from_value::<ConnOnly>(payload) {
                Ok(c) => OpenConnectionPayload {
                    connection_id: c.connection_id,
                    host: String::new(),
                    port: 0,
                    unit_id: 1,
                    framing: default_framing(),
                },
                Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
            }
        }
    };
    let request_pdu = match fc {
        0x07 => pdu::build_read_exception_status_pdu(),
        0x0B => pdu::build_get_comm_event_counter_pdu(),
        0x0C => pdu::build_get_comm_event_log_pdu(),
        0x11 => pdu::build_report_slave_id_pdu(),
        _ => return failure(Some(request_id.to_string()), CoreError::UnknownCommand(format!("unknown simple fc {fc:#04X}"))),
    };
    let response_pdu = match session.transact_tcp(&p.connection_id, &request_pdu) {
        Ok(resp) => resp,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    // 返回解析后的结构化数据
    let parsed = match fc {
        0x07 => match pdu::parse_read_exception_status_response(&response_pdu) {
            Ok(status) => json!({ "status": "ok", "exceptionStatus": status }),
            Err(_) => json!({ "status": "ok", "rawResponse": response_pdu }),
        },
        0x0B => match pdu::parse_get_comm_event_counter_response(&response_pdu) {
            Ok((status, count)) => json!({ "status": "ok", "commStatus": status, "eventCount": count }),
            Err(_) => json!({ "status": "ok", "rawResponse": response_pdu }),
        },
        0x0C => match pdu::parse_get_comm_event_log_response(&response_pdu) {
            Ok((status, event_count, msg_count, events)) => json!({
                "status": "ok", "commStatus": status, "eventCount": event_count,
                "messageCount": msg_count, "events": events
            }),
            Err(_) => json!({ "status": "ok", "rawResponse": response_pdu }),
        },
        0x11 => match pdu::parse_report_slave_id_response(&response_pdu) {
            Ok((slave_id, run)) => json!({
                "status": "ok",
                "slaveId": slave_id,
                "slaveIdString": String::from_utf8_lossy(&slave_id).to_string(),
                "runStatus": run,
                "runStatusName": if run == 0xFF { "ON" } else { "OFF" },
            }),
            Err(_) => json!({ "status": "ok", "rawResponse": response_pdu }),
        },
        _ => json!({ "rawResponse": response_pdu }),
    };
    success(request_id.to_string(), parsed, false)
}

// --- UDP 端到端读写(逻辑与 TCP 版本相同,只是用 transact_udp) ---

fn handle_udp_read_bits(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let payload: TcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x01 {
        pdu::build_read_coils_pdu
    } else {
        pdu::build_read_discrete_inputs_pdu
    };
    let request_pdu = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    let parser = if fc == 0x01 {
        pdu::parse_read_coils_response
    } else {
        pdu::parse_read_discrete_inputs_response
    };
    match parser(&response_pdu, payload.quantity) {
        Ok(bits) => success(
            request_id.to_string(),
            json!({ "status": "ok", "coils": bits, "exceptionCode": null }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_udp_read_registers(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let payload: TcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x03 {
        pdu::build_read_holding_registers_pdu
    } else {
        pdu::build_read_input_registers_pdu
    };
    let request_pdu = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    let parser = if fc == 0x03 {
        pdu::parse_read_holding_registers_response
    } else {
        pdu::parse_read_input_registers_response
    };
    match parser(&response_pdu, payload.quantity) {
        Ok(registers) => success(
            request_id.to_string(),
            json!({ "status": "ok", "registers": registers, "exceptionCode": null }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_udp_write_single_coil(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteSinglePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let value = match payload.value.as_bool() {
        Some(v) => v,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "value",
                    message: "FC05 值必须是布尔".into(),
                },
            );
        }
    };
    let request_pdu = match pdu::build_write_single_coil_pdu(payload.address, value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x05) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "value": value }),
        false,
    )
}

fn handle_udp_write_single_register(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteSinglePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let value = match payload.value.as_u64().and_then(|v| u16::try_from(v).ok()) {
        Some(v) => v,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "value",
                    message: "FC06 值必须是 0-65535".into(),
                },
            );
        }
    };
    let request_pdu = match pdu::build_write_single_register_pdu(payload.address, value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x06) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "value": value }),
        false,
    )
}

fn handle_udp_write_multiple_coils(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let values: Vec<bool> = payload.values.iter().map(|v| v.as_bool().unwrap_or(false)).collect();
    let request_pdu = match pdu::build_write_multiple_coils_pdu(payload.address, &values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x0F) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "quantity": values.len() }),
        false,
    )
}

fn handle_udp_write_multiple_registers(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let values: Vec<u16> = payload
        .values
        .iter()
        .map(|v| v.as_u64().and_then(|n| u16::try_from(n).ok()).unwrap_or(0))
        .collect();
    let request_pdu = match pdu::build_write_multiple_registers_pdu(payload.address, &values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x10) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "quantity": values.len() }),
        false,
    )
}

// --- 值解码(纯计算,对标 28 种显示格式) ---

fn handle_decode_values(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: DecodeValuesPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let data_type = match crate::value_codec::DataType::parse(&payload.data_type) {
        Some(t) => t,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "dataType",
                    message: format!("不支持的数据类型: {}", payload.data_type),
                },
            );
        }
    };
    let reg_per_elem = data_type.register_count().max(1);
    let count = payload.count.unwrap_or_else(|| {
        if reg_per_elem == 0 {
            1
        } else {
            payload.registers.len() / reg_per_elem
        }
    });
    let offset = payload.offset.unwrap_or(0);
    let values = crate::value_codec::decode_values(
        &payload.registers,
        offset,
        count,
        data_type,
        payload.scale,
        payload.offset_value,
    );
    success(
        request_id.to_string(),
        json!({ "values": values, "dataType": payload.data_type }),
        false,
    )
}

// --- 扫描站号(仅 TCP/UDP 连接) ---

// =============================================================================
// 三菱 MC 协议命令
// =============================================================================

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McAddressPayload {
    address: String,
}

/// mc_parse_address:富文本地址 → 结构化(device_code/head/is_bit)。
fn handle_mc_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McAddressPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => success(
            request_id.to_string(),
            json!({
                "deviceCode": a.device_code,
                "headNumber": a.head_number,
                "isBit": a.is_bit,
                "headBytes": crate::mc_address::encode_head_number(a.head_number),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McReadPayload {
    address: String,
    points: u16,
}

/// mc_build_read:地址+点数 → 完整 3E/4E 请求帧(hex 数组)。
fn handle_mc_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_read_batch_pdu(&addr, payload.points) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let frame = crate::mc_frame::build_request_frame(
        crate::mc_frame::FrameType::Type3E,
        &crate::mc_frame::AccessRoute::default(),
        0x0010,
        &req_data,
        0,
    );
    success(
        request_id.to_string(),
        json!({ "frame": frame }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McWritePayload {
    address: String,
    values: Vec<u16>,
}

/// mc_build_write:地址+值 → 完整 3E/4E 写请求帧。
fn handle_mc_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McWritePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_write_batch_pdu(&addr, &payload.values) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let frame = crate::mc_frame::build_request_frame(
        crate::mc_frame::FrameType::Type3E,
        &crate::mc_frame::AccessRoute::default(),
        0x0010,
        &req_data,
        0,
    );
    success(
        request_id.to_string(),
        json!({ "frame": frame }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McParseResponsePayload {
    frame: Vec<u8>,
    points: u16,
    #[serde(default)]
    is_bit: bool,
}

/// mc_parse_response:响应帧字节 → 结束代码 + 解析值。
fn handle_mc_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McParseResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let resp = match crate::mc_frame::parse_response_frame(&payload.frame) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let values = if resp.end_code == 0x0000 {
        match crate::mc_pdu::parse_read_batch_response(&resp.data, payload.points, payload.is_bit) {
            Ok(v) => v,
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    } else {
        Vec::new()
    };
    success(
        request_id.to_string(),
        json!({
            "endCode": resp.end_code,
            "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code),
            "values": values,
        }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxLinksBuildPayload {
    station: u8,
    cmd: String,
    #[serde(default)]
    delay: u8,
    #[serde(default)]
    data: String,
}

/// fx_links_build:站号+命令+延时+数据 → FX Computer Link 请求帧(§3.2)。
fn handle_fx_links_build(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxLinksBuildPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_links::build_fx_links_request(
        payload.station,
        &payload.cmd,
        payload.delay,
        &payload.data,
    ) {
        Ok(frame) => {
            // 帧尾固定为 SUM(2)+CRLF(2),和校验范围 = 站号首字符 ~ ETX(即 frame[1..len-4])
            let checksum = crate::fx_links::fx_links_checksum(&frame[1..frame.len() - 4]);
            success(
                request_id.to_string(),
                json!({
                    "frame": frame,
                    "frameHex": format_hex(&frame),
                    "checksum": format!("{checksum:02X}"),
                }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxLinksParsePayload {
    response: Vec<u8>,
}

/// fx_links_parse:PLC 响应 → STX 数据 / ACK / NAK 错误码(§3.2.4)。
fn handle_fx_links_parse(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxLinksParsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_links::parse_fx_links_response(&payload.response) {
        Ok(crate::fx_links::FxLinksResponse::ReadData { station, pc, data }) => success(
            request_id.to_string(),
            json!({
                "status": "data",
                "station": station,
                "pc": pc,
                "data": data,
                "dataAscii": String::from_utf8_lossy(&data),
            }),
            false,
        ),
        Ok(crate::fx_links::FxLinksResponse::Ack) => success(
            request_id.to_string(),
            json!({ "status": "ack", "station": null, "pc": null, "data": [], "errorCode": null }),
            false,
        ),
        Ok(crate::fx_links::FxLinksResponse::Nak { station, error_code }) => success(
            request_id.to_string(),
            json!({
                "status": "nak",
                "station": station,
                "pc": null,
                "data": [],
                "errorCode": error_code,
                "errorMessage": crate::fx_links::fx_links_error_message(error_code),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxProgBuildReadPayload {
    device: String,
    /// 软元件编号(X/Y 八进制书写,如 "17";其余十进制)
    address: String,
    words: u16,
}

/// fx_prog_build_read:软元件+编号+字数 → FX 编程口 DEVICE READ 帧(§3.3)。
/// fx_links_read:站号+软元件+首地址+点数 → BR/WR 读请求帧(§3.2)。
fn handle_fx_links_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        device: String,
        head: u16,
        points: u16,
        #[serde(default)]
        delay: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_links::build_fx_links_read(p.station, &p.device, p.head, p.points, p.delay) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// fx_links_write_bits:BW 位写帧。
fn handle_fx_links_write_bits(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        device: String,
        head: u16,
        values: Vec<u16>,
        #[serde(default)]
        delay: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let bits: Vec<bool> = p.values.iter().map(|v| *v != 0).collect();
    match crate::fx_links::build_fx_links_write_bits(p.station, &p.device, p.head, &bits, p.delay) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// fx_links_write_words:WW 字写帧。
fn handle_fx_links_write_words(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        device: String,
        head: u16,
        values: Vec<u16>,
        #[serde(default)]
        delay: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_links::build_fx_links_write_words(p.station, &p.device, p.head, &p.values, p.delay) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fx_prog_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxProgBuildReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let number = match crate::fx_programming::fx_prog_parse_number(&payload.device, &payload.address)
    {
        Ok(n) => n,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::fx_programming::build_fx_prog_read(&payload.device, number, payload.words) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": format_hex(&frame),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxProgBuildWritePayload {
    device: String,
    /// 软元件编号(X/Y 八进制书写,其余十进制)
    address: String,
    values: Vec<u16>,
}

/// fx_prog_build_write:软元件+编号+字值 → FX 编程口 DEVICE WRITE 帧(低字节在前)。
fn handle_fx_prog_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxProgBuildWritePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let number = match crate::fx_programming::fx_prog_parse_number(&payload.device, &payload.address)
    {
        Ok(n) => n,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::fx_programming::build_fx_prog_write(&payload.device, number, &payload.values) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": format_hex(&frame),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxProgParsePayload {
    frame: Vec<u8>,
}

/// fx_prog_parse:FX 编程口响应 → STX 数据(含字解码)/ ACK / NAK 错误码(§3.3.2)。
fn handle_fx_prog_parse(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxProgParsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_programming::parse_fx_prog_response(&payload.frame) {
        Ok(crate::fx_programming::FxProgResponse::Data(data)) => {
            let words = crate::fx_programming::decode_fx_prog_word_data(&data).unwrap_or_default();
            success(
                request_id.to_string(),
                json!({
                    "status": "data",
                    "data": data,
                    "dataAscii": String::from_utf8_lossy(&data),
                    "words": words,
                }),
                false,
            )
        }
        Ok(crate::fx_programming::FxProgResponse::Ack) => success(
            request_id.to_string(),
            json!({ "status": "ack", "data": [], "words": [], "errorCode": null }),
            false,
        ),
        Ok(crate::fx_programming::FxProgResponse::Nak { error_code }) => success(
            request_id.to_string(),
            json!({
                "status": "nak",
                "data": [],
                "words": [],
                "errorCode": error_code,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenMcTcpPayload {
    connection_id: String,
    host: String,
    port: u16,
    #[serde(default = "default_mc_network_no")]
    network_no: u8,
    #[serde(default = "default_mc_pc_no")]
    pc_no: u8,
    #[serde(default = "default_mc_module_io")]
    module_io: u16,
    #[serde(default)]
    station_no: u8,
    #[serde(default = "default_mc_frame_type")]
    frame_type: String,
    #[serde(default = "default_mc_watchdog")]
    watchdog: u16,
}

fn default_mc_network_no() -> u8 { 0x00 }
fn default_mc_pc_no() -> u8 { 0xFF }
fn default_mc_module_io() -> u16 { 0x03FF }
fn default_mc_frame_type() -> String { "3e".into() }
fn default_mc_watchdog() -> u16 { 0x0010 }

fn handle_open_mc_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenMcTcpPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame_type = match payload.frame_type.to_lowercase().as_str() {
        "3e" => crate::mc_frame::FrameType::Type3E,
        "4e" => crate::mc_frame::FrameType::Type4E,
        other => {
            return failure(Some(request_id.to_string()), CoreError::Modbus {
                code: "MC_BAD_FRAME_TYPE",
                message: format!("帧类型「{other}」无效(支持 3e/4e)"),
                details: None,
            })
        }
    };
    let route = crate::mc_frame::AccessRoute {
        network_no: payload.network_no,
        pc_no: payload.pc_no,
        module_io: payload.module_io,
        station_no: payload.station_no,
    };
    match session.open_mc_tcp(
        &payload.connection_id,
        &payload.host,
        payload.port,
        route,
        frame_type,
        payload.watchdog,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": payload.connection_id, "frameType": payload.frame_type }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McTcpReadPayload {
    connection_id: String,
    address: String,
    points: u16,
}

/// mc_tcp_read:在线成批读——地址 → 帧 → 收发 → 解析值。
fn handle_mc_tcp_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McTcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_read_batch_pdu(&addr, payload.points) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({
                "endCode": resp.end_code,
                "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code),
            }),
            false,
        );
    }
    let values = match crate::mc_pdu::parse_read_batch_response(&resp.data, payload.points, addr.is_bit) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    success(
        request_id.to_string(),
        json!({
            "endCode": 0,
            "isBit": addr.is_bit,
            "values": values,
        }),
        false,
    )
}

/// mc_tcp_write:在线成批写。
fn handle_mc_tcp_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct McTcpWritePayload {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let payload: McTcpWritePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_write_batch_pdu(&addr, &payload.values) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    success(
        request_id.to_string(),
        json!({
            "endCode": resp.end_code,
            "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code),
        }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McSlavePayload {
    slave_id: String,
    #[serde(default = "default_mc_port")]
    port: u16,
    #[serde(default = "default_mc_seed")]
    seed: bool,
}

fn default_mc_port() -> u16 { 5000 }
fn default_mc_seed() -> bool { true }

fn handle_start_mc_tcp_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McSlavePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_mc_tcp_slave(&payload.slave_id, payload.port, payload.seed) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": payload.slave_id, "port": payload.port, "running": true }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_mc_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McSlavePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_mc_slave(&payload.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": payload.slave_id, "running": false }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McSlaveSetPayload {
    slave_id: String,
    device: String,
    start: u32,
    values: Vec<u16>,
}

fn handle_mc_slave_set(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McSlaveSetPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.mc_slave_set(&payload.slave_id, &payload.device, payload.start, &payload.values) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "set": true, "device": payload.device, "start": payload.start }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

// =============================================================================
// 三菱 MC 进阶命令(M2):随机/多块读写、CPU 控制、时钟、回送、型号
// =============================================================================

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McRandomReadPayload {
    connection_id: String,
    addresses: Vec<String>,
}

fn handle_mc_tcp_read_random(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McRandomReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut addrs = Vec::with_capacity(payload.addresses.len());
    for a in &payload.addresses {
        match crate::mc_address::parse_mc_address(a) {
            Ok(addr) => addrs.push(addr),
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let is_bit = addrs.first().map(|a| a.is_bit).unwrap_or(false);
    let req_data = match crate::mc_pdu::build_read_random_pdu(&addrs) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_random_response(&resp.data, addrs.len(), is_bit) {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "isBit": is_bit, "values": values }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McRandomWritePayload {
    connection_id: String,
    /// 地址 → 值 的有序对
    entries: Vec<McRandomWriteEntry>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McRandomWriteEntry {
    address: String,
    value: u16,
}

fn handle_mc_tcp_write_random(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McRandomWritePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut items = Vec::with_capacity(payload.entries.len());
    for e in &payload.entries {
        match crate::mc_address::parse_mc_address(&e.address) {
            Ok(a) => items.push((a, e.value)),
            Err(err) => return failure(Some(request_id.to_string()), err),
        }
    }
    let req_data = match crate::mc_pdu::build_write_random_word_pdu(&items) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    success(
        request_id.to_string(),
        json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McBlocksReadPayload {
    connection_id: String,
    blocks: Vec<McBlockPayload>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McBlockPayload {
    address: String,
    points: u16,
}

fn handle_mc_tcp_read_blocks(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McBlocksReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut blocks = Vec::with_capacity(payload.blocks.len());
    for b in &payload.blocks {
        match crate::mc_address::parse_mc_address(&b.address) {
            Ok(a) => blocks.push(crate::mc_pdu::McBlock { address: a, points: b.points }),
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let req_data = match crate::mc_pdu::build_read_blocks_pdu(&blocks) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_blocks_response(&resp.data, &blocks) {
        Ok(chunks) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "blocks": chunks }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McConnOnlyPayload {
    connection_id: String,
}

fn handle_mc_remote(session: &mut Session, request_id: &str, cmd: u16, payload: Value) -> CommandOutcome {
    let payload: McConnOnlyPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let req_data = match crate::mc_pdu::build_remote_control_pdu(cmd) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    success(
        request_id.to_string(),
        json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
        false,
    )
}

fn handle_mc_read_clock(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McConnOnlyPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let req_data = crate::mc_pdu::build_read_clock_pdu();
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_clock_response(&resp.data) {
        Ok(c) => success(
            request_id.to_string(),
            json!({
                "endCode": 0,
                "clock": {
                    "yearBCD": c.year, "monthBCD": c.month, "dayBCD": c.day,
                    "hourBCD": c.hour, "minuteBCD": c.minute, "secondBCD": c.second, "weekdayBCD": c.weekday,
                    "year": bcd(c.year), "month": bcd(c.month), "day": bcd(c.day),
                    "hour": bcd(c.hour), "minute": bcd(c.minute), "second": bcd(c.second), "weekday": bcd(c.weekday),
                }
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn bcd(v: u8) -> u8 { (v >> 4) * 10 + (v & 0x0F) }

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McEchoPayload {
    connection_id: String,
    #[serde(default = "default_echo_payload")]
    data: Vec<u8>,
}

fn default_echo_payload() -> Vec<u8> { vec![0xAB, 0xCD, 0xEF] }

fn handle_mc_echo_test(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McEchoPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let req_data = crate::mc_pdu::build_echo_test_pdu(&payload.data);
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    let matched = crate::mc_pdu::parse_echo_test_response(&resp.data, &payload.data).unwrap_or(false);
    success(
        request_id.to_string(),
        json!({ "endCode": 0, "matched": matched, "echoed": resp.data }),
        false,
    )
}

fn handle_mc_cpu_info(session: &mut Session, request_id: &str, kind: &str, payload: Value) -> CommandOutcome {
    let payload: McConnOnlyPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let req_data = if kind == "type" {
        crate::mc_pdu::build_read_cpu_type_pdu()
    } else {
        crate::mc_pdu::build_read_cpu_status_pdu()
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    if kind == "type" {
        match crate::mc_pdu::parse_read_cpu_type_response(&resp.data) {
            Ok(t) => success(request_id.to_string(), json!({ "endCode": 0, "cpuType": t }), false),
            Err(e) => failure(Some(request_id.to_string()), e),
        }
    } else {
        match crate::mc_pdu::parse_read_cpu_status_response(&resp.data) {
            Ok(s) => {
                let status = match s {
                    crate::mc_pdu::CpuStatus::Run => "RUN",
                    crate::mc_pdu::CpuStatus::Stop => "STOP",
                    crate::mc_pdu::CpuStatus::Pause => "PAUSE",
                    crate::mc_pdu::CpuStatus::Other(v) => {
                        return success(request_id.to_string(), json!({ "endCode": 0, "cpuStatus": format!("OTHER({v:#04x})") }), false)
                    }
                };
                success(request_id.to_string(), json!({ "endCode": 0, "cpuStatus": status }), false)
            }
            Err(e) => failure(Some(request_id.to_string()), e),
        }
    }
}

fn handle_mc_build_ascii_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
        points: u16,
        #[serde(default = "default_ascii_watchdog")]
        watchdog: u16,
    }
    fn default_ascii_watchdog() -> u16 { 0x0010 }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mc_ascii::build_ascii_read_request(
        crate::mc_frame::FrameType::Type3E, 0,
        &crate::mc_frame::AccessRoute::default(), p.watchdog, &p.address, p.points,
    ) {
        Ok(s) => success(request_id.to_string(), json!({ "ascii": s }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_mc_ascii(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenMcTcpPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame_type = match payload.frame_type.to_lowercase().as_str() {
        "3e" => crate::mc_frame::FrameType::Type3E,
        "4e" => crate::mc_frame::FrameType::Type4E,
        other => {
            return failure(Some(request_id.to_string()), CoreError::Modbus {
                code: "MC_BAD_FRAME_TYPE",
                message: format!("帧类型「{other}」无效(支持 3e/4e)"),
                details: None,
            })
        }
    };
    let route = crate::mc_frame::AccessRoute {
        network_no: payload.network_no,
        pc_no: payload.pc_no,
        module_io: payload.module_io,
        station_no: payload.station_no,
    };
    match session.open_mc_tcp_ascii(
        &payload.connection_id, &payload.host, payload.port, route, frame_type, payload.watchdog,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": payload.connection_id, "mode": "ascii" }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_ascii_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McTcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.mc_transact_ascii_read(&payload.connection_id, &payload.address, payload.points) {
        Ok((end_code, is_bit, values)) => {
            if end_code != 0 {
                return success(
                    request_id.to_string(),
                    json!({ "endCode": end_code, "endCodeMessage": crate::mc_frame::end_code_message(end_code) }),
                    false,
                );
            }
            success(
                request_id.to_string(),
                json!({ "endCode": 0, "isBit": is_bit, "values": values }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// open_mc_1e_tcp:A-1E/SLMP-1E TCP 连接(A 系列 E71 / FX3U-ENET / FX5U)。
// ============ 西门子 S7comm ============

fn handle_brand_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        brand: String,
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let profile = match p.brand.as_str() {
        "delta-es" => crate::brand_profiles::BrandProfile::DeltaDvpEs,
        "inovance-h3u" => crate::brand_profiles::BrandProfile::InovanceH3u,
        "inovance-h5u" => crate::brand_profiles::BrandProfile::InovanceH5u,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "BRAND_UNKNOWN",
                    message: format!("未知品牌「{other}」(当前支持: delta-es / inovance-h3u / inovance-h5u)·汇川/信捷映射待手册确认后加入"),
                    details: None,
                },
            )
        }
    };
    match crate::brand_profiles::parse_brand_address(profile, &p.address) {
        Ok(a) => success(
            request_id.to_string(),
            json!({
                "area": match a.area {
                    crate::brand_profiles::BrandArea::Coil => "coil",
                    crate::brand_profiles::BrandArea::DiscreteInput => "discrete",
                    crate::brand_profiles::BrandArea::HoldingRegister => "holding",
                },
                "modbusAddress": a.modbus_address,
                "modbusAddressHex": format!("0x{:04X}", a.modbus_address),
                "isBit": a.is_bit,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

// ============ 欧姆龙 FINS ============

fn fins_nodes_from_payload(payload: &Value) -> crate::fins_frame::FinsNodes {
    let mut n = crate::fins_frame::FinsNodes::default();
    if let Some(d) = payload.get("destNode").and_then(|v| v.as_u64()) {
        n.da1 = d as u8;
    }
    if let Some(s) = payload.get("sourceNode").and_then(|v| v.as_u64()) {
        n.sa1 = s as u8;
    }
    n
}

fn handle_fins_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fins_address::parse_fins_address(&p.address) {
        Ok(a) => success(
            request_id.to_string(),
            json!({
                "areaCode": format!("0x{:02X}", a.area_code),
                "address": a.address,
                "kind": format!("{:?}", a.kind),
                "wordBitFlag": a.word_bit_flag(),
                "encoded": a.encode().iter().map(|b| format!("{b:02X}")).collect::<String>(),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_fins_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        #[serde(default = "default_fins_port")]
        port: u16,
        #[serde(default)]
        dest_node: Option<u8>,
        #[serde(default)]
        source_node: Option<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut nodes = fins_nodes_from_payload(&json!({
        "destNode": p.dest_node, "sourceNode": p.source_node
    }));
    nodes.da1 = p.dest_node.unwrap_or(0);
    nodes.sa1 = p.source_node.unwrap_or(0);
    match session.open_fins_tcp(&p.connection_id, &p.host, p.port, nodes) {
        Ok(_) => success(
            request_id.to_string(),
            json!({ "connectionId": p.connection_id, "transport": "tcp", "port": p.port }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn default_fins_port() -> u16 { 9600 }

fn handle_open_fins_udp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        #[serde(default = "default_fins_port")]
        port: u16,
        #[serde(default)]
        dest_node: Option<u8>,
        #[serde(default)]
        source_node: Option<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut nodes = crate::fins_frame::FinsNodes::default();
    nodes.da1 = p.dest_node.unwrap_or(0);
    nodes.sa1 = p.source_node.unwrap_or(0);
    match session.open_fins_udp(&p.connection_id, &p.host, p.port, nodes) {
        Ok(_) => success(
            request_id.to_string(),
            json!({ "connectionId": p.connection_id, "transport": "udp", "port": p.port }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fins_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        #[serde(default = "one_count")]
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.fins_read(&p.connection_id, &p.address, p.count) {
        Ok((end_code, data)) => {
            if end_code != 0 {
                return failure(
                    Some(request_id.to_string()),
                    CoreError::Modbus {
                        code: "FINS_CPU_ERROR",
                        message: format!(
                            "FINS 结束码 0x{end_code:04X}:{}",
                            crate::fins_frame::end_code_message(end_code)
                        ),
                        details: Some(json!({ "endCode": end_code })),
                    },
                );
            }
            // 字访问:字节 → u16 大端;位访问:每字节 0/1
            let addr = crate::fins_address::parse_fins_address(&p.address);
            let is_bit = matches!(&addr, Ok(a) if a.kind == crate::fins_address::FinsKind::Bit);
            let values: Vec<u16> = if is_bit {
                data.iter().map(|b| *b as u16).collect()
            } else {
                data.chunks_exact(2).map(|c| u16::from_be_bytes([c[0], c[1]])).collect()
            };
            success(
                request_id.to_string(),
                json!({ "endCode": end_code, "data": data, "values": values, "isBit": is_bit }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn one_count() -> u16 { 1 }

fn handle_fins_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::fins_address::parse_fins_address(&p.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let is_bit = addr.kind == crate::fins_address::FinsKind::Bit;
    let count = p.values.len() as u16;
    let data: Vec<u8> = if is_bit {
        p.values.iter().map(|v| *v as u8).collect()
    } else {
        p.values.iter().flat_map(|v| v.to_be_bytes()).collect()
    };
    match session.fins_write(&p.connection_id, &p.address, count, &data) {
        Ok(end_code) => {
            if end_code != 0 {
                return failure(
                    Some(request_id.to_string()),
                    CoreError::Modbus {
                        code: "FINS_CPU_ERROR",
                        message: format!(
                            "FINS 结束码 0x{end_code:04X}:{}",
                            crate::fins_frame::end_code_message(end_code)
                        ),
                        details: Some(json!({ "endCode": end_code })),
                    },
                );
            }
            success(request_id.to_string(), json!({ "endCode": end_code }), false)
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_start_fins_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        #[serde(default = "default_fins_port")]
        port: u16,
        #[serde(default = "default_true_fins")]
        seed: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_fins_slave(&p.slave_id, p.port, p.seed) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": p.slave_id, "port": p.port, "protocol": "fins", "transports": ["tcp", "udp"] }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn default_true_fins() -> bool { true }

fn handle_stop_fins_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_fins_slave(&p.slave_id) {
        Ok(()) => success(request_id.to_string(), json!({ "stopped": p.slave_id }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fins_slave_set(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.fins_slave_set(&p.slave_id, &p.address, &p.values) {
        Ok(()) => success(request_id.to_string(), json!({ "ok": true }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fins_slave_get(session: &Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        address: String,
        #[serde(default = "one_count")]
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.fins_slave_get(&p.slave_id, &p.address, p.count) {
        Ok(values) => success(request_id.to_string(), json!({ "values": values }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::s7_address::parse_s7_address(&p.address) {
        Ok(addr) => success(
            request_id.to_string(),
            json!({
                "area": addr.area,
                "areaName": crate::s7_address::area_name(addr.area),
                "db": addr.db,
                "byte": addr.byte,
                "bit": addr.bit,
                "kind": format!("{:?}", addr.kind),
                "elemBytes": addr.kind.elem_bytes(),
                "anyAddressHex": addr.encode_any_address().iter().map(|b| format!("{b:02X}")).collect::<String>(),
                "display": addr.display(),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_s7_connection(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        #[serde(default = "default_s7_port")]
        port: u16,
        #[serde(default)]
        rack: u8,
        #[serde(default = "default_s7_slot")]
        slot: u8,
        /// 1=PG(默认) 2=OP 3=S7 Basic
        #[serde(default)]
        conn_type: u8,
        /// 十六进制 TSAP 覆盖(如 "0100");缺省用 rack/slot 公式
        #[serde(default)]
        local_tsap: Option<String>,
        #[serde(default)]
        remote_tsap: Option<String>,
        /// 0 = 默认 480
        #[serde(default)]
        pdu_request: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let parse_tsap = |s: &Option<String>, field: &str| -> Result<Option<u16>, CoreError> {
        match s {
            None => Ok(None),
            Some(hex) => u16::from_str_radix(hex.trim_start_matches("0x"), 16).map(Some).map_err(|_| {
                CoreError::Modbus {
                    code: "S7_TSAP_INVALID",
                    message: format!("{field} 应为十六进制(如 0100),实际「{}」", hex),
                    details: None,
                }
            }),
        }
    };
    let local = match parse_tsap(&p.local_tsap, "localTsap") {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let remote = match parse_tsap(&p.remote_tsap, "remoteTsap") {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match session.open_s7_connection(
        &p.connection_id,
        &p.host,
        p.port,
        p.rack,
        p.slot,
        p.conn_type,
        local,
        remote,
        p.pdu_request,
    ) {
        Ok(pdu_size) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "pduSize": pdu_size,
                "maxReadBytes": crate::s7_pdu::max_read_bytes(pdu_size),
                "maxWriteBytes": crate::s7_pdu::max_write_bytes(pdu_size),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// s7_read 请求项。
#[derive(Debug, Deserialize, Clone)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct S7ItemPayload {
    address: String,
    #[serde(default = "default_s7_count")]
    count: u16,
}

fn default_s7_count() -> u16 {
    1
}
fn default_s7_port() -> u16 {
    102
}
fn default_s7_seed() -> bool { true }
fn default_s7_slot() -> u8 {
    1
}

/// 按 PDU 预算与 20 项上限把请求项拆成多轮。
/// 每轮元素为 (原始项索引, 子项):分片子项共享原始索引,供结果合并。
fn s7_chunk_items(
    items: &[crate::s7_pdu::S7Item],
    budget_bytes: usize,
) -> Vec<Vec<(usize, crate::s7_pdu::S7Item)>> {
    let mut chunks: Vec<Vec<(usize, crate::s7_pdu::S7Item)>> = Vec::new();
    let mut cur: Vec<(usize, crate::s7_pdu::S7Item)> = Vec::new();
    let mut cur_bytes = 0usize;
    for (origin, item) in items.iter().enumerate() {
        let bytes = item.data_bytes();
        if bytes > budget_bytes {
            // 单项超预算:按元素宽度拆成多个子项(每子项独占一轮)
            let per = (budget_bytes / item.addr.kind.elem_bytes() as usize).max(1);
            let mut remaining = item.count as usize;
            while remaining > 0 {
                let take = remaining.min(per).min(u16::MAX as usize) as u16;
                let mut sub = item.clone();
                sub.count = take;
                remaining -= take as usize;
                chunks.push(vec![(origin, sub)]);
            }
            continue;
        }
        if cur.len() >= crate::s7_pdu::MAX_ITEMS || cur_bytes + bytes > budget_bytes {
            if !cur.is_empty() {
                chunks.push(std::mem::take(&mut cur));
                cur_bytes = 0;
            }
        }
        cur_bytes += bytes;
        cur.push((origin, item.clone()));
    }
    if !cur.is_empty() {
        chunks.push(cur);
    }
    chunks
}

fn handle_s7_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        /// 单项:[{address, count}];字符串简写 "DB1.DBW0" 等价 {address, count:1}
        items: Vec<serde_json::Value>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut items: Vec<crate::s7_pdu::S7Item> = Vec::with_capacity(p.items.len());
    for raw in &p.items {
        let item_payload: S7ItemPayload = match raw {
            Value::String(s) => S7ItemPayload { address: s.clone(), count: 1 },
            other => match serde_json::from_value(other.clone()) {
                Ok(v) => v,
                Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
            },
        };
        match crate::s7_pdu::S7Item::new(&item_payload.address, item_payload.count) {
            Ok(it) => items.push(it),
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let pdu_size = match session.s7_pdu_size(&p.connection_id) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let budget = crate::s7_pdu::max_read_bytes(pdu_size);
    let item_count = items.len();
    let chunks = s7_chunk_items(&items, budget);
    // 原始项索引 → (returnCode, 数据拼接)
    let mut merged: Vec<(u8, Vec<u8>)> = vec![(0xFF, Vec::new()); item_count];
    for chunk in chunks {
        let sub_items: Vec<crate::s7_pdu::S7Item> =
            chunk.iter().map(|(_, it)| it.clone()).collect();
        match session.s7_read(&p.connection_id, &sub_items) {
            Ok(parts) => {
                for (i, part) in parts.iter().enumerate() {
                    let (origin, sub) = match chunk.get(i) {
                        Some(v) => v,
                        None => continue, // 防御:响应 item 数 < 请求数时跳过(不 panic)
                    };
                    // 分片子项:期望字节数按子项 count 计
                    let exp = sub.data_bytes();
                    let mut data = part.data.clone();
                    data.truncate(exp);
                    merged[*origin].0 = part.return_code;
                    merged[*origin].1.extend(data);
                }
            }
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let results: Vec<Value> = merged
        .into_iter()
        .map(|(rc, data)| {
            json!({
                "returnCode": rc,
                "returnCodeMessage": crate::s7_pdu::item_return_code_message(rc),
                "data": data,
            })
        })
        .collect();
    success(request_id.to_string(), json!({ "items": results }), false)
}

fn handle_s7_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct ItemIn {
        address: String,
        #[serde(default)]
        count: Option<u16>,
        /// 字节数组(10 进制);位写时每字节 1 个位
        values: Option<Vec<u8>>,
        /// 或 hex 字符串(优先级低于 values)
        hex: Option<String>,
    }
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        items: Vec<ItemIn>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut items: Vec<crate::s7_pdu::S7Item> = Vec::new();
    let mut blocks: Vec<Vec<u8>> = Vec::new();
    for it in &p.items {
        let data: Vec<u8> = if let Some(v) = &it.values {
            v.clone()
        } else if let Some(hex) = &it.hex {
            match hex_parse_bytes(hex) {
                Some(b) => b,
                None => {
                    return failure(
                        Some(request_id.to_string()),
                        CoreError::Modbus {
                            code: "S7_WRITE_MISMATCH",
                            message: format!("hex「{}」不合法", hex),
                            details: None,
                        },
                    )
                }
            }
        } else {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "S7_WRITE_MISMATCH",
                    message: format!("项 {} 缺少 values/hex", it.address),
                    details: None,
                },
            );
        };
        let count = it.count.unwrap_or_else(|| match crate::s7_address::parse_s7_address(&it.address) {
            Ok(a) if a.kind == crate::s7_address::S7Kind::Bit => data.len() as u16,
            Ok(a) => (data.len() as u16) / a.kind.elem_bytes() as u16,
            Err(_) => 1,
        });
        match crate::s7_pdu::S7Item::new(&it.address, count) {
            Ok(item) => {
                items.push(item);
                blocks.push(data);
            }
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let pdu_size = match session.s7_pdu_size(&p.connection_id) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let budget = crate::s7_pdu::max_write_bytes(pdu_size);
    // 简化:按 20 项一轮直接写(单轮超限由上层避免;S1 UI 单项写)
    let mut index = 0usize;
    let mut codes: Vec<u8> = Vec::new();
    while index < items.len() {
        let end = (index + crate::s7_pdu::MAX_ITEMS).min(items.len());
        let chunk = &items[index..end];
        let blocks_chunk = &blocks[index..end];
        let total: usize = chunk.iter().map(|i| i.data_bytes()).sum();
        if total > budget {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "S7_DATA_OVER_PDU",
                    message: format!(
                        "写数据总量 {total} 字节超过 PDU 预算 {budget}(协商 PDU={pdu_size}),请分次写"
                    ),
                    details: None,
                },
            );
        }
        match session.s7_write(&p.connection_id, chunk, blocks_chunk) {
            Ok(rcs) => codes.extend(rcs),
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
        index = end;
    }
    success(
        request_id.to_string(),
        json!({
            "returnCodes": codes,
            "returnCodeMessages": codes
                .iter()
                .map(|c| crate::s7_pdu::item_return_code_message(*c))
                .collect::<Vec<_>>(),
        }),
        false,
    )
}

fn hex_parse_bytes(hex: &str) -> Option<Vec<u8>> {
    let clean: String = hex.chars().filter(|c| !c.is_whitespace()).collect();
    if clean.len() % 2 != 0 {
        return None;
    }
    (0..clean.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&clean[i..i + 2], 16).ok())
        .collect()
}

fn handle_start_s7_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        #[serde(default = "default_s7_port")]
        port: u16,
        #[serde(default = "default_s7_seed")]
        seed: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_s7_slave(&p.slave_id, p.port, p.seed) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": p.slave_id, "port": p.port, "protocol": "s7comm", "pduLimit": crate::s7_slave::SLAVE_PDU_LIMIT }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_s7_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_s7_slave(&p.slave_id) {
        Ok(()) => success(request_id.to_string(), json!({ "stopped": p.slave_id }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_slave_set(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        address: String,
        values: Option<Vec<u8>>,
        hex: Option<String>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let bytes: Vec<u8> = if let Some(v) = p.values {
        v
    } else if let Some(hex) = p.hex {
        match hex_parse_bytes(&hex) {
            Some(b) => b,
            None => {
                return failure(
                    Some(request_id.to_string()),
                    CoreError::Modbus {
                        code: "S7_SLAVE_WRITE_FAILED",
                        message: format!("hex「{hex}」不合法"),
                        details: None,
                    },
                )
            }
        }
    } else {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "S7_SLAVE_WRITE_FAILED",
                message: "缺少 values/hex".to_string(),
                details: None,
            },
        );
    };
    match session.s7_slave_set(&p.slave_id, &p.address, &bytes) {
        Ok(()) => success(request_id.to_string(), json!({ "ok": true, "address": p.address }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_slave_get(session: &Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        address: String,
        #[serde(default = "default_s7_count")]
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.s7_slave_get(&p.slave_id, &p.address, p.count) {
        Ok(data) => success(
            request_id.to_string(),
            json!({ "address": p.address, "data": data }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_ppi_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        port: u16,
        #[serde(default = "default_ppi_station")]
        station: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_ppi_tcp(&p.connection_id, &p.host, p.port, p.station) {
        Ok(()) => success(request_id.to_string(), json!({ "connectionId": p.connection_id, "station": p.station, "note": "串口形态请用主站页串口 + ppi framing" }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn default_ppi_station() -> u8 { 2 }

fn handle_ppi_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { connection_id: String, address: String, count: u16 }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.ppi_read(&p.connection_id, &p.address, p.count) {
        Ok(items) => success(request_id.to_string(), json!({
            "items": items.iter().map(|it| json!({
                "returnCode": it.return_code,
                "returnCodeMessage": crate::s7_pdu::item_return_code_message(it.return_code),
                "data": it.data,
            })).collect::<Vec<_>>(),
        }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_ppi_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { connection_id: String, address: String, count: u16, values: Vec<u8> }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.ppi_write(&p.connection_id, &p.address, p.count, &p.values) {
        Ok(codes) => success(request_id.to_string(), json!({
            "returnCodes": codes,
            "returnCodeMessages": codes.iter().map(|c| crate::s7_pdu::item_return_code_message(*c)).collect::<Vec<_>>(),
        }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_start_ppi_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { slave_id: String, port: u16, #[serde(default = "default_true_fins")] seed: bool }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_ppi_slave(&p.slave_id, p.port, p.seed) {
        Ok(()) => success(request_id.to_string(), json!({ "slaveId": p.slave_id, "port": p.port, "protocol": "ppi-over-tcp" }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_ppi_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { slave_id: String }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_ppi_slave(&p.slave_id) {
        Ok(()) => success(request_id.to_string(), json!({ "stopped": p.slave_id }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_hostlink_build_fins(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { station: u8, #[serde(default)] area: String, #[serde(default)] byte: u32, count: Option<u16> }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    // 构建 FINS 读帧(默认 DM 区)
    let area_code = if p.area.is_empty() { 0x82 } else {
        match p.area.as_str() { "DM" | "" => 0x82, "CIO" | "WR" => 0xB0, "HR" => 0xB2, _ => 0x82 }
    };
    let count = p.count.unwrap_or(1);
    let fins = crate::fins_frame::build_read_frame(
        &crate::fins_frame::FinsNodes::default(), 1,
        &crate::fins_address::FinsAddress { area_code, address: p.byte, kind: crate::fins_address::FinsKind::Word },
        count,
    );
    let frame = crate::hostlink::build_hostlink_fins(p.station, &fins);
    success(request_id.to_string(), json!({
        "frame": frame,
        "frameText": String::from_utf8_lossy(&frame),
    }), false)
}

fn handle_hostlink_parse_fins(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { frame: Vec<u8> }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::hostlink::parse_hostlink_fins(&p.frame) {
        Ok(fins) => {
            let ack = crate::fins_frame::parse_response_frame(&fins)
                .map_err(|e| CoreError::Modbus { code: "HOSTLINK_FINS_INVALID", message: e.to_string(), details: None });
            match ack {
                Ok(resp) => success(request_id.to_string(), json!({ "endCode": resp.end_code, "data": resp.data }), false),
                Err(e) => failure(Some(request_id.to_string()), e),
            }
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_hostlink_build_cmode_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { station: u8, dmStart: u16, wordCount: u16 }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame = crate::hostlink::build_cmode_read_dm(p.station, p.dmStart, p.wordCount);
    success(request_id.to_string(), json!({
        "frame": frame,
        "frameText": String::from_utf8_lossy(&frame),
    }), false)
}

fn handle_hostlink_parse_cmode_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { frame: Vec<u8> }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::hostlink::parse_cmode_read_dm(&p.frame) {
        Ok(words) => success(request_id.to_string(), json!({ "words": words }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_uss_build_request(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        #[serde(default)]
        param: Option<u16>,
        #[serde(default)]
        value: Option<u16>,
        #[serde(default)]
        pzd: Option<Vec<u8>>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (pke, ind) = if let Some(v) = p.value {
        let param = p.param.unwrap_or(0);
        crate::uss_frame::pke_write_16(param, v)
    } else {
        (crate::uss_frame::pke_read(p.param.unwrap_or(0)), [0u8, 0])
    };
    let pzd = p.pzd.unwrap_or_default();
    let frame = crate::uss_frame::build_uss_request(p.station, pke, ind, &pzd);
    success(request_id.to_string(), json!({
        "frame": frame,
        "frameHex": frame.iter().map(|b| format!("{b:02X}")).collect::<String>(),
        "pkeAk": format!("0x{:X}", pke[0] >> 4),
        "pkeAkMessage": crate::uss_frame::ak_message(pke[0] >> 4),
    }), false)
}

fn handle_uss_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { frame: Vec<u8> }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::uss_frame::parse_uss_response(&p.frame) {
        Ok((station, pke, ind, pzd)) => success(request_id.to_string(), json!({
            "station": station,
            "pkeAk": format!("0x{:X}", pke[0] >> 4),
            "pkeAkMessage": crate::uss_frame::ak_message(pke[0] >> 4),
            "pkePnu": ((pke[0] as u16 & 0x0F) << 8) | pke[1] as u16,
            "ind": ind,
            "pzd": pzd,
        }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_rk512_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { area: String, db: u16, offset: u16, count: u16 }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::rk512::build_rk512_read(&p.area, p.db, p.offset, p.count) {
        Ok(frame) => success(request_id.to_string(), json!({
            "frame": frame,
            "frameHex": frame.iter().map(|b| format!("{b:02X}")).collect::<String>(),
        }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_rk512_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { area: String, db: u16, offset: u16, values: Vec<u8> }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::rk512::build_rk512_write(&p.area, p.db, p.offset, &p.values) {
        Ok(frame) => success(request_id.to_string(), json!({
            "frame": frame,
            "frameHex": frame.iter().map(|b| format!("{b:02X}")).collect::<String>(),
        }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_rk512_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { frame: Vec<u8> }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::rk512::parse_rk512_response(&p.frame) {
        Ok((resp, data)) => success(request_id.to_string(), json!({
            "error": resp.error,
            "errorMessage": crate::rk512::rk512_error_message(resp.error),
            "func": resp.func,
            "count": resp.count,
            "db": resp.db,
            "offset": resp.offset,
            "data": data,
        }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_cpu_control(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { connection_id: String, action: String }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.s7_cpu_control(&p.connection_id, &p.action) {
        Ok((code, msg)) => success(request_id.to_string(), json!({ "result": code, "message": msg }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_read_status(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { connection_id: String }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.s7_read_status(&p.connection_id) {
        Ok(mode) => success(request_id.to_string(), json!({ "mode": mode }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_password(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { connection_id: String, password: String }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.s7_password(&p.connection_id, &p.password) {
        Ok(()) => success(request_id.to_string(), json!({ "ok": true, "note": "S7-1200/1500 无会话密码机制,仅 300/400 有效" }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_fw_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { connection_id: String, host: String, port: u16 }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_fw_tcp(&p.connection_id, &p.host, p.port) {
        Ok(()) => success(request_id.to_string(), json!({ "connectionId": p.connection_id, "port": p.port }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FwAddrPayload {
    connection_id: String,
    /// 区:"DB"/"M"/"I"/"Q"/"C"/"T"
    area: String,
    #[serde(default)]
    db: u8,
    address: u16,
    length: Option<u16>,
    values: Option<Vec<u8>>,
}

fn handle_fw_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    // 注意:不用 serde(flatten) —— flatten 与 deny_unknown_fields 组合存在字段丢失的已知问题
    let p: FwAddrPayload = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let Some(org) = crate::s7_fetchwrite::fw_area_code(&p.area) else {
        return failure(Some(request_id.to_string()), CoreError::Modbus {
            code: "S7_FW_INVALID", message: format!("区「{}」不支持(DB/M/I/Q/C/T)", p.area), details: None });
    };
    match session.fw_read(&p.connection_id, org, p.db, p.address, p.length.unwrap_or(0)) {
        Ok(data) => success(request_id.to_string(), json!({ "data": data }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fw_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let p: FwAddrPayload = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let Some(org) = crate::s7_fetchwrite::fw_area_code(&p.area) else {
        return failure(Some(request_id.to_string()), CoreError::Modbus {
            code: "S7_FW_INVALID", message: format!("区「{}」不支持", p.area), details: None });
    };
    match session.fw_write(&p.connection_id, org, p.db, p.address, &p.values.unwrap_or_default()) {
        Ok(()) => success(request_id.to_string(), json!({ "ok": true }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_start_fw_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { slave_id: String, port: u16, #[serde(default = "default_true_fins")] seed: bool }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_fw_slave(&p.slave_id, p.port, p.seed) {
        Ok(()) => success(request_id.to_string(), json!({ "slaveId": p.slave_id, "port": p.port }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_fw_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P { slave_id: String }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p, Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_fw_slave(&p.slave_id) {
        Ok(()) => success(request_id.to_string(), json!({ "stopped": p.slave_id }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_mc_1e(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        port: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_mc_1e_tcp(&p.connection_id, &p.host, p.port) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": p.connection_id, "frame": "1e" }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Mc1eAddrPayload {
    connection_id: String,
    address: String,
}

fn mc_1e_split(address: &str) -> Result<(String, u32), CoreError> {
    let a = address.trim();
    let split = a.find(|c: char| c.is_ascii_digit()).ok_or_else(|| CoreError::Modbus {
        code: "MC_ADDRESS_INVALID",
        message: format!("「{a}」不是有效的软元件地址(如 D100/M100/X17)"),
        details: None,
    })?;
    let (prefix, num_str) = a.split_at(split);
    let num = crate::fx_programming::fx_prog_parse_number(prefix, num_str)?;
    Ok((prefix.to_uppercase(), num))
}

fn handle_mc_1e_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        points: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (prefix, num) = match mc_1e_split(&p.address) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let is_bit = matches!(prefix.as_str(), "X" | "Y" | "M" | "S" | "B" | "TS" | "TC" | "CS" | "CC" | "SS" | "SC");
    let cmd = if is_bit { crate::mc_1e::CMD1E_BIT_READ } else { crate::mc_1e::CMD1E_WORD_READ };
    let req = match crate::mc_1e::build_1e_read(cmd, &prefix, num, p.points, 10) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_1e_transact(&p.connection_id, &req) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_1e::parse_1e_response(&resp, cmd, p.points) {
        Ok(crate::mc_1e::OneEResponse::Words(w)) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "isBit": false, "values": w }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::Bits(b)) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "isBit": true, "values": b.iter().map(|v| if *v { 1 } else { 0 }).collect::<Vec<u16>>() }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::WriteAck) => success(
            request_id.to_string(),
            json!({ "endCode": 0 }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::Error { code, detail }) => success(
            request_id.to_string(),
            json!({ "endCode": code, "detail": detail, "message": crate::mc_1e::onee_error_message(code, detail) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_1e_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (prefix, num) = match mc_1e_split(&p.address) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let is_bit = matches!(prefix.as_str(), "X" | "Y" | "M" | "S" | "B" | "TS" | "TC" | "CS" | "CC" | "SS" | "SC");
    let count = u16::try_from(p.values.len()).unwrap_or(0);
    let req = if is_bit {
        let bits: Vec<bool> = p.values.iter().map(|v| *v != 0).collect();
        crate::mc_1e::build_1e_write(crate::mc_1e::CMD1E_BIT_WRITE, &prefix, num, &[], &bits, 10)
    } else {
        crate::mc_1e::build_1e_write(crate::mc_1e::CMD1E_WORD_WRITE, &prefix, num, &p.values, &[], 10)
    };
    let req = match req {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = count;
    let resp = match session.mc_1e_transact(&p.connection_id, &req) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_1e::parse_1e_response(&resp, if is_bit { crate::mc_1e::CMD1E_BIT_WRITE } else { crate::mc_1e::CMD1E_WORD_WRITE }, 0) {
        Ok(crate::mc_1e::OneEResponse::WriteAck) => success(request_id.to_string(), json!({ "endCode": 0 }), false),
        Ok(crate::mc_1e::OneEResponse::Error { code, detail }) => success(
            request_id.to_string(),
            json!({ "endCode": code, "detail": detail, "message": crate::mc_1e::onee_error_message(code, detail) }),
            false,
        ),
        _ => failure(Some(request_id.to_string()), CoreError::Modbus {
            code: "MC_1E_UNEXPECTED_RESPONSE",
            message: "1E 写响应格式异常".into(),
            details: None,
        }),
    }
}

/// open_mc_udp_connection:MC/SLMP over UDP(§2.5,PLC 侧打开设置选 UDP)。
fn handle_open_mc_udp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenMcTcpPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame_type = match payload.frame_type.to_lowercase().as_str() {
        "3e" => crate::mc_frame::FrameType::Type3E,
        "4e" => crate::mc_frame::FrameType::Type4E,
        other => {
            return failure(Some(request_id.to_string()), CoreError::Modbus {
                code: "MC_BAD_FRAME_TYPE",
                message: format!("帧类型「{other}」无效(支持 3e/4e)"),
                details: None,
            })
        }
    };
    let route = crate::mc_frame::AccessRoute {
        network_no: payload.network_no,
        pc_no: payload.pc_no,
        module_io: payload.module_io,
        station_no: payload.station_no,
    };
    match session.open_mc_udp(
        &payload.connection_id, &payload.host, payload.port, route, frame_type, payload.watchdog,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": payload.connection_id, "transport": "udp" }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_udp_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McTcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_read_batch_pdu(&addr, payload.points) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_udp_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_batch_response(&resp.data, payload.points, addr.is_bit) {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "isBit": addr.is_bit, "values": values }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_udp_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&p.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_write_batch_pdu(&addr, &p.values) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match session.mc_udp_transact(&p.connection_id, &req_data) {
        Ok(resp) => success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_ascii_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.mc_transact_ascii_write(&p.connection_id, &p.address, &p.values) {
        Ok(end_code) => success(
            request_id.to_string(),
            json!({ "endCode": end_code, "endCodeMessage": crate::mc_frame::end_code_message(end_code) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_c24_read:地址+点数+格式+站号 → C24 完整读请求帧(一步到位,供 Electron 串口事务)。
fn handle_mc_c24_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
        points: u16,
        #[serde(default = "default_c24_format")]
        format: String,
        #[serde(default)]
        station: u8,
    }
    fn default_c24_format() -> String { "1".into() }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&p.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let app = match crate::mc_pdu::build_read_batch_pdu(&addr, p.points) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let format = match mc_serial_format_from_str(&p.format) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_serial::build_mc_serial_3c(p.station, format, &app) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame), "isBit": addr.is_bit }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_c24_parse_read:C24 响应帧 → 去封装 → 应用区(结束码+数据) → 解出值。
fn handle_mc_c24_parse_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
        points: u16,
        is_bit: bool,
        #[serde(default = "default_c24_format")]
        format: String,
    }
    fn default_c24_format() -> String { "1".into() }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let format = match mc_serial_format_from_str(&p.format) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let (_station, app) = match crate::mc_serial::parse_mc_serial_3c_response(&p.frame, format) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    // 应用区 = 结束代码(2 LE) + 数据
    if app.len() < 2 {
        return failure(Some(request_id.to_string()), CoreError::Modbus {
            code: "MC_SERIAL_FRAME_TOO_SHORT",
            message: format!("C24 应用区 {} 字节,短于结束代码 2 字节", app.len()),
            details: None,
        });
    }
    let end_code = u16::from_le_bytes([app[0], app[1]]);
    if end_code != 0 {
        return success(
            request_id.to_string(),
            json!({ "endCode": end_code, "endCodeMessage": crate::mc_frame::end_code_message(end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_batch_response(&app[2..], p.points, p.is_bit) {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "values": values }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

// === 三菱 MC 串口 C24(3C/4C 离线组帧,§3.1)与 A-1E 帧(§3.4)===

/// 数据格式字符串 → McSerialFormat("1"/"3"/"4")。
fn mc_serial_format_from_str(format: &str) -> Result<crate::mc_serial::McSerialFormat, CoreError> {
    use crate::mc_serial::McSerialFormat;
    match format {
        "1" => Ok(McSerialFormat::Format1Ascii),
        "3" => Ok(McSerialFormat::Format3Binary),
        "4" => Ok(McSerialFormat::Format4BinaryNoChecksum),
        other => Err(CoreError::Modbus {
            code: "MC_SERIAL_BAD_FORMAT",
            message: format!("数据格式「{other}」无效(支持 1=ASCII和校验 / 3=二进制和校验 / 4=二进制无校验)"),
            details: None,
        }),
    }
}

/// mc_serial_build_3c:站号 + 3E 应用区(mc_pdu 产出)→ 3C/4C 串口帧。
fn handle_mc_serial_build_3c(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        format: String,
        station: u8,
        /// 3E 应用数据区(指令+子命令+软元件+点数…,mc_build_read 产出的 data 区)
        mc_app_data: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let format = match mc_serial_format_from_str(&p.format) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_serial::build_mc_serial_3c(p.station, format, &p.mc_app_data) {
        Ok(frame) => {
            // 和校验:格式1 = 帧去掉尾部 SUM(2)+CRLF(2) 后低 8 位;格式3 = 帧尾 2 字节 LE
            let checksum = match format {
                crate::mc_serial::McSerialFormat::Format1Ascii => {
                    let sum = crate::mc_serial::mc_serial_checksum_ascii(&frame[..frame.len() - 4]);
                    Some(format!("{sum:02X}"))
                }
                crate::mc_serial::McSerialFormat::Format3Binary => {
                    let n = frame.len();
                    Some(format!("{:04X}", u16::from_le_bytes([frame[n - 2], frame[n - 1]])))
                }
                crate::mc_serial::McSerialFormat::Format4BinaryNoChecksum => None,
            };
            success(
                request_id.to_string(),
                json!({
                    "frame": frame,
                    "frameHex": format_hex(&frame),
                    "checksum": checksum,
                }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_serial_parse_3c:PLC 串口响应 → (站号, 3E 应用区响应体)。
fn handle_mc_serial_parse_3c(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        format: String,
        frame: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let format = match mc_serial_format_from_str(&p.format) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_serial::parse_mc_serial_3c_response(&p.frame, format) {
        Ok((station, mc_app_data)) => success(
            request_id.to_string(),
            json!({
                "station": station,
                "mcAppData": mc_app_data,
                "mcAppDataHex": format_hex(&mc_app_data),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_1e_build_read:A-1E 读请求帧(命令 00 位读 / 01 字读)。
fn handle_mc_1e_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        /// 0x00 位读 / 0x01 字读(§3.4.2 命令字节表)
        cmd: u8,
        device: String,
        head: u32,
        points: u16,
        #[serde(default = "default_1e_watchdog")]
        watchdog: u16,
    }
    fn default_1e_watchdog() -> u16 { 10 }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mc_1e::build_1e_read(p.cmd, &p.device, p.head, p.points, p.watchdog) {
        Ok(frame) => {
            let code = crate::mc_1e::device_code_1e_ascii(&p.device)
                .map(|c| String::from_utf8_lossy(&c).into_owned())
                .unwrap_or_default();
            success(
                request_id.to_string(),
                json!({ "frame": frame, "frameHex": format_hex(&frame), "deviceCode": code }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_1e_build_write:A-1E 写请求帧(命令 02 位写 / 03 字写)。
fn handle_mc_1e_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        /// 0x02 位写 / 0x03 字写(§3.4.2 命令字节表)
        cmd: u8,
        device: String,
        head: u32,
        #[serde(default)]
        values_words: Vec<u16>,
        /// 位值 0/1(内部转 bool)
        #[serde(default)]
        values_bits: Vec<u16>,
        #[serde(default = "default_1e_watchdog")]
        watchdog: u16,
    }
    fn default_1e_watchdog() -> u16 { 10 }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let bits: Vec<bool> = p.values_bits.iter().map(|v| *v != 0).collect();
    match crate::mc_1e::build_1e_write(p.cmd, &p.device, p.head, &p.values_words, &bits, p.watchdog) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_1e_parse:A-1E 响应帧 → 字值 / 位值 / 写确认 / 异常(5BH 详细码)。
fn handle_mc_1e_parse(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
        /// 原请求命令字节(决定数据区解释:位打包/字小端/写确认)
        cmd: u8,
        #[serde(default)]
        points: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mc_1e::parse_1e_response(&p.frame, p.cmd, p.points) {
        Ok(crate::mc_1e::OneEResponse::Words(values)) => success(
            request_id.to_string(),
            json!({ "status": "words", "values": values }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::Bits(bits)) => success(
            request_id.to_string(),
            json!({
                "status": "bits",
                "values": bits.iter().map(|b| u16::from(*b)).collect::<Vec<u16>>(),
            }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::WriteAck) => success(
            request_id.to_string(),
            json!({ "status": "writeAck" }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::Error { code, detail }) => success(
            request_id.to_string(),
            json!({
                "status": "error",
                "errorCode": code,
                "detailCode": detail,
                "message": crate::mc_1e::onee_error_message(code, detail),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_scan_station_ids(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: ScanStationIdsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let started_at = std::time::Instant::now();
    let request_pdu = match pdu::build_read_holding_registers_pdu(0, 1) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    // 接通 timeout_ms:按用户参数缩短每站探测等待(默认 500ms)。
    // 旧实现忽略该参数,247 空站 × 5s 默认超时 = 约 20 分钟主循环假死。
    let _ = session.set_connection_read_timeout_ms(&payload.connection_id, payload.timeout_ms);
    let mut found: Vec<Value> = Vec::new();
    for station_id in payload.range_start..=payload.range_end {
        // 临时切换 unit_id:用 transact_tcp/udp 发请求,看是否成功
        // 由于 Session 的连接已绑定 unit_id,我们直接发 PDU,通过是否收到有效响应判断
        match session.probe_station(&payload.connection_id, station_id, &request_pdu) {
            Ok(response_ms) => {
                found.push(json!({
                    "stationId": station_id,
                    "firstResponseMs": response_ms,
                }));
            }
            Err(_) => { /* 超时或错误,跳过 */ }
        }
    }
    // 恢复默认超时
    let _ = session.set_connection_read_timeout_ms(&payload.connection_id, 0);
    let elapsed_ms = started_at.elapsed().as_millis();
    success(
        request_id.to_string(),
        json!({
            "found": found,
            "scanned": (payload.range_end as usize).saturating_sub(payload.range_start as usize) + 1,
            "elapsedMs": elapsed_ms,
        }),
        false,
    )
}

// --- 从站模拟 ---

fn handle_start_tcp_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: StartTcpSlavePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_tcp_slave(&payload.slave_id, payload.port, payload.allowed_station_ids) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "running": true, "slaveId": payload.slave_id, "port": payload.port }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: SlaveIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_slave(&payload.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "stopped": true, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_set_value(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveSetValuePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.slave_set_value(&payload.slave_id, &payload.area, payload.address, &payload.values)
    {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "set": true, "slaveId": payload.slave_id, "area": payload.area }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_set_coil(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveSetCoilPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.slave_set_coil(&payload.slave_id, &payload.area, payload.address, &payload.values)
    {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "set": true, "slaveId": payload.slave_id, "area": payload.area }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_clear(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveClearPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let area = payload.area.as_deref().unwrap_or("holding");
    match session.slave_clear(&payload.slave_id, area) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "cleared": true, "slaveId": payload.slave_id, "area": area }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

// --- 串口从站模拟 ---

fn handle_start_serial_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_serial_slave(&payload.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "running": true, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_serial_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_serial_slave(&payload.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "stopped": true, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_handle_serial_bytes(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveHandleSerialBytesPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.slave_handle_serial_bytes(&payload.slave_id, &payload.bytes) {
        Ok((should_respond, response_bytes)) => success(
            request_id.to_string(),
            json!({
                "shouldRespond": should_respond,
                "responseBytes": response_bytes,
                "responseHex": format_hex(&response_bytes),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_serial_slave_set_value(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SerialSlaveSetValuePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.serial_slave_set_value(&payload.slave_id, &payload.area, payload.address, &payload.values)
    {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "set": true, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_serial_slave_get_memory(
    session: &Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SerialSlaveGetMemoryPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.serial_slave_get_memory(&payload.slave_id, &payload.area, payload.address, payload.count)
    {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "values": values, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_get_memory(
    session: &Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveGetMemoryPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.slave_get_memory(&payload.slave_id, &payload.area, payload.address, payload.count)
    {
        Ok(values) => success(
            request_id.to_string(),
            json!({
                "values": values,
                "slaveId": payload.slave_id,
                "area": payload.area,
                "address": payload.address,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_compute_crc16(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ChecksumPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let crc = crc16_modbus(&payload.bytes);
    let bytes = crc.to_le_bytes();
    success(
        request_id.to_string(),
        json!({
            "crc": crc,
            "crcHex": format!("0x{:04X}", crc),
            "crcHexLo": format!("0x{:02X}", bytes[0]),
            "crcHexHi": format!("0x{:02X}", bytes[1]),
        }),
        false,
    )
}

fn handle_compute_lrc(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ChecksumPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let lrc = crate::modbus_ascii::compute_lrc(&payload.bytes);
    success(
        request_id.to_string(),
        json!({
            "lrc": lrc,
            "lrcHex": format!("0x{:02X}", lrc),
        }),
        false,
    )
}

fn handle_parse_frame_online(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseFrameOnlinePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let transport = payload.transport.as_str();
    // 解析帧
    let (unit_id, pdu_data, fc, checksum_status) = if transport == "ascii" {
        match crate::modbus_ascii::parse_ascii_frame(&payload.bytes) {
            Ok((uid, pdu)) => {
                let fc_val = pdu.first().copied().unwrap_or(0);
                (uid, pdu, fc_val, "valid")
            }
            Err(_) => (0, vec![], 0, "invalid"),
        }
    } else {
        // RTU 或 TCP(尝试 RTU 解析)
        match crate::modbus_rtu::RtuFrame::decode(
            &payload.bytes,
            crate::modbus_rtu::RtuFrameRole::Response,
        ) {
            Ok(frame) => {
                let mut pdu = vec![frame.function_code()];
                pdu.extend_from_slice(frame.data());
                (frame.unit_id(), pdu, frame.function_code(), "valid")
            }
            Err(_) => (0, vec![], 0, "invalid"),
        }
    };

    let function_name = fc_name(fc & 0x7F);
    let is_exception = fc & 0x80 != 0;
    success(
        request_id.to_string(),
        json!({
            "isValid": checksum_status == "valid",
            "transport": transport,
            "unitId": unit_id,
            "functionCode": fc,
            "functionName": function_name,
            "isException": is_exception,
            "checksumStatus": checksum_status,
            "summary": format!("站号{} FC{:02X} {} {}", unit_id, fc, function_name, if is_exception { "(异常)" } else { "" }),
        }),
        false,
    )
}

fn handle_parse_frame_offline(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        hex: Option<String>,
        bytes: Option<Vec<u8>>,
        #[serde(default = "default_transport")]
        transport: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let bytes = if let Some(ref hex) = p.hex {
        match crate::frame_parser::parse_hex_string(hex) {
            Ok(b) => b,
            Err(e) => {
                return failure(
                    Some(request_id.to_string()),
                    CoreError::InvalidSerialConfig {
                        field: "hex",
                        message: e,
                    },
                );
            }
        }
    } else if let Some(b) = p.bytes {
        b
    } else {
        return failure(
            Some(request_id.to_string()),
            CoreError::InvalidSerialConfig {
                field: "hex",
                message: "必须提供 hex 或 bytes".into(),
            },
        );
    };
    let info = crate::frame_parser::parse_frame(&bytes, &p.transport);
    success(request_id.to_string(), serde_json::to_value(info).unwrap_or(json!({})), false)
}

fn fc_name(fc: u8) -> &'static str {
    match fc {
        0x01 => "读线圈",
        0x02 => "读离散输入",
        0x03 => "读保持寄存器",
        0x04 => "读输入寄存器",
        0x05 => "写单线圈",
        0x06 => "写单寄存器",
        0x0F => "写多线圈",
        0x10 => "写多寄存器",
        0x16 => "屏蔽写寄存器",
        0x17 => "读写多寄存器",
        _ => "未知功能码",
    }
}

// --- 流式轮询(v2 协议) ---

fn handle_start_poll_stream(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: StartPollStreamPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_poll_stream(
        &payload.stream_id,
        &payload.connection_id,
        payload.fc,
        payload.start_address,
        payload.quantity,
        payload.interval_ms,
    ) {
        Ok(()) => {
            // 返回首个响应(带 streamId),后续推送由 serve 循环检查到期后发送
            let mut outcome = success(
                request_id.to_string(),
                json!({
                    "streamId": payload.stream_id,
                    "started": true,
                    "intervalMs": payload.interval_ms,
                }),
                false,
            );
            outcome.response.stream_id = Some(payload.stream_id.clone());
            outcome
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_poll_stream(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: StopPollStreamPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_poll_stream(&payload.stream_id) {
        Ok(()) => {
            let mut outcome = success(
                request_id.to_string(),
                json!({ "streamId": payload.stream_id, "stopped": true }),
                false,
            );
            outcome.response.stream_id = Some(payload.stream_id.clone());
            outcome.response.stream_end = Some(true);
            outcome
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// 检查 PDU 是否为异常响应,如果是返回对应的 failure outcome。
fn check_pdu_exception(pdu_bytes: &[u8], expected_fc: u8) -> Option<CommandOutcome> {
    if pdu_bytes.is_empty() {
        return None;
    }
    let fc = pdu_bytes[0];
    if fc & 0x80 != 0 {
        let exception_code = pdu_bytes.get(1).copied().unwrap_or(0);
        Some(success(
            String::new(), // 会由调用者覆盖
            json!({
                "status": "exception",
                "exceptionCode": exception_code,
                "exceptionName": modbus_exception_name(exception_code),
            }),
            false,
        ))
    } else {
        None
    }
}

// =============================================================================
// 辅助
// =============================================================================

fn format_hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<Vec<_>>()
        .join(" ")
}

fn format_read_registers_result(
    parsed: &modbus_rtu::ParsedReadHoldingRegistersResponse,
) -> Value {
    match parsed.exception_code {
        Some(code) => json!({
            "status": "exception",
            "exceptionCode": code,
            "exceptionName": modbus_exception_name(code),
            "registers": [],
        }),
        None => json!({
            "status": "ok",
            "exceptionCode": null,
            "exceptionName": null,
            "registers": parsed.registers,
        }),
    }
}

pub fn line_too_long() -> CommandOutcome {
    failure(None, CoreError::LineTooLong(crate::MAX_LINE_BYTES))
}

pub fn invalid_json() -> CommandOutcome {
    failure(None, CoreError::InvalidJson)
}

/// 构造一个流式推送帧(无 request_id,有 stream_id)。
/// 轮询流出错时的推送帧(带 stream_end=true,告知 JS 该流已终止)。
pub fn stream_error_outcome(stream_id: &str, e: &CoreError) -> CommandOutcome {
    let (code, message) = match e {
        CoreError::Modbus { code, message, .. } => (*code, message.clone()),
        other => ("POLL_STREAM_FAILED", other.to_string()),
    };
    CommandOutcome {
        response: ResponseEnvelope {
            protocol_version: PROTOCOL_VERSION,
            request_id: None,
            stream_id: Some(stream_id.to_string()),
            stream_end: Some(true),
            ok: false,
            result: None,
            error: Some(crate::error::ErrorBody {
                code,
                message,
                details: None,
            }),
        },
        shutdown: false,
    }
}

pub fn stream_push_outcome(stream_id: &str, result: Value) -> CommandOutcome {
    CommandOutcome {
        response: ResponseEnvelope {
            protocol_version: PROTOCOL_VERSION,
            request_id: None,
            stream_id: Some(stream_id.to_string()),
            stream_end: Some(false),
            ok: true,
            result: Some(result),
            error: None,
        },
        shutdown: false,
    }
}

fn success(request_id: String, result: Value, shutdown: bool) -> CommandOutcome {
    CommandOutcome {
        response: ResponseEnvelope {
            protocol_version: PROTOCOL_VERSION,
            request_id: Some(request_id),
            stream_id: None,
            stream_end: None,
            ok: true,
            result: Some(result),
            error: None,
        },
        shutdown,
    }
}

fn failure(request_id: Option<String>, error: CoreError) -> CommandOutcome {
    CommandOutcome {
        response: ResponseEnvelope {
            protocol_version: PROTOCOL_VERSION,
            request_id,
            stream_id: None,
            stream_end: None,
            ok: false,
            result: None,
            error: Some(error.body()),
        },
        shutdown: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run(session: &mut Session, line: &str) -> CommandOutcome {
        handle_line(session, line)
    }

    #[test]
    fn hello_reports_the_protocol_contract() {
        let mut session = Session::new();
        let outcome = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"request-1","command":"hello","payload":{}}"#,
        );
        assert!(outcome.response.ok);
        assert_eq!(outcome.response.request_id.as_deref(), Some("request-1"));
        let capabilities = outcome.response.result.unwrap()["capabilities"]
            .as_array()
            .unwrap()
            .clone();
        assert!(capabilities.contains(&json!("build_read_holding_registers")));
        assert!(capabilities.contains(&json!("tcp_write_multiple_registers")));
        assert!(capabilities.contains(&json!("build_write_single_coil")));
        assert!(!outcome.shutdown);
    }

    #[test]
    fn malformed_json_has_no_request_id() {
        let mut session = Session::new();
        let outcome = run(&mut session, "{");
        assert_eq!(outcome.response.request_id, None);
        assert_eq!(outcome.response.error.unwrap().code, "INVALID_JSON");
    }

    #[test]
    fn fc03_commands_build_and_parse_a_complete_transaction() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"build-1","command":"build_read_holding_registers","payload":{"unitId":1,"startAddress":0,"quantity":2}}"#,
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(result["adu"], json!([1, 3, 0, 0, 0, 2, 196, 11]));
        assert_eq!(result["expectedResponseLength"], 9);

        let mut response = vec![1, 3, 4, 0x12, 0x34, 0xAB, 0xCD];
        let crc = crc16_modbus(&response);
        response.extend_from_slice(&crc.to_le_bytes());
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "parse-1",
                "command": "parse_read_holding_registers",
                "payload": { "response": response, "unitId": 1, "quantity": 2 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], "ok");
        assert_eq!(result["registers"], json!([0x1234, 0xABCD]));
    }

    #[test]
    fn fc04_commands_build_and_parse_a_complete_transaction() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"build-4","command":"build_read_input_registers","payload":{"unitId":1,"startAddress":0,"quantity":2}}"#,
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(result["adu"], json!([1, 4, 0, 0, 0, 2, 113, 203]));
    }

    #[test]
    fn write_single_coil_build_command_produces_correct_adu() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"w1","command":"build_write_single_coil","payload":{"unitId":1,"address":10,"value":true}}"#,
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        let adu = result["adu"].as_array().unwrap();
        let adu_bytes: Vec<u8> = adu.iter().map(|v| v.as_u64().unwrap() as u8).collect();
        // unit=1, fc=0x05, addr=0x000A, value=0xFF00 + CRC
        assert_eq!(adu_bytes[0], 1);
        assert_eq!(adu_bytes[1], 0x05);
        assert_eq!(adu_bytes[2], 0);
        assert_eq!(adu_bytes[3], 10);
        assert_eq!(adu_bytes[4], 0xFF);
        assert_eq!(adu_bytes[5], 0x00);
        // 最后 2 字节是 CRC(非零)
        assert_eq!(adu_bytes.len(), 8);
    }

    #[test]
    fn broadcast_write_reports_no_expected_response() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"wb","command":"build_write_single_register","payload":{"unitId":0,"address":0,"value":1234}}"#,
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(result["expectResponse"], false);
    }

    // === ASCII 串口主站端到端(FC01-06,15,16)===

    fn adu_bytes(outcome: &CommandOutcome) -> Vec<u8> {
        outcome.response.result.as_ref().unwrap()["adu"]
            .as_array()
            .unwrap()
            .iter()
            .map(|v| v.as_u64().unwrap() as u8)
            .collect()
    }

    #[test]
    fn ascii_fc03_build_matches_canonical_vector() {
        // 标准向量:站号1 FC03 读地址0数量2 → :010300000002FA\r\n
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"a1","command":"build_ascii_read_holding_registers","payload":{"unitId":1,"startAddress":0,"quantity":2}}"#,
        );
        assert!(built.response.ok);
        assert_eq!(adu_bytes(&built), b":010300000002FA\r\n".to_vec());
    }

    #[test]
    fn ascii_fc03_round_trip_parses_registers() {
        let mut session = Session::new();
        // 响应:unit=1 FC=03 byte_count=4 数据 1234 ABCD
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "a2",
                "command": "parse_ascii_read_holding_registers",
                "payload": { "response": resp, "unitId": 1, "quantity": 2 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], "ok");
        assert_eq!(result["registers"], json!([0x1234, 0xABCD]));
    }

    #[test]
    fn ascii_fc04_read_input_registers_round_trip() {
        let mut session = Session::new();
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x04, 0x02, 0x00, 0x05]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "a4",
                "command": "parse_ascii_read_input_registers",
                "payload": { "response": resp, "unitId": 1, "quantity": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["registers"], json!([5]));
    }

    #[test]
    fn ascii_fc01_read_coils_round_trip() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"ac1","command":"build_ascii_read_coils","payload":{"unitId":1,"startAddress":0,"quantity":8}}"#,
        );
        assert!(built.response.ok);
        // 响应:unit=1 FC=01 byte_count=1 数据 0xA5(8 个线圈)
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x01, 0x01, 0xA5]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "ac1p",
                "command": "parse_ascii_read_coils",
                "payload": { "response": resp, "unitId": 1, "quantity": 8 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], "ok");
        // 0xA5 = 1010 0101(位顺序:低位在前)
        assert_eq!(result["coils"], json!([true, false, true, false, false, true, false, true]));
    }

    #[test]
    fn ascii_fc02_read_discrete_inputs_builds() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"ad2","command":"build_ascii_read_discrete_inputs","payload":{"unitId":2,"startAddress":10,"quantity":1}}"#,
        );
        assert!(built.response.ok);
        let bytes = adu_bytes(&built);
        // 帧应以 ':' 开头,CRLF 结尾,FC=02
        assert_eq!(bytes[0], b':');
        assert_eq!(&bytes[bytes.len() - 2..], b"\r\n");
        // : 02 02 00 0A 00 01 LRC
        assert_eq!(
            bytes,
            crate::modbus_ascii::build_ascii_frame(2, &[0x02, 0x00, 0x0A, 0x00, 0x01])
        );
    }

    #[test]
    fn ascii_fc05_write_single_coil_round_trip() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"aw5","command":"build_ascii_write_single_coil","payload":{"unitId":1,"address":10,"value":true}}"#,
        );
        assert!(built.response.ok);
        let bytes = adu_bytes(&built);
        assert_eq!(bytes[0], b':');
        // : 01 05 00 0A FF 00 LRC —— 与裸帧构造器一致
        assert_eq!(
            bytes,
            crate::modbus_ascii::build_ascii_frame(1, &[0x05, 0x00, 0x0A, 0xFF, 0x00])
        );
        // 响应回显
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x05, 0x00, 0x0A, 0xFF, 0x00]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "aw5p",
                "command": "parse_ascii_write_single_coil",
                "payload": { "response": resp, "unitId": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["address"], 10);
        assert_eq!(result["value"], true);
    }

    #[test]
    fn ascii_fc06_write_single_register_round_trip() {
        let mut session = Session::new();
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x06, 0x00, 0x05, 0x12, 0x34]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "aw6",
                "command": "parse_ascii_write_single_register",
                "payload": { "response": resp, "unitId": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["address"], 5);
        assert_eq!(result["value"], 0x1234);
    }

    #[test]
    fn ascii_fc10_write_multiple_registers_round_trip() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"aw10","command":"build_ascii_write_multiple_registers","payload":{"unitId":1,"address":0,"values":[1,2]}}"#,
        );
        assert!(built.response.ok);
        // 响应:unit=1 FC=10 addr=0 qty=2
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x10, 0x00, 0x00, 0x00, 0x02]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "aw10p",
                "command": "parse_ascii_write_multiple_registers",
                "payload": { "response": resp, "unitId": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["address"], 0);
        assert_eq!(result["quantity"], 2);
    }

    #[test]
    fn ascii_fc15_write_multiple_coils_builds() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"aw15","command":"build_ascii_write_multiple_coils","payload":{"unitId":1,"address":0,"values":[true,false,true]}}"#,
        );
        assert!(built.response.ok);
        let bytes = adu_bytes(&built);
        assert_eq!(bytes[0], b':');
        // : 01 0F 00 00 00 03 01 05 LRC(3 线圈 → 1 字节 0x05)
        assert_eq!(
            bytes,
            crate::modbus_ascii::build_ascii_frame(1, &[0x0F, 0x00, 0x00, 0x00, 0x03, 0x01, 0x05])
        );
    }

    #[test]
    fn ascii_parse_handles_exception_response() {
        let mut session = Session::new();
        // 从站返回异常:FC=0x83(FC03|0x80) + 异常码 0x02(非法数据地址)
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x83, 0x02]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "aexc",
                "command": "parse_ascii_read_holding_registers",
                "payload": { "response": resp, "unitId": 1, "quantity": 2 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], "exception");
        assert_eq!(result["exceptionCode"], 2);
        assert_eq!(result["registers"], json!([]));
    }

    #[test]
    fn ascii_parse_rejects_unit_id_mismatch() {
        let mut session = Session::new();
        let resp = crate::modbus_ascii::build_ascii_frame(2, &[0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "auid",
                "command": "parse_ascii_read_holding_registers",
                "payload": { "response": resp, "unitId": 1, "quantity": 2 }
            })
            .to_string(),
        );
        assert!(!parsed.response.ok);
        assert_eq!(parsed.response.error.unwrap().code, "UNIT_ID_MISMATCH");
    }

    /// FX Computer Link:fx_links_build 构造 + fx_links_parse 解析 NAK
    #[test]
    fn fx_links_commands_build_and_parse() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxl-1",
                "command": "fx_links_build",
                "payload": { "station": 0, "cmd": "WR", "delay": 0, "data": "D00C8000A" }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        // 文档 §3.2.4 示例:WR 读 D200 起 10 字
        assert_eq!(
            result["frame"],
            json!([0x05, 0x30, 0x30, 0x46, 0x46, 0x57, 0x52, 0x30, 0x44, 0x30, 0x30, 0x43,
                   0x38, 0x30, 0x30, 0x30, 0x41, 0x03, 0x42, 0x38, 0x0D, 0x0A])
        );
        assert_eq!(result["checksum"], json!("B8"));

        // NAK:NAK "00" 错误码 "06"
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxl-2",
                "command": "fx_links_parse",
                "payload": { "response": [0x15, 0x30, 0x30, 0x30, 0x36, 0x0D, 0x0A] }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("nak"));
        assert_eq!(result["errorCode"], json!(6));
    }

    /// MC 串口 C24:mc_serial_build_3c 构造(格式1 手算校验和 4D)+ mc_serial_parse_3c 还原
    #[test]
    fn mc_serial_commands_build_and_parse() {
        let mut session = Session::new();
        // mc_app_data = mc_pdu 读 D100 1 字的 3E 应用区(§2.1.4-(2))
        let app_data = [0x01, 0x04, 0x01, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00];
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "mcs-1",
                "command": "mc_serial_build_3c",
                "payload": { "format": "1", "station": 0, "mcAppData": app_data }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        // "00"(站号)+"01040100640000A80100"(报文体)+ETX+"4D"(和校验)+CR LF
        let mut expect: Vec<u8> = b"0001040100640000A80100".to_vec();
        expect.push(0x03);
        expect.extend_from_slice(b"4D");
        expect.push(0x0D);
        expect.push(0x0A);
        assert_eq!(result["frame"], json!(expect));
        assert_eq!(result["checksum"], json!("4D"), "手算:0x60+0x3EA+0x03=0x44D→4D");

        // 响应解析还原应用区(格式3 二进制 + 和校验)
        let resp_app: Vec<u8> = vec![0x00, 0x00, 0x34, 0x12];
        let resp_frame = crate::mc_serial::build_mc_serial_3c(
            0x0A,
            crate::mc_serial::McSerialFormat::Format3Binary,
            &resp_app,
        )
        .unwrap();
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "mcs-2",
                "command": "mc_serial_parse_3c",
                "payload": { "format": "3", "frame": resp_frame }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["station"], json!(0x0A));
        assert_eq!(result["mcAppData"], json!(resp_app));
        assert_eq!(result["mcAppDataHex"], json!("00 00 34 12"));

        // 能力表已注册
        let hello = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"mcs-cap","command":"hello","payload":{}}"#,
        );
        let caps = hello.response.result.unwrap()["capabilities"].as_array().unwrap().clone();
        assert!(caps.contains(&json!("mc_serial_build_3c")));
        assert!(caps.contains(&json!("mc_serial_parse_3c")));
        assert!(caps.contains(&json!("mc_1e_build_read")));

        // 非法格式拒绝
        let bad = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "mcs-3",
                "command": "mc_serial_build_3c",
                "payload": { "format": "2", "station": 0, "mcAppData": [0x01] }
            })
            .to_string(),
        );
        assert!(!bad.response.ok);
        assert_eq!(bad.response.error.unwrap().code, "MC_SERIAL_BAD_FORMAT");
    }

    /// A-1E:mc_1e_build_read 文档 §3.4.2 示例向量(字读 D100 起 12 点)
    #[test]
    fn mc_1e_build_read_matches_doc_vector() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-1",
                "command": "mc_1e_build_read",
                "payload": { "cmd": 1, "device": "D", "head": 100, "points": 12, "watchdog": 10 }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(result["frameHex"], json!("01 FF 0A 00 64 00 00 00 44 2A 0C 00"));
        assert_eq!(result["deviceCode"], json!("D*"));
        assert_eq!(
            result["frame"],
            json!([0x01, 0xFF, 0x0A, 0x00, 0x64, 0x00, 0x00, 0x00, 0x44, 0x2A, 0x0C, 0x00])
        );
    }

    /// A-1E:mc_1e_build_write(位打包)+ mc_1e_parse(字读/写确认/5BH 详细码)
    #[test]
    fn mc_1e_commands_write_and_parse() {
        let mut session = Session::new();
        // 位写 M0 起 3 点 [1,0,1] → 数据区 05 00(每 16 点 2 字节打包)
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-2",
                "command": "mc_1e_build_write",
                "payload": { "cmd": 2, "device": "M", "head": 0, "valuesBits": [1, 0, 1], "watchdog": 10 }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(result["frameHex"], json!("02 FF 0A 00 00 00 00 00 4D 2A 03 00 05 00"));

        // 字读响应:81 00 + 34 12 → D100=0x1234
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-3",
                "command": "mc_1e_parse",
                "payload": { "frame": [0x81, 0x00, 0x34, 0x12], "cmd": 1, "points": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("words"));
        assert_eq!(result["values"], json!([0x1234]));

        // 位读响应:81 00 05 00 → 前 3 位 [1,0,1]
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-4",
                "command": "mc_1e_parse",
                "payload": { "frame": [0x81, 0x00, 0x05, 0x00], "cmd": 0, "points": 3 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("bits"));
        assert_eq!(result["values"], json!([1, 0, 1]));

        // 写确认:81 00
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-5",
                "command": "mc_1e_parse",
                "payload": { "frame": [0x81, 0x00], "cmd": 3, "points": 0 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        assert_eq!(parsed.response.result.unwrap()["status"], json!("writeAck"));

        // 异常:5BH + 详细代码 11H(软元件代码异常)
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-6",
                "command": "mc_1e_parse",
                "payload": { "frame": [0x81, 0x5B, 0x11, 0x00], "cmd": 1, "points": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("error"));
        assert_eq!(result["errorCode"], json!(0x5B));
        assert_eq!(result["detailCode"], json!(0x11));
        assert!(result["message"].as_str().unwrap().contains("软元件代码"));

        // 位/字类别不匹配拒绝(字读命令配位元件)
        let bad = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-7",
                "command": "mc_1e_build_read",
                "payload": { "cmd": 1, "device": "M", "head": 0, "points": 1, "watchdog": 10 }
            })
            .to_string(),
        );
        assert!(!bad.response.ok);
        assert_eq!(bad.response.error.unwrap().code, "MC_1E_DEVICE_CLASS_MISMATCH");
    }

    /// FX 编程口:fx_prog_build_read 构造(文档 §3.3.5(1) 向量)+ fx_prog_parse 解析
    #[test]
    fn fx_prog_commands_build_and_parse() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxp-1",
                "command": "fx_prog_build_read",
                "payload": { "device": "D", "address": "123", "words": 2 }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(
            result["frame"],
            json!([0x02, 0x30, 0x31, 0x30, 0x46, 0x36, 0x30, 0x34, 0x03, 0x37, 0x34])
        );

        // 读响应:数据 "3412"(D123 = 0x1234,低字节在前)
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxp-2",
                "command": "fx_prog_parse",
                "payload": { "frame": [0x02, 0x33, 0x34, 0x31, 0x32, 0x03, 0x43, 0x44] }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("data"));
        assert_eq!(result["dataAscii"], json!("3412"));
        assert_eq!(result["words"], json!([0x1234]));

        // ACK(写成功)
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxp-3",
                "command": "fx_prog_parse",
                "payload": { "frame": [0x06] }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        assert_eq!(
            parsed.response.result.unwrap()["status"],
            json!("ack")
        );
    }
}
