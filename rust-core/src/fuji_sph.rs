//! Fuji Electric MICREX-SX SPH Loader Command binary codec.
//!
//! This is the software-first boundary retained from the audited SPH
//! implementation: a 20-byte header, command 00/01, 24-bit little-endian
//! word address and 16-bit little-endian word count.  It deliberately does
//! not open TCP, infer CPU state, or expose bit writes.

use crate::error::CoreError;

pub const HEADER_BYTES: usize = 20;
pub const FRAME_BYTES: usize = 26;
pub const DEFAULT_PORT: u16 = 18_245;
pub const MAX_WORDS: u16 = 230;
pub const READ: u8 = 0x00;
pub const WRITE: u8 = 0x01;

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
    error("FUJI_SPH_PARAM_INVALID", message, Some(details))
}

fn frame_error(message: impl Into<String>) -> CoreError {
    error("FUJI_SPH_FRAME_INVALID", message, None)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Address {
    pub area: String,
    pub type_code: u8,
    pub word_address: u32,
    pub bit_index: Option<u8>,
    pub canonical: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Response {
    pub connection_id: u8,
    pub command: u8,
    pub error_code: u8,
    pub type_code: u8,
    pub word_address: u32,
    pub words: u16,
    pub data: Vec<u8>,
}

pub fn parse_address(input: &str) -> Result<Address, CoreError> {
    let value = input.trim().to_ascii_uppercase();
    if value.is_empty() {
        return Err(invalid(
            "SPH 地址不能为空",
            serde_json::json!({ "address": input }),
        ));
    }
    let (area, type_code, numeric) = if let Some(rest) = value.strip_prefix("M10.") {
        ("M10", 0x08, rest)
    } else if let Some(rest) = value.strip_prefix("M3.") {
        ("M3", 0x04, rest)
    } else if let Some(rest) = value.strip_prefix("M1.") {
        ("M1", 0x02, rest)
    } else if let Some(rest) = value.strip_prefix('I') {
        ("I", 0x01, rest)
    } else if let Some(rest) = value.strip_prefix('Q') {
        ("Q", 0x01, rest)
    } else {
        return Err(invalid(
            "SPH 地址必须是 M1.0、M3.0、M10.0、I0 或 Q0，可选 .0-.15 位后缀",
            serde_json::json!({ "address": input }),
        ));
    };
    let mut parts = numeric.split('.');
    let word_text = parts.next().unwrap_or_default();
    let bit_text = parts.next();
    if parts.next().is_some() || word_text.is_empty() {
        return Err(invalid(
            "SPH 地址层级或位后缀格式无效",
            serde_json::json!({ "address": input }),
        ));
    }
    let word_address = word_text.parse::<u32>().map_err(|_| {
        invalid(
            "SPH 字地址必须是 0..16777215",
            serde_json::json!({ "address": input }),
        )
    })?;
    if word_address > 0xFF_FFFF {
        return Err(invalid(
            "SPH 字地址必须是 0..16777215",
            serde_json::json!({ "address": input }),
        ));
    }
    let bit_index = match bit_text {
        None => None,
        Some(text) => {
            let bit = text.parse::<u8>().map_err(|_| {
                invalid(
                    "SPH 位后缀必须是 0..15",
                    serde_json::json!({ "address": input }),
                )
            })?;
            if bit > 15 {
                return Err(invalid(
                    "SPH 位后缀必须是 0..15",
                    serde_json::json!({ "address": input }),
                ));
            }
            Some(bit)
        }
    };
    let canonical = match bit_index {
        Some(bit) => format!("{area}{word_address}.{bit}"),
        None => format!("{area}{word_address}"),
    };
    Ok(Address {
        area: area.to_string(),
        type_code,
        word_address,
        bit_index,
        canonical,
    })
}

fn validate_range(address: &Address, words: u16) -> Result<(), CoreError> {
    if words == 0 || words > MAX_WORDS {
        return Err(invalid(
            format!("SPH 单帧字数必须是 1..{MAX_WORDS}"),
            serde_json::json!({ "words": words }),
        ));
    }
    let last = u64::from(address.word_address) + u64::from(words) - 1;
    if last > 0xFF_FFFF {
        return Err(invalid(
            "SPH 地址与字数超出 24 位地址范围",
            serde_json::json!({ "address": address.canonical, "words": words }),
        ));
    }
    Ok(())
}

fn build_memory_command(
    connection_id: u8,
    command: u8,
    address: &Address,
    words: u16,
    data: &[u8],
) -> Result<Vec<u8>, CoreError> {
    if command != READ && command != WRITE {
        return Err(invalid(
            "SPH 命令只能是 00H 读取或 01H 写入",
            serde_json::json!({ "command": command }),
        ));
    }
    validate_range(address, words)?;
    if command == READ && !data.is_empty() {
        return Err(invalid(
            "SPH 读命令不能携带写入数据",
            serde_json::json!({ "bytes": data.len() }),
        ));
    }
    if command == WRITE && (data.is_empty() || data.len() != usize::from(words) * 2) {
        return Err(invalid(
            "SPH 写入数据必须恰好是 wordCount×2 字节",
            serde_json::json!({ "words": words, "bytes": data.len() }),
        ));
    }
    let payload_length = 6usize + data.len();
    if payload_length > u16::MAX as usize {
        return Err(invalid(
            "SPH payload 过长",
            serde_json::json!({ "bytes": payload_length }),
        ));
    }
    let mut frame = vec![0u8; FRAME_BYTES + data.len()];
    frame[0..6].copy_from_slice(&[0xFB, 0x80, 0x80, 0x00, 0xFF, 0x7B]);
    frame[6] = connection_id;
    frame[8] = 0x11;
    frame[14] = command;
    frame[17] = 0x01;
    frame[18..20].copy_from_slice(&(payload_length as u16).to_le_bytes());
    frame[20] = address.type_code;
    let word = address.word_address.to_le_bytes();
    frame[21..24].copy_from_slice(&word[..3]);
    frame[24..26].copy_from_slice(&words.to_le_bytes());
    frame[26..].copy_from_slice(data);
    Ok(frame)
}

pub fn build_read(connection_id: u8, address: &str, words: u16) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(address)?;
    if parsed.bit_index.is_some() {
        return Err(invalid(
            "SPH 首轮字读取地址不能包含位后缀；位读取只保留地址解析",
            serde_json::json!({ "address": address }),
        ));
    }
    build_memory_command(connection_id, READ, &parsed, words, &[])
}

pub fn build_write(connection_id: u8, address: &str, data: &[u8]) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(address)?;
    if parsed.bit_index.is_some() {
        return Err(invalid(
            "SPH 字写入地址不能包含位后缀",
            serde_json::json!({ "address": address }),
        ));
    }
    if data.is_empty() || data.len() % 2 != 0 {
        return Err(invalid(
            "SPH 写入数据必须是非空偶数字节",
            serde_json::json!({ "bytes": data.len() }),
        ));
    }
    let words = u16::try_from(data.len() / 2).map_err(|_| {
        invalid(
            "SPH 写入字数超过 u16",
            serde_json::json!({ "bytes": data.len() }),
        )
    })?;
    build_memory_command(connection_id, WRITE, &parsed, words, data)
}

