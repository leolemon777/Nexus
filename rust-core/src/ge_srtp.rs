//! GE Series 90 / PACSystems SRTP binary codec.
//!
//! This is the software-first boundary retained from the audited GE client:
//! a 56-byte zero session request, explicit transaction association, and the
//! 56-byte read/write envelope.  It deliberately does not open TCP, infer PLC
//! state, expose program/clock services, or provide a virtual PLC.
//!
//! The memory data codes and envelope layout are derived from the audited
//! HslCommunication reference (MIT); see `Nexus/docs/audit/ge-srtp.md` and the
//! repository NOTICE files for attribution.

use crate::error::CoreError;

pub const HEADER_BYTES: usize = 56;
pub const DEFAULT_PORT: u16 = 18_245;
pub const READ: u8 = 0x04;
pub const WRITE: u8 = 0x02;
pub const RESPONSE_LONG: u8 = 0x94;
pub const RESPONSE_SHORT: u8 = 0xD4;

fn error(
    code: &'static str,
    message: impl Into<String>,
    details: Option<serde_json::Value>,
) -> CoreError {
    CoreError::Modbus {
        code,
        message: message.into(),
        details,
    }
}

fn invalid(message: impl Into<String>, details: serde_json::Value) -> CoreError {
    error("GE_SRTP_PARAM_INVALID", message, Some(details))
}

