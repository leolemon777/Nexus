//! Keyence KV Host Link ASCII codec.
//!
//! This is the first software-only boundary for the Keyence protocol family.
//! It intentionally covers KV Host Link over TCP (CR handshake, RDS/WRS, ST/RS)
//! and keeps MC Compatible, EtherNet/IP/CIP, serial Host Link and PLC control
//! as separate future protocol gates.

use std::fmt::Write as _;

use crate::error::CoreError;

pub const DEFAULT_PORT: u16 = 8501;
pub const MAX_COUNT: u16 = 256;

fn keyence_err(
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

fn invalid(message: impl Into<String>, details: Option<serde_json::Value>) -> CoreError {
    keyence_err("KEYENCE_INVALID", message, details)
}

fn param(message: impl Into<String>, details: serde_json::Value) -> CoreError {
    keyence_err("KEYENCE_PARAM_INVALID", message, Some(details))
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KeyenceAddress {
    pub device: String,
    pub offset: String,
    pub is_bit: bool,
    pub command_address: String,
}

const DEVICES: [&str; 15] = [
    "DM", "EM", "FM", "ZF", "TM", "CM", "VM", "MR", "LR", "CR", "VB", "R", "B", "W", "Z",
];

pub fn parse_address(address: &str) -> Result<KeyenceAddress, CoreError> {
    let normalized = address.trim().to_ascii_uppercase();
    if normalized.is_empty() || normalized.len() > 64 || normalized.chars().any(char::is_control) {
        return Err(param(
            "Keyence KV 地址不能为空、不能含控制字符且长度不能超过 64",
            serde_json::json!({ "address": address }),
        ));
    }
    let device = DEVICES
        .iter()
        .find(|candidate| normalized.starts_with(**candidate))
        .copied()
        .ok_or_else(|| {
            invalid(
                format!("不支持的 Keyence KV Host Link 地址: {address}"),
                Some(serde_json::json!({ "address": address })),
            )
        })?;
    let offset = &normalized[device.len()..];
    if offset.is_empty() {
        return Err(param(
            "Keyence KV 地址缺少偏移",
            serde_json::json!({ "address": address }),
        ));
    }
    let is_bit = matches!(device, "R" | "B" | "MR" | "LR" | "CR" | "VB");
    if !is_bit && offset.contains('.') {
        return Err(param(
            "Keyence KV 字地址不能包含位后缀",
            serde_json::json!({ "address": address }),
        ));
    }
    if matches!(device, "W" | "B" | "VB") {
        if offset.contains('.') || !is_hex(offset) {
            return Err(param(
                "Keyence KV W/B/VB 偏移必须是十六进制且不能带点",
                serde_json::json!({ "address": address }),
            ));
        }
    } else if !is_decimal_or_dotted(offset) {
        return Err(param(
            "Keyence KV 偏移必须是十进制或 word.bit 形式",
            serde_json::json!({ "address": address }),
        ));
    }
    let command_address = build_command_address(device, offset)?;
    Ok(KeyenceAddress {
        device: device.to_string(),
        offset: offset.to_string(),
        is_bit,
        command_address,
    })
}

fn is_hex(value: &str) -> bool {
    !value.is_empty()
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'A'..=b'F').contains(&byte))
}

fn is_decimal_or_dotted(value: &str) -> bool {
    let dots = value.bytes().filter(|byte| *byte == b'.').count();
    !value.is_empty()
        && dots <= 1
        && !value.starts_with('.')
        && !value.ends_with('.')
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || byte == b'.')
}

fn build_command_address(device: &str, offset: &str) -> Result<String, CoreError> {
    if !matches!(device, "R" | "MR" | "LR" | "CR") {
        return Ok(format!("{device}{offset}"));
    }
    let bit_address = parse_relay_bit_address(offset)?;
    let formatted = if bit_address < 16 {
        bit_address.to_string()
    } else {
        format!("{}{bit:02}", bit_address / 16, bit = bit_address % 16)
    };
    Ok(if device == "R" {
        formatted
    } else {
        format!("{device}{formatted}")
    })
}