pub fn parse_response(
    response: &[u8],
    connection_id: u8,
    command: u8,
    expected_data_bytes: usize,
) -> Result<Response, CoreError> {
    if response.len() < FRAME_BYTES {
        return Err(frame_error(format!("SPH 响应至少需要 {FRAME_BYTES} 字节")));
    }
    if response[0..4] != [0xFB, 0x80, 0x80, 0x00]
        || response[5] != 0x7B
        || response[8] != 0x11
        || response[15] != 0x00
    {
        return Err(frame_error("SPH 响应固定头字段无效"));
    }
    if response[6] != connection_id {
        return Err(error(
            "FUJI_SPH_CONNECTION_MISMATCH",
            "SPH 响应连接 ID 与请求不一致",
            None,
        ));
    }
    if response[14] != command {
        return Err(error(
            "FUJI_SPH_COMMAND_MISMATCH",
            "SPH 响应命令与请求不一致",
            None,
        ));
    }
    let payload_length = u16::from_le_bytes([response[18], response[19]]) as usize;
    if payload_length < 6 || payload_length != response.len() - HEADER_BYTES {
        return Err(frame_error("SPH 响应 payload 长度字段无效"));
    }
    if response[4] != 0 {
        return Err(error(
            "FUJI_SPH_DEVICE_ERROR",
            format!("SPH CPU 返回错误码 0x{:02X}", response[4]),
            Some(serde_json::json!({ "errorCode": response[4] })),
        ));
    }
    let data = response[FRAME_BYTES..].to_vec();
    if data.len() != expected_data_bytes {
        return Err(error(
            "FUJI_SPH_DATA_LENGTH_MISMATCH",
            format!(
                "SPH 响应数据长度不符，期望 {expected_data_bytes}，实际 {}",
                data.len()
            ),
            None,
        ));
    }
    Ok(Response {
        connection_id,
        command,
        error_code: response[4],
        type_code: response[20],
        word_address: u32::from(response[21])
            | (u32::from(response[22]) << 8)
            | (u32::from(response[23]) << 16),
        words: u16::from_le_bytes([response[24], response[25]]),
        data,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_addresses_and_builds_little_endian_header() {
        let address = parse_address("M10.258").unwrap();
        assert_eq!(address.type_code, 0x08);
        assert_eq!(address.word_address, 258);
        let frame = build_read(0xFE, "M10.258", 2).unwrap();
        assert_eq!(&frame[..6], &[0xFB, 0x80, 0x80, 0x00, 0xFF, 0x7B]);
        assert_eq!(frame[6], 0xFE);
        assert_eq!(frame[14], READ);
        assert_eq!(&frame[20..26], &[0x08, 0x02, 0x01, 0x00, 0x02, 0x00]);
    }

    #[test]
    fn builds_write_and_parses_response() {
        let request = build_write(0xFE, "M1.0", &[0x34, 0x12]).unwrap();
        let mut response = request.clone();
        response[4] = 0;
        response[14] = WRITE;
        response[18..20].copy_from_slice(&6u16.to_le_bytes());
        response.truncate(FRAME_BYTES);
        let parsed = parse_response(&response, 0xFE, WRITE, 0).unwrap();
        assert_eq!(parsed.type_code, 0x02);
        assert_eq!(parsed.words, 1);
    }

    #[test]
    fn rejects_bit_write_and_unsafe_ranges() {
        assert!(parse_address("I0.15").is_ok());
        assert!(build_read(0xFE, "I0.15", 1).is_err());
        assert!(build_read(0xFE, "M1.16777215", 2).is_err());
        assert!(build_write(0xFE, "M3.1.2", &[0, 1]).is_err());
    }
}
