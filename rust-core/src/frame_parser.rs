//! 离线报文解析器 —— 对标 .NET Nexus 的 `ModbusPacketParser`。
//!
//! 输入 hex 字符串(或字节序列)+ 传输方式,输出结构化的帧信息。
//! 支持 RTU / ASCII / TCP 三种传输,自动推断方向,验证校验和。

use serde::Serialize;

use crate::modbus_rtu::{self, RtuFrame, RtuFrameRole};
use crate::modbus_ascii;
use crate::modbus_tcp;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameInfo {
    pub is_valid: bool,
    pub transport: String,
    pub direction: String, // "request" | "response" | "unknown"
    pub unit_id: u16,
    pub function_code: u8,
    pub function_name: String,
    pub base_function_code: u8,
    pub is_exception: bool,
    pub exception_code: Option<u8>,
    pub exception_name: Option<String>,
    pub address: Option<u16>,
    pub quantity: Option<u16>,
    pub write_address: Option<u16>,
    pub byte_count: Option<u8>,
    pub data: Vec<u8>,
    pub registers: Vec<u16>,
    pub coils: Vec<bool>,
    pub checksum_status: String, // "valid" | "invalid" | "not_applicable"
    pub checksum: Option<String>,
    pub summary: String,
    pub error: Option<String>,
}

/// 解析 hex 字符串为字节序列。
pub fn parse_hex_string(hex: &str) -> Result<Vec<u8>, String> {
    let cleaned = hex.replace([' ', ',', ':', '\t', '\n', '\r'], "");
    if cleaned.is_empty() {
        return Err("输入为空".to_string());
    }
    if cleaned.len() % 2 != 0 {
        return Err(format!("hex 长度 {} 不是偶数", cleaned.len()));
    }
    (0..cleaned.len())
        .step_by(2)
        .map(|i| {
            u8::from_str_radix(&cleaned[i..i + 2], 16)
                .map_err(|e| format!("hex 解析失败 @ {i}: {e}"))
        })
        .collect()
}

/// 解析一帧报文。
pub fn parse_frame(bytes: &[u8], transport: &str) -> FrameInfo {
    match transport.to_lowercase().as_str() {
        "rtu" => parse_rtu(bytes),
        "ascii" => parse_ascii(bytes),
        "tcp" => parse_tcp(bytes),
        "auto" => infer_and_parse(bytes),
        _ => parse_rtu(bytes),
    }
}

fn parse_rtu(bytes: &[u8]) -> FrameInfo {
    // 尝试作为响应解析
    match RtuFrame::decode(bytes, RtuFrameRole::Response) {
        Ok(frame) => build_info_from_rtu(frame, bytes, "valid", "rtu"),
        Err(_) => {
            // 尝试作为请求解析
            match RtuFrame::decode(bytes, RtuFrameRole::Request) {
                Ok(frame) => build_info_from_rtu(frame, bytes, "valid", "rtu"),
                Err(e) => invalid_frame("rtu", bytes, &e.to_string()),
            }
        }
    }
}

fn parse_ascii(bytes: &[u8]) -> FrameInfo {
    match modbus_ascii::parse_ascii_frame(bytes) {
        Ok((unit_id, pdu)) => {
            let fc = pdu.first().copied().unwrap_or(0);
            let info = build_info_from_pdu(unit_id as u16, &pdu, "ascii", "valid");
            info
        }
        Err(e) => invalid_frame("ascii", bytes, &e.to_string()),
    }
}

fn parse_tcp(bytes: &[u8]) -> FrameInfo {
    match modbus_tcp::parse_mbap_frame(bytes) {
        Ok((header, pdu)) => {
            let mut info = build_info_from_pdu(header.unit_id as u16, &pdu, "tcp", "not_applicable");
            info.unit_id = header.unit_id as u16;
            info
        }
        Err(e) => invalid_frame("tcp", bytes, &e.to_string()),
    }
}

fn infer_and_parse(bytes: &[u8]) -> FrameInfo {
    // 启发式:如果以 ':' 开头(0x3A),是 ASCII
    if bytes.first() == Some(&b':') {
        return parse_ascii(bytes);
    }
    // 如果长度 >= 8 且 protocol_id 字段(bytes[2..3])为 0,可能是 TCP
    if bytes.len() >= 8 {
        let proto_id = u16::from_be_bytes([bytes[2], bytes[3]]);
        if proto_id == 0 {
            return parse_tcp(bytes);
        }
    }
    // 否则尝试 RTU
    parse_rtu(bytes)
}