fn parse_relay_bit_address(offset: &str) -> Result<u32, CoreError> {
    let value = if let Some((word, bit)) = offset.split_once('.') {
        let word = word.parse::<u32>().map_err(|_| {
            param(
                "Keyence relay word 无效",
                serde_json::json!({ "offset": offset }),
            )
        })?;
        let bit = bit.parse::<u32>().map_err(|_| {
            param(
                "Keyence relay bit 无效",
                serde_json::json!({ "offset": offset }),
            )
        })?;
        if bit > 15 {
            return Err(param(
                "Keyence relay 位必须是 0..15",
                serde_json::json!({ "offset": offset }),
            ));
        }
        word.checked_mul(16).and_then(|base| base.checked_add(bit))
    } else if offset.len() <= 2 {
        offset.parse::<u32>().ok()
    } else {
        let suffix = offset[offset.len() - 2..].parse::<u32>().ok();
        let prefix = offset[..offset.len() - 2].parse::<u32>().ok();
        match (prefix, suffix) {
            (Some(prefix), Some(suffix)) if suffix <= 15 => prefix
                .checked_mul(16)
                .and_then(|base| base.checked_add(suffix)),
            _ => None,
        }
    };
    value.ok_or_else(|| {
        param(
            "Keyence relay 地址超出范围或位后缀无效",
            serde_json::json!({ "offset": offset }),
        )
    })
}

pub fn build_connect(station: Option<u8>) -> Vec<u8> {
    match station {
        Some(station) => format!("CR {station:02}\r").into_bytes(),
        None => b"CR\r".to_vec(),
    }
}

pub fn build_read_words(address: &str, count: u16) -> Result<Vec<u8>, CoreError> {
    validate_count(count)?;
    let parsed = parse_address(address)?;
    if parsed.is_bit {
        return Err(param(
            "字读取要求 Keyence 字设备",
            serde_json::json!({ "address": address }),
        ));
    }
    Ok(format!("RDS {}.U {count}\r", parsed.command_address).into_bytes())
}

pub fn build_read_bits(address: &str, count: u16) -> Result<Vec<u8>, CoreError> {
    validate_count(count)?;
    let parsed = parse_address(address)?;
    if !parsed.is_bit {
        return Err(param(
            "位读取要求 Keyence 位设备",
            serde_json::json!({ "address": address }),
        ));
    }
    Ok(format!("RDS {} {count}\r", parsed.command_address).into_bytes())
}

pub fn build_write_words(address: &str, values: &[u16]) -> Result<Vec<u8>, CoreError> {
    if values.is_empty() || values.len() > usize::from(MAX_COUNT) {
        return Err(param(
            "Keyence 字写入数量必须是 1..256",
            serde_json::json!({ "count": values.len(), "maximum": MAX_COUNT }),
        ));
    }
    let parsed = parse_address(address)?;
    if parsed.is_bit {
        return Err(param(
            "字写入要求 Keyence 字设备",
            serde_json::json!({ "address": address }),
        ));
    }
    let mut text = format!("WRS {}.U {}", parsed.command_address, values.len());
    for value in values {
        write!(&mut text, " {value}").expect("writing to String cannot fail");
    }
    text.push('\r');
    Ok(text.into_bytes())
}

pub fn build_write_bit(address: &str, value: bool) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(address)?;
    if !parsed.is_bit {
        return Err(param(
            "位写入要求 Keyence 位设备",
            serde_json::json!({ "address": address }),
        ));
    }
    Ok(format!(
        "{} {}\r",
        if value { "ST" } else { "RS" },
        parsed.command_address
    )
    .into_bytes())
}

fn validate_count(count: u16) -> Result<(), CoreError> {
    if count == 0 || count > MAX_COUNT {
        return Err(param(
            "Keyence Host Link 数量必须是 1..256",
            serde_json::json!({ "count": count, "maximum": MAX_COUNT }),
        ));
    }
    Ok(())
}

pub fn normalize_line(response: &str) -> Result<String, CoreError> {
    if !response.ends_with("\r\n") {
        return Err(invalid(
            "Keyence Host Link 响应必须以 CRLF 结束",
            Some(serde_json::json!({ "response": response })),
        ));
    }
    let line = response.trim_end_matches(['\r', '\n']);
    if line.is_empty() || line.contains(['\r', '\n']) {
        return Err(invalid("Keyence Host Link 响应必须是单行 ASCII 文本", None));
    }
    if !line.is_ascii() {
        return Err(invalid("Keyence Host Link 响应必须是 ASCII", None));
    }
    Ok(line.to_string())
}

fn check_error(line: &str) -> Result<(), CoreError> {
    if !line.starts_with('E') {
        return Ok(());
    }
    let code_text = &line[1..];
    let code = code_text.parse::<u16>().map_err(|_| {
        invalid(
            format!("Keyence 错误码格式无效: {line}"),
            Some(serde_json::json!({ "response": line })),
        )
    })?;
    Err(keyence_err(
        "KEYENCE_DEVICE_ERROR",
        format!("Keyence Host Link {}: {line}", error_message(code)),
        Some(serde_json::json!({ "errorCode": code, "raw": line })),
    ))
}

pub fn parse_connect_response(response: &str) -> Result<(), CoreError> {
    let line = normalize_line(response)?;
    if line != "CC" {
        return Err(invalid(
            format!("Keyence Host Link 握手响应应为 CC，收到 {line}"),
            Some(serde_json::json!({ "response": line })),
        ));
    }
    Ok(())
}

