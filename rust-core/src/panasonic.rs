//! Panasonic MEWTOCOL-COM ASCII codec.
//!
//! The first software boundary follows the retained PanasonicMewtocolClient:
//! station-addressed `%`/`<` frames, XOR BCC, CR termination, DT/D/LD/FL/F
//! word areas and X/Y/R/L/T/C contact operations.  This module deliberately
//! does not open a serial port or claim FP-series L2 evidence.

use crate::error::CoreError;

pub const STANDARD_FRAME_MAX: usize = 118;
pub const EXPANDED_FRAME_MAX: usize = 2048;
pub const MAX_WORDS: usize = 500;
pub const MIN_STATION: u8 = 1;
pub const MAX_STATION: u8 = 32;

fn mew_err(
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
    mew_err("PANASONIC_INVALID", message, details)
}

fn param(message: impl Into<String>, details: serde_json::Value) -> CoreError {
    mew_err("PANASONIC_PARAM_INVALID", message, Some(details))
}

fn station(station: u8) -> Result<(), CoreError> {
    if !(MIN_STATION..=MAX_STATION).contains(&station) {
        return Err(param(
            "MEWTOCOL 站号必须为 1..32",
            serde_json::json!({ "station": station }),
        ));
    }
    Ok(())
}

fn parse_hex_digit(value: char) -> Option<u8> {
    match value {
        '0'..='9' => Some(value as u8 - b'0'),
        'A'..='F' => Some(value as u8 - b'A' + 10),
        _ => None,
    }
}

fn parse_hex_byte(value: &str) -> Option<u8> {
    let chars: Vec<char> = value.chars().collect();
    if chars.len() != 2 {
        return None;
    }
    Some(parse_hex_digit(chars[0])? * 16 + parse_hex_digit(chars[1])?)
}