fn build_info_from_rtu(frame: RtuFrame, raw: &[u8], checksum: &str, transport: &str) -> FrameInfo {
    let fc = frame.function_code();
    let base_fc = fc & 0x7F;
    let is_exception = frame.is_exception();
    let direction = if frame.role() == RtuFrameRole::Request {
        "request"
    } else {
        "response"
    };
    let data = frame.data().to_vec();
    let crc = modbus_rtu::crc16_modbus(&raw[..raw.len() - 2]);
    FrameInfo {
        is_valid: true,
        transport: transport.to_string(),
        direction: direction.to_string(),
        unit_id: frame.unit_id() as u16,
        function_code: fc,
        function_name: fc_name(base_fc).to_string(),
        base_function_code: base_fc,
        is_exception,
        exception_code: frame.exception_code(),
        exception_name: frame.exception_code().map(modbus_rtu::modbus_exception_name).map(String::from),
        address: extract_address(base_fc, &data),
        quantity: extract_quantity(base_fc, &data),
        write_address: None,
        byte_count: data.first().copied(),
        data: data.clone(),
        registers: extract_registers(base_fc, &data, is_exception),
        coils: extract_coils(base_fc, &data, is_exception),
        checksum_status: checksum.to_string(),
        checksum: Some(format!("0x{:04X}", crc)),
        summary: format_summary(frame.unit_id(), fc, base_fc, is_exception, &data, transport),
        error: None,
    }
}

fn build_info_from_pdu(unit_id: u16, pdu: &[u8], transport: &str, checksum: &str) -> FrameInfo {
    if pdu.is_empty() {
        return invalid_frame(transport, pdu, "PDU 为空");
    }
    let fc = pdu[0];
    let base_fc = fc & 0x7F;
    let is_exception = fc & 0x80 != 0;
    let data = &pdu[1..];
    FrameInfo {
        is_valid: true,
        transport: transport.to_string(),
        direction: "unknown".to_string(),
        unit_id,
        function_code: fc,
        function_name: fc_name(base_fc).to_string(),
        base_function_code: base_fc,
        is_exception,
        exception_code: if is_exception { data.first().copied() } else { None },
        exception_name: if is_exception {
            data.first().map(|&c| modbus_rtu::modbus_exception_name(c).to_string())
        } else {
            None
        },
        address: extract_address(base_fc, data),
        quantity: extract_quantity(base_fc, data),
        write_address: None,
        byte_count: data.first().copied(),
        data: data.to_vec(),
        registers: extract_registers(base_fc, data, is_exception),
        coils: extract_coils(base_fc, data, is_exception),
        checksum_status: checksum.to_string(),
        checksum: None,
        summary: format_summary(unit_id as u8, fc, base_fc, is_exception, data, transport),
        error: None,
    }
}

fn invalid_frame(transport: &str, bytes: &[u8], error: &str) -> FrameInfo {
    FrameInfo {
        is_valid: false,
        transport: transport.to_string(),
        direction: "unknown".to_string(),
        unit_id: 0,
        function_code: 0,
        function_name: "无效".to_string(),
        base_function_code: 0,
        is_exception: false,
        exception_code: None,
        exception_name: None,
        address: None,
        quantity: None,
        write_address: None,
        byte_count: None,
        data: bytes.to_vec(),
        registers: vec![],
        coils: vec![],
        checksum_status: "invalid".to_string(),
        checksum: None,
        summary: format!("解析失败: {error}"),
        error: Some(error.to_string()),
    }
}

fn fc_name(fc: u8) -> &'static str {
    match fc {
        0x01 => "读线圈",
        0x02 => "读离散输入",
        0x03 => "读保持寄存器",
        0x04 => "读输入寄存器",
        0x05 => "写单线圈",
        0x06 => "写单寄存器",
        0x08 => "诊断",
        0x0B => "获取通信事件计数",
        0x0C => "获取通信事件日志",
        0x0F => "写多线圈",
        0x10 => "写多寄存器",
        0x11 => "报告从站 ID",
        0x16 => "屏蔽写寄存器",
        0x17 => "读写多寄存器",
        0x2B => "读设备标识",
        _ => "未知",
    }
}

fn extract_address(fc: u8, data: &[u8]) -> Option<u16> {
    if data.len() >= 2 && matches!(fc, 0x01..=0x06 | 0x0F | 0x10 | 0x16 | 0x17) {
        Some(u16::from_be_bytes([data[0], data[1]]))
    } else {
        None
    }
}

fn extract_quantity(fc: u8, data: &[u8]) -> Option<u16> {
    if data.len() >= 4 && matches!(fc, 0x01..=0x04 | 0x0F | 0x10) {
        Some(u16::from_be_bytes([data[2], data[3]]))
    } else {
        None
    }
}

fn extract_registers(fc: u8, data: &[u8], is_exception: bool) -> Vec<u16> {
    if is_exception || !matches!(fc, 0x03 | 0x04) {
        return vec![];
    }
    if data.len() < 2 {
        return vec![];
    }
    let byte_count = data[0] as usize;
    if data.len() < 1 + byte_count {
        return vec![];
    }
    data[1..1 + byte_count]
        .chunks_exact(2)
        .map(|c| u16::from_be_bytes([c[0], c[1]]))
        .collect()
}