fn frame_error(message: impl Into<String>) -> CoreError {
    error("GE_SRTP_FRAME_INVALID", message, None)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Address {
    pub area: String,
    pub canonical: String,
    pub byte_data_code: u8,
    pub bit_data_code: Option<u8>,
    pub is_word_area: bool,
    pub offset: u16,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HandshakeResponse {
    pub response_type: u8,
    pub marker: u8,
    pub payload_length: usize,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Response {
    pub transaction_id: u16,
    pub response_form: &'static str,
    pub declared_length: usize,
    pub plc_status: Option<u16>,
    pub data: Vec<u8>,
}

pub fn parse_address(input: &str) -> Result<Address, CoreError> {
    let mut value = input.trim().to_ascii_uppercase();
    if let Some(stripped) = value.strip_prefix('%') {
        value = stripped.to_string();
    }
    if value.is_empty() {
        return Err(invalid(
            "GE SRTP 地址不能为空",
            serde_json::json!({ "address": input }),
        ));
    }

    let (area, digits) = if let Some(rest) = value.strip_prefix("AI") {
        ("AI", rest)
    } else if let Some(rest) = value.strip_prefix("AQ") {
        ("AQ", rest)
    } else if let Some(rest) = value.strip_prefix("SA") {
        ("SA", rest)
    } else if let Some(rest) = value.strip_prefix("SB") {
        ("SB", rest)
    } else if let Some(rest) = value.strip_prefix("SC") {
        ("SC", rest)
    } else {
        let mut chars = value.chars();
        let prefix = chars.next().unwrap_or_default();
        let rest = chars.as_str();
        let area = match prefix {
            'R' | 'I' | 'Q' | 'T' | 'M' | 'S' | 'G' => prefix.to_string(),
            _ => {
                return Err(invalid(
                    "GE SRTP 地址区必须是 R、AI、AQ、I、Q、T、M、SA、SB、SC、S 或 G",
                    serde_json::json!({ "address": input }),
                ));
            }
        };
        return parse_area_digits(&area, rest, input);
    };
    parse_area_digits(area, digits, input)
}

fn parse_area_digits(area: &str, digits: &str, input: &str) -> Result<Address, CoreError> {
    let reference = digits.parse::<u32>().map_err(|_| {
        invalid(
            "GE SRTP 地址编号必须是十进制数字",
            serde_json::json!({ "address": input }),
        )
    })?;
    if !(1..=65_536).contains(&reference) {
        return Err(invalid(
            "GE SRTP 用户地址范围是 1..65536；R0/M0 等地址拒绝",
            serde_json::json!({ "address": input, "reference": reference }),
        ));
    }

    let (byte_data_code, bit_data_code, is_word_area) = match area {
        "R" => (0x08, None, true),
        "AI" => (0x0A, None, true),
        "AQ" => (0x0C, None, true),
        "I" => (0x10, Some(0x46), false),
        "Q" => (0x12, Some(0x48), false),
        "T" => (0x14, Some(0x4A), false),
        "M" => (0x16, Some(0x4C), false),
        "SA" => (0x18, Some(0x4E), false),
        "SB" => (0x1A, Some(0x50), false),
        "SC" => (0x1C, Some(0x52), false),
        "S" => (0x1E, Some(0x54), false),
        "G" => (0x38, Some(0x56), false),
        _ => {
            return Err(invalid(
                "GE SRTP 地址区不受首轮 profile 支持",
                serde_json::json!({ "address": input }),
            ));
        }
    };
    let offset = (reference - 1) as u16;
    Ok(Address {
        area: area.to_string(),
        canonical: format!("{area}{reference}"),
        byte_data_code,
        bit_data_code,
        is_word_area,
        offset,
    })
}

pub fn build_handshake() -> Vec<u8> {
    vec![0; HEADER_BYTES]
}

pub fn parse_handshake(response: &[u8]) -> Result<HandshakeResponse, CoreError> {
    if response.len() != HEADER_BYTES {
        return Err(frame_error("GE SRTP 会话初始化响应必须恰好是 56 字节"));
    }
    if response[0] != 0x01 || response[8] != 0x0F {
        return Err(frame_error("GE SRTP 会话初始化响应标识无效"));
    }
    let payload_length = u16::from_le_bytes([response[4], response[5]]) as usize;
    if payload_length != 0 {
        return Err(frame_error("GE SRTP 会话初始化响应包含意外负载"));
    }
    Ok(HandshakeResponse {
        response_type: response[0],
        marker: response[8],
        payload_length,
    })
}

fn validate_range(
    address: &Address,
    element_count: u16,
    bit_access: bool,
) -> Result<(), CoreError> {
    if element_count == 0 {
        return Err(invalid(
            "GE SRTP 元素数量必须大于 0",
            serde_json::json!({ "elementCount": element_count }),
        ));
    }
    if bit_access && address.bit_data_code.is_none() {
        return Err(invalid(
            format!("{} 区不支持位访问", address.area),
            serde_json::json!({ "area": address.area, "bitAccess": true }),
        ));
    }
    let last = u64::from(address.offset) + u64::from(element_count) - 1;
    if last > u64::from(u16::MAX) {
        return Err(invalid(
            "GE SRTP 地址与元素数量超出 16 位地址范围",
            serde_json::json!({ "address": address.canonical, "elementCount": element_count }),
        ));
    }
    Ok(())
}

/// Returns the number of response bytes for a read using the parsed address
/// and explicit element count.  Word areas count 16-bit words; byte and bit
/// areas use their respective element units.
pub fn expected_data_bytes(address: &Address, element_count: u16, bit_access: bool) -> usize {
    if bit_access {
        (usize::from(address.offset % 8) + usize::from(element_count)).div_ceil(8)
    } else if address.is_word_area {
        usize::from(element_count) * 2
    } else {
        usize::from(element_count)
    }
}

/// Parses an address and returns the exact response-byte budget for a read.
/// Keeping this calculation in the codec prevents the live TCP path from
/// inventing a second word/byte/bit sizing rule.
pub fn expected_read_data_bytes(
    address: &str,
    element_count: u16,
    bit_access: bool,
) -> Result<usize, CoreError> {
    let parsed = parse_address(address)?;
    validate_range(&parsed, element_count, bit_access)?;
    Ok(expected_data_bytes(&parsed, element_count, bit_access))
}

fn set_common_read_header(frame: &mut [u8], transaction_id: u16) {
    frame[0] = 0x02;
    frame[2..4].copy_from_slice(&transaction_id.to_le_bytes());
    frame[9] = 0x01;
    frame[17] = 0x01;
    frame[30] = 0x06;
    frame[31] = 0xC0;
    frame[36] = 0x10;
    frame[37] = 0x0E;
    frame[40] = 0x01;
    frame[41] = 0x01;
}

pub fn build_read(
    transaction_id: u16,
    address: &str,
    element_count: u16,
    bit_access: bool,
) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(address)?;
    validate_range(&parsed, element_count, bit_access)?;
    let data_code = if bit_access {
        parsed.bit_data_code.expect("validated bit data code")
    } else {
        parsed.byte_data_code
    };
    let mut frame = vec![0u8; HEADER_BYTES];
    set_common_read_header(&mut frame, transaction_id);
    frame[42] = READ;
    frame[43] = data_code;
    frame[44..46].copy_from_slice(&parsed.offset.to_le_bytes());
    frame[46..48].copy_from_slice(&element_count.to_le_bytes());
    Ok(frame)
}

pub fn build_write(
    transaction_id: u16,
    address: &str,
    data: &[u8],
    element_count: u16,
    bit_access: bool,
) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(address)?;
    validate_range(&parsed, element_count, bit_access)?;
    if data.is_empty() {
        return Err(invalid(
            "GE SRTP 写入数据不能为空",
            serde_json::json!({ "bytes": data.len() }),
        ));
    }
    if parsed.is_word_area && !bit_access && data.len() % 2 != 0 {
        return Err(invalid(
            format!("{} 区按 16 位字访问，写入字节数必须为偶数", parsed.area),
            serde_json::json!({ "bytes": data.len() }),
        ));
    }
    let expected = expected_data_bytes(&parsed, element_count, bit_access);
    if data.len() != expected {
        return Err(invalid(
            format!(
                "GE SRTP 写入数据长度不符，期望 {expected} 字节，实际 {}",
                data.len()
            ),
            serde_json::json!({ "expectedBytes": expected, "actualBytes": data.len() }),
        ));
    }
    if data.len() > u16::MAX as usize {
        return Err(invalid(
            "GE SRTP 单帧写入负载不能超过 65535 字节",
            serde_json::json!({ "bytes": data.len() }),
        ));
    }
    let data_code = if bit_access {
        parsed.bit_data_code.expect("validated bit data code")
    } else {
        parsed.byte_data_code
    };
    let mut frame = vec![0u8; HEADER_BYTES + data.len()];
    frame[0] = 0x02;
    frame[2..4].copy_from_slice(&transaction_id.to_le_bytes());
    frame[4..6].copy_from_slice(&(data.len() as u16).to_le_bytes());
    frame[9] = 0x02;
    frame[17] = 0x02;
    frame[30] = 0x09;
    frame[31] = 0x80;
    frame[36] = 0x10;
    frame[37] = 0x0E;
    frame[40] = 0x01;
    frame[41] = 0x01;
    frame[42] = WRITE;
    frame[48] = 0x01;
    frame[49] = 0x01;
    frame[50] = 0x07;
    frame[51] = data_code;
    frame[52..54].copy_from_slice(&parsed.offset.to_le_bytes());
    frame[54..56].copy_from_slice(&element_count.to_le_bytes());
    frame[HEADER_BYTES..].copy_from_slice(data);
    Ok(frame)
}

pub fn parse_response(
    response: &[u8],
    transaction_id: u16,
    expected_data_length: usize,
) -> Result<Response, CoreError> {
    if response.len() < HEADER_BYTES {
        return Err(frame_error("GE SRTP 响应短于 56 字节报头"));
    }
    if response[0] != 0x03 {
        return Err(error(
            "GE_SRTP_RESPONSE_TYPE_MISMATCH",
            format!("GE SRTP 响应类型无效: 0x{:02X}", response[0]),
            None,
        ));
    }
    let received_id = u16::from_le_bytes([response[2], response[3]]);
    if received_id != transaction_id {
        return Err(error(
            "GE_SRTP_TRANSACTION_MISMATCH",
            "GE SRTP 响应事务号与请求不一致",
            Some(serde_json::json!({ "expected": transaction_id, "received": received_id })),
        ));
    }
    let declared_length = u16::from_le_bytes([response[4], response[5]]) as usize;
    if declared_length != response.len() - HEADER_BYTES {
        return Err(error(
            "GE_SRTP_LENGTH_MISMATCH",
            "GE SRTP 响应长度字段与实际报文不一致",
            Some(
                serde_json::json!({ "declared": declared_length, "actual": response.len() - HEADER_BYTES }),
            ),
        ));
    }

    match response[31] {
        RESPONSE_SHORT => {
            if expected_data_length > 6 || response.len() < 44 + expected_data_length {
                return Err(error(
                    "GE_SRTP_SHORT_RESPONSE_TOO_SMALL",
                    "GE SRTP 短响应最多只能携带 6 字节数据",
                    Some(serde_json::json!({ "expectedDataLength": expected_data_length })),
                ));
            }
            let status = u16::from_le_bytes([response[42], response[43]]);
            if status != 0 {
                return Err(error(
                    "GE_SRTP_DEVICE_ERROR",
                    format!("GE SRTP PLC 返回错误 0x{status:04X}"),
                    Some(serde_json::json!({ "status": status })),
                ));
            }
            Ok(Response {
                transaction_id,
                response_form: "short",
                declared_length,
                plc_status: Some(status),
                data: response[44..44 + expected_data_length].to_vec(),
            })
        }
        RESPONSE_LONG => {
            if declared_length != expected_data_length {
                return Err(error(
                    "GE_SRTP_DATA_LENGTH_MISMATCH",
                    format!(
                        "GE SRTP 长响应数据长度不符，期望 {expected_data_length}，实际 {declared_length}"
                    ),
                    None,
                ));
            }
            Ok(Response {
                transaction_id,
                response_form: "long",
                declared_length,
                plc_status: None,
                data: response[HEADER_BYTES..].to_vec(),
            })
        }
        form => Err(error(
            "GE_SRTP_RESPONSE_FORM_INVALID",
            format!("未知的 GE SRTP 响应格式 0x{form:02X}"),
            None,
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_one_based_areas_and_builds_read_header() {
        let address = parse_address("%M1").unwrap();
        assert_eq!(address.offset, 0);
        assert_eq!(address.bit_data_code, Some(0x4C));
        assert!(parse_address("R0").is_err());
        let frame = build_read(0x1234, "AI10", 2, false).unwrap();
        assert_eq!(frame.len(), HEADER_BYTES);
        assert_eq!(&frame[2..4], &[0x34, 0x12]);
        assert_eq!(&frame[42..48], &[READ, 0x0A, 0x09, 0x00, 0x02, 0x00]);
    }

    #[test]
    fn builds_write_and_parses_short_and_long_responses() {
        let frame = build_write(7, "M1", &[0xAA, 0x55], 2, false).unwrap();
        assert_eq!(frame.len(), HEADER_BYTES + 2);
        assert_eq!(&frame[4..6], &[2, 0]);
        let mut short = vec![0u8; HEADER_BYTES];
        short[0] = 0x03;
        short[2..4].copy_from_slice(&7u16.to_le_bytes());
        short[31] = RESPONSE_SHORT;
        short[44..46].copy_from_slice(&[0x34, 0x12]);
        let parsed = parse_response(&short, 7, 2).unwrap();
        assert_eq!(parsed.data, vec![0x34, 0x12]);

        let mut long = vec![0u8; HEADER_BYTES + 2];
        long[0] = 0x03;
        long[2..4].copy_from_slice(&8u16.to_le_bytes());
        long[4..6].copy_from_slice(&2u16.to_le_bytes());
        long[31] = RESPONSE_LONG;
        long[56..].copy_from_slice(&[0x78, 0x56]);
        let parsed = parse_response(&long, 8, 2).unwrap();
        assert_eq!(parsed.data, vec![0x78, 0x56]);
    }
}