fn parse_decimal_digits(value: &str, max: u32) -> Option<u32> {
    if value.is_empty() || !value.chars().all(|c| c.is_ascii_digit()) {
        return None;
    }
    let parsed = value.parse::<u32>().ok()?;
    (parsed <= max).then_some(parsed)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DataAddress {
    pub area_code: char,
    pub address: u32,
    pub canonical: String,
}

pub fn parse_data_address(address: &str) -> Result<DataAddress, CoreError> {
    let value = address.trim().to_ascii_uppercase();
    if value.is_empty() {
        return Err(param(
            "MEWTOCOL 数据地址不能为空",
            serde_json::json!({ "address": address }),
        ));
    }
    let (area_code, digits) = if let Some(rest) = value.strip_prefix("DT") {
        ('D', rest)
    } else if let Some(rest) = value.strip_prefix("LD") {
        ('L', rest)
    } else if let Some(rest) = value.strip_prefix("FL") {
        ('F', rest)
    } else if let Some(rest) = value.strip_prefix('D') {
        ('D', rest)
    } else if let Some(rest) = value.strip_prefix('F') {
        ('F', rest)
    } else {
        return Err(param(
            "MEWTOCOL 数据地址应为 DT/D、LD 或 FL/F",
            serde_json::json!({ "address": address }),
        ));
    };
    let parsed = parse_decimal_digits(digits, 99_999).ok_or_else(|| {
        param(
            "MEWTOCOL 数据地址必须是 0..99999",
            serde_json::json!({ "address": address }),
        )
    })?;
    Ok(DataAddress {
        area_code,
        address: parsed,
        canonical: format!("{}{}", area_code, parsed),
    })
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ContactAddress {
    pub area_code: char,
    pub linear_address: u32,
    pub contact_number: String,
    pub canonical: String,
}

pub fn parse_contact_address(address: &str) -> Result<ContactAddress, CoreError> {
    let value = address.trim().to_ascii_uppercase();
    if value.len() < 2 {
        return Err(param(
            "MEWTOCOL 触点地址不能为空",
            serde_json::json!({ "address": address }),
        ));
    }
    let area_code = value.chars().next().unwrap();
    let numeric = &value[area_code.len_utf8()..];
    if matches!(area_code, 'T' | 'C') {
        let parsed = parse_decimal_digits(numeric, 999).ok_or_else(|| {
            param(
                "MEWTOCOL T/C 触点必须是 0..999",
                serde_json::json!({ "address": address }),
            )
        })?;
        return Ok(ContactAddress {
            area_code,
            linear_address: parsed,
            contact_number: format!("{parsed:04}"),
            canonical: format!("{area_code}{parsed}"),
        });
    }
    if !matches!(area_code, 'X' | 'Y' | 'R' | 'L') {
        return Err(param(
            "MEWTOCOL 触点区只支持 X/Y/R/L/T/C",
            serde_json::json!({ "address": address }),
        ));
    }
    let (word_text, bit_text) = if let Some(dot) = numeric.find('.') {
        if numeric[dot + 1..].contains('.') {
            return Err(param(
                "MEWTOCOL 触点只能包含一个点号",
                serde_json::json!({ "address": address }),
            ));
        }
        (&numeric[..dot], &numeric[dot + 1..])
    } else {
        if numeric.is_empty() {
            return Err(param(
                "MEWTOCOL 触点缺少编号",
                serde_json::json!({ "address": address }),
            ));
        }
        if numeric.len() == 1 {
            ("0", numeric)
        } else {
            (&numeric[..numeric.len() - 1], &numeric[numeric.len() - 1..])
        }
    };
    let word = parse_decimal_digits(word_text, 999).ok_or_else(|| {
        param(
            "MEWTOCOL X/Y/R/L 字编号必须是 0..999",
            serde_json::json!({ "address": address }),
        )
    })?;
    if bit_text.chars().count() != 1 {
        return Err(param(
            "MEWTOCOL 触点位必须是一个十六进制字符",
            serde_json::json!({ "address": address }),
        ));
    }
    let bit = parse_hex_digit(bit_text.chars().next().unwrap()).ok_or_else(|| {
        param(
            "MEWTOCOL 触点位必须是 0..F",
            serde_json::json!({ "address": address }),
        )
    })?;
    let linear_address = word * 16 + u32::from(bit);
    Ok(ContactAddress {
        area_code,
        linear_address,
        contact_number: format!("{word:03}{bit:X}"),
        canonical: format!("{area_code}{word}{bit:X}"),
    })
}

pub fn compute_bcc(text: &str) -> u8 {
    text.as_bytes().iter().fold(0u8, |bcc, byte| bcc ^ byte)
}

fn build_command(
    station_id: u8,
    command: &str,
    data: &str,
    expanded_header: bool,
) -> Result<Vec<u8>, CoreError> {
    station(station_id)?;
    if command.trim().is_empty() {
        return Err(param("MEWTOCOL 命令不能为空", serde_json::json!({})));
    }
    let prefix = format!(
        "{}{:02}#{}{}",
        if expanded_header { '<' } else { '%' },
        station_id,
        command.to_ascii_uppercase(),
        data.to_ascii_uppercase()
    );
    let frame_len = prefix.len() + 3;
    let max = if expanded_header {
        EXPANDED_FRAME_MAX
    } else {
        STANDARD_FRAME_MAX
    };
    if frame_len > max {
        return Err(param(
            format!("MEWTOCOL 帧长度不能超过 {max} 字符"),
            serde_json::json!({ "length": frame_len, "maximum": max }),
        ));
    }
    let frame = format!("{prefix}{:02X}\r", compute_bcc(&prefix));
    Ok(frame.into_bytes())
}

pub fn build_read(station_id: u8, address: &str, word_count: u16) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_data_address(address)?;
    if word_count == 0 || usize::from(word_count) > MAX_WORDS {
        return Err(param(
            format!("MEWTOCOL 单帧读取长度必须为 1..{MAX_WORDS} 字"),
            serde_json::json!({ "wordCount": word_count }),
        ));
    }
    let end = parsed
        .address
        .checked_add(u32::from(word_count) - 1)
        .ok_or_else(|| {
            param(
                "MEWTOCOL 数据地址溢出",
                serde_json::json!({ "address": address }),
            )
        })?;
    if end > 99_999 {
        return Err(param(
            "MEWTOCOL 数据地址超出 5 位范围",
            serde_json::json!({ "end": end }),
        ));
    }
    let data = format!("{}{:05}{end:05}", parsed.area_code, parsed.address);
    let expanded = 9 + usize::from(word_count) * 4 > STANDARD_FRAME_MAX;
    build_command(station_id, "RD", &data, expanded)
}

pub fn build_write(station_id: u8, address: &str, data: &[u8]) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_data_address(address)?;
    if data.is_empty() || data.len() % 2 != 0 {
        return Err(param(
            "MEWTOCOL 字写入数据必须是非空偶数字节数组",
            serde_json::json!({ "bytes": data.len() }),
        ));
    }
    let words = data.len() / 2;
    if words > MAX_WORDS {
        return Err(param(
            format!("MEWTOCOL 单帧写入长度不能超过 {MAX_WORDS} 字"),
            serde_json::json!({ "words": words }),
        ));
    }
    let end = parsed
        .address
        .checked_add(words as u32 - 1)
        .ok_or_else(|| {
            param(
                "MEWTOCOL 数据地址溢出",
                serde_json::json!({ "address": address }),
            )
        })?;
    if end > 99_999 {
        return Err(param(
            "MEWTOCOL 数据地址超出 5 位范围",
            serde_json::json!({ "end": end }),
        ));
    }
    let hex = data
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<String>();
    let command_data = format!("{}{:05}{end:05}{hex}", parsed.area_code, parsed.address);
    let standard_length = 1 + 2 + 1 + 2 + command_data.len() + 2 + 1;
    build_command(
        station_id,
        "WD",
        &command_data,
        standard_length > STANDARD_FRAME_MAX,
    )
}

pub fn build_read_contact(station_id: u8, address: &str) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_contact_address(address)?;
    build_command(
        station_id,
        "RCS",
        &format!("{}{}", parsed.area_code, parsed.contact_number),
        false,
    )
}