fn extract_coils(fc: u8, data: &[u8], is_exception: bool) -> Vec<bool> {
    if is_exception || !matches!(fc, 0x01 | 0x02) {
        return vec![];
    }
    if data.is_empty() {
        return vec![];
    }
    let byte_count = data[0] as usize;
    if data.len() < 1 + byte_count {
        return vec![];
    }
    let mut bits = Vec::new();
    for &byte in &data[1..1 + byte_count] {
        for bit in 0..8 {
            bits.push(byte & (1 << bit) != 0);
        }
    }
    bits
}

fn format_summary(unit_id: u8, fc: u8, base_fc: u8, is_exception: bool, data: &[u8], transport: &str) -> String {
    let name = fc_name(base_fc);
    let exc = if is_exception {
        let code = data.first().copied().unwrap_or(0);
        format!(" (异常 0x{:02X} {})", code, modbus_rtu::modbus_exception_name(code))
    } else {
        String::new()
    };
    format!("[{}] 站号 {} FC 0x{:02X} {}{}", transport.to_uppercase(), unit_id, fc, name, exc)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_hex_string_works() {
        assert_eq!(parse_hex_string("01 03").unwrap(), vec![0x01, 0x03]);
        assert_eq!(parse_hex_string("0103").unwrap(), vec![0x01, 0x03]);
        assert!(parse_hex_string("").is_err());
        assert!(parse_hex_string("abc").is_err()); // 奇数长度
    }

    #[test]
    fn parse_rtu_fc03_request() {
        // FC03 读请求:01 03 00 00 00 0A + CRC(C5 CD)
        let hex = "01 03 00 00 00 0A C5 CD";
        let bytes = parse_hex_string(hex).unwrap();
        let info = parse_frame(&bytes, "rtu");
        assert!(info.is_valid);
        assert_eq!(info.base_function_code, 0x03);
        assert_eq!(info.address, Some(0));
        assert_eq!(info.quantity, Some(10));
    }

    #[test]
    fn parse_rtu_fc03_response_with_registers() {
        // FC03 响应:01 03 04 12 34 AB CD + CRC
        let raw = [0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD];
        let crc = modbus_rtu::crc16_modbus(&raw);
        let mut bytes = raw.to_vec();
        bytes.extend_from_slice(&crc.to_le_bytes());
        let info = parse_frame(&bytes, "rtu");
        assert!(info.is_valid);
        assert_eq!(info.registers, vec![0x1234, 0xABCD]);
    }

    #[test]
    fn parse_rtu_exception() {
        // 异常响应:01 83 02 + CRC
        let raw = [0x01, 0x83, 0x02];
        let crc = modbus_rtu::crc16_modbus(&raw);
        let mut bytes = raw.to_vec();
        bytes.extend_from_slice(&crc.to_le_bytes());
        let info = parse_frame(&bytes, "rtu");
        assert!(info.is_valid);
        assert!(info.is_exception);
        assert_eq!(info.exception_code, Some(0x02));
    }

    #[test]
    fn parse_tcp_frame() {
        // MBAP + FC03: TID=0001 PID=0000 LEN=0006 UID=01 FC=03 addr=0000 qty=000A
        let bytes = [0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A];
        let info = parse_frame(&bytes, "tcp");
        assert!(info.is_valid);
        assert_eq!(info.unit_id, 1);
        assert_eq!(info.base_function_code, 0x03);
        assert_eq!(info.checksum_status, "not_applicable");
    }

    #[test]
    fn parse_ascii_frame() {
        // :01030000000AF2 + CRLF
        let ascii = b":01030000000AF2\r\n";
        let info = parse_frame(ascii, "ascii");
        assert!(info.is_valid);
        assert_eq!(info.unit_id, 1);
        assert_eq!(info.base_function_code, 0x03);
    }

    #[test]
    fn invalid_frame_reports_error() {
        let bytes = [0x01, 0x02, 0x03]; // 太短
        let info = parse_frame(&bytes, "rtu");
        assert!(!info.is_valid);
        assert!(info.error.is_some());
    }

    #[test]
    fn infer_ascii_from_colon_prefix() {
        let ascii = b":01030000000AF2\r\n";
        let bytes: Vec<u8> = ascii.to_vec();
        let info = parse_frame(&bytes, "auto");
        assert!(info.is_valid);
        assert_eq!(info.transport, "ascii");
    }

    #[test]
    fn infer_tcp_from_protocol_id_zero() {
        let bytes = [0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A];
        let info = parse_frame(&bytes, "auto");
        assert!(info.is_valid);
        assert_eq!(info.transport, "tcp");
    }
}