pub fn parse_word_response(response: &str, expected_count: usize) -> Result<Vec<u16>, CoreError> {
    if expected_count == 0 || expected_count > usize::from(MAX_COUNT) {
        return Err(param(
            "Keyence 期望字数量必须是 1..256",
            serde_json::json!({ "count": expected_count }),
        ));
    }
    let line = normalize_line(response)?;
    check_error(&line)?;
    let parts: Vec<&str> = line.split_whitespace().collect();
    if parts.len() != expected_count {
        return Err(invalid(
            format!("Keyence 返回 {} 个字，期望 {expected_count}", parts.len()),
            Some(serde_json::json!({ "actual": parts.len(), "expected": expected_count })),
        ));
    }
    parts
        .iter()
        .map(|part| {
            part.parse::<u16>().map_err(|_| {
                invalid(
                    format!("Keyence 字值无效: {part}"),
                    Some(serde_json::json!({ "value": part })),
                )
            })
        })
        .collect()
}

pub fn parse_bit_response(response: &str, expected_count: usize) -> Result<Vec<bool>, CoreError> {
    if expected_count == 0 || expected_count > usize::from(MAX_COUNT) {
        return Err(param(
            "Keyence 期望位数量必须是 1..256",
            serde_json::json!({ "count": expected_count }),
        ));
    }
    let line = normalize_line(response)?;
    check_error(&line)?;
    let parts: Vec<&str> = line.split_whitespace().collect();
    if parts.len() != expected_count {
        return Err(invalid(
            format!("Keyence 返回 {} 个位，期望 {expected_count}", parts.len()),
            Some(serde_json::json!({ "actual": parts.len(), "expected": expected_count })),
        ));
    }
    parts
        .iter()
        .map(|part| match *part {
            "0" => Ok(false),
            "1" => Ok(true),
            _ => Err(invalid(
                format!("Keyence 位值无效: {part}"),
                Some(serde_json::json!({ "value": part })),
            )),
        })
        .collect()
}

pub fn parse_write_response(response: &str) -> Result<(), CoreError> {
    let line = normalize_line(response)?;
    check_error(&line)?;
    if line == "OK" {
        Ok(())
    } else {
        Err(invalid(
            format!("Keyence 写入响应应为 OK，收到 {line}"),
            Some(serde_json::json!({ "response": line })),
        ))
    }
}

pub fn error_message(code: u16) -> &'static str {
    match code {
        0 => "未定义命令",
        1 => "命令格式错误",
        2 => "设备/地址错误",
        4 => "写保护",
        5 => "PLC 忙",
        6 => "不支持的命令",
        _ => "PLC 错误",
    }
}

pub fn frame_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn address_and_host_link_commands_match_old_client() {
        assert_eq!(parse_address("DM0").unwrap().command_address, "DM0");
        assert_eq!(parse_address("MR10.1").unwrap().command_address, "MR1001");
        assert_eq!(parse_address("R0.1").unwrap().command_address, "1");
        assert_eq!(
            String::from_utf8(build_read_words("DM0", 2).unwrap()).unwrap(),
            "RDS DM0.U 2\r"
        );
        assert_eq!(
            String::from_utf8(build_read_bits("MR10.1", 3).unwrap()).unwrap(),
            "RDS MR1001 3\r"
        );
        assert_eq!(
            String::from_utf8(build_write_words("DM0", &[1, 65535]).unwrap()).unwrap(),
            "WRS DM0.U 2 1 65535\r"
        );
        assert_eq!(
            String::from_utf8(build_write_bit("MR10.1", true).unwrap()).unwrap(),
            "ST MR1001\r"
        );
    }

    #[test]
    fn response_parsers_keep_crlf_and_error_boundaries() {
        assert_eq!(parse_connect_response("CC\r\n").unwrap(), ());
        assert_eq!(
            parse_word_response("1 65535\r\n", 2).unwrap(),
            vec![1, 65535]
        );
        assert_eq!(
            parse_bit_response("0 1 0\r\n", 3).unwrap(),
            vec![false, true, false]
        );
        assert!(
            parse_word_response("E2\r\n", 1)
                .unwrap_err()
                .to_string()
                .contains("设备/地址错误")
        );
        assert!(parse_write_response("OK\n").is_err());
    }

    #[test]
    fn unsafe_addresses_and_counts_fail_closed() {
        assert!(parse_address("DM0.1").is_err());
        assert!(parse_address("W0.1").is_err());
        assert!(parse_address("MR10.16").is_err());
        assert!(build_read_words("DM0", 0).is_err());
        assert!(build_read_words("DM0", 257).is_err());
        assert!(build_read_bits("DM0", 1).is_err());
    }
}