pub fn build_write_contact(
    station_id: u8,
    address: &str,
    value: bool,
) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_contact_address(address)?;
    build_command(
        station_id,
        "WCS",
        &format!(
            "{}{}{}",
            parsed.area_code,
            parsed.contact_number,
            if value { '1' } else { '0' }
        ),
        false,
    )
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParsedResponse {
    pub header: char,
    pub station: u8,
    pub command: Option<String>,
    pub payload: String,
    pub error_code: Option<u8>,
    pub raw: String,
}

pub fn parse_response(
    response: &[u8],
    station_id: u8,
    expected_command: &str,
    expected_header: Option<char>,
) -> Result<ParsedResponse, CoreError> {
    station(station_id)?;
    if response.len() < 9 {
        return Err(invalid("MEWTOCOL 响应长度不足", None));
    }
    let raw = String::from_utf8(response.to_vec())
        .map_err(|_| invalid("MEWTOCOL 响应必须是 ASCII", None))?;
    if !raw.ends_with('\r') {
        return Err(invalid("MEWTOCOL 响应缺少 CR 终止符", None));
    }
    let chars: Vec<char> = raw.chars().collect();
    let header = chars[0];
    if let Some(expected) = expected_header {
        if header != expected {
            return Err(invalid(
                format!("MEWTOCOL 响应头不匹配: 期望 {expected}"),
                None,
            ));
        }
    }
    if !matches!(header, '%' | '<') {
        return Err(invalid("MEWTOCOL 响应头必须是 % 或 <", None));
    }
    if chars.len() < 4 || !chars[1..3].iter().all(|c| c.is_ascii_digit()) {
        return Err(invalid("MEWTOCOL 响应站号不是两位十进制", None));
    }
    let response_station = raw[1..3]
        .parse::<u8>()
        .map_err(|_| invalid("MEWTOCOL 响应站号无效", None))?;
    if response_station != station_id {
        return Err(invalid(
            format!("MEWTOCOL 响应站号不匹配: 期望 {station_id}, 收到 {response_station}"),
            Some(serde_json::json!({ "expected": station_id, "actual": response_station })),
        ));
    }
    let bcc_index = raw.len() - 3;
    let actual_bcc = parse_hex_byte(&raw[bcc_index..bcc_index + 2])
        .ok_or_else(|| invalid("MEWTOCOL 响应 BCC 不是十六进制", None))?;
    let expected_bcc = compute_bcc(&raw[..bcc_index]);
    if actual_bcc != expected_bcc {
        return Err(invalid(
            format!("MEWTOCOL BCC 校验失败，期望 {expected_bcc:02X}，实际 {actual_bcc:02X}"),
            Some(serde_json::json!({ "expected": expected_bcc, "actual": actual_bcc })),
        ));
    }
    if raw.as_bytes()[3] == b'!' {
        if bcc_index != 6 {
            return Err(invalid("MEWTOCOL 错误响应格式无效", None));
        }
        let error_code = parse_hex_byte(&raw[4..6])
            .ok_or_else(|| invalid("MEWTOCOL 错误码不是十六进制", None))?;
        return Err(mew_err(
            "PANASONIC_DEVICE_ERROR",
            format!("MEWTOCOL PLC 错误 0x{error_code:02X}"),
            Some(serde_json::json!({ "errorCode": error_code, "raw": raw })),
        ));
    }
    if raw.as_bytes()[3] != b'$' {
        return Err(invalid("MEWTOCOL 响应既不是正常响应也不是错误响应", None));
    }
    let command = expected_command.to_ascii_uppercase();
    let data_start = 4 + command.len();
    if bcc_index < data_start || raw.get(4..data_start) != Some(command.as_str()) {
        return Err(invalid("MEWTOCOL 响应命令与请求不一致", None));
    }
    Ok(ParsedResponse {
        header,
        station: response_station,
        command: Some(command),
        payload: raw[data_start..bcc_index].to_string(),
        error_code: None,
        raw,
    })
}

pub fn frame_text(frame: &[u8]) -> String {
    String::from_utf8_lossy(frame).to_string()
}
pub fn frame_hex(frame: &[u8]) -> String {
    frame.iter().map(|byte| format!("{byte:02X}")).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn official_read_write_and_contact_vectors() {
        assert_eq!(
            frame_text(&build_read_contact(1, "X1F").unwrap()),
            "%01#RCSX001F6A\r"
        );
        assert_eq!(
            frame_text(&build_read(1, "DT100", 1).unwrap()),
            "%01#RDD001000010055\r"
        );
        assert_eq!(
            frame_text(&build_write(1, "DT100", &[0x64, 0x00]).unwrap()),
            "%01#WDD0010000100640052\r"
        );
    }

    #[test]
    fn parses_payload_and_rejects_bad_bcc_or_address() {
        let parsed = parse_response(b"%01$RD640014\r", 1, "RD", Some('%')).unwrap();
        assert_eq!(parsed.payload, "6400");
        assert!(parse_response(b"%01$RD640000\r", 1, "RD", Some('%')).is_err());
        assert!(parse_data_address("D100000").is_err());
        assert!(parse_contact_address("X1G").is_err());
    }
}
