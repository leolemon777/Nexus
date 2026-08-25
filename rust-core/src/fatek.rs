//! FATEK FBs native ASCII programming protocol over TCP.
//!
//! The codec keeps the documented STX/station/command/checksum/ETX framing and
//! the 40/44/45/46/47 first-round commands.  The session layer exposes only
//! read-only TCP transactions; it does not claim a live PLC model match or
//! RUN/STOP permission.

use crate::error::CoreError;

const STX: u8 = 0x02;
const ETX: u8 = 0x03;
pub const MAX_DISCRETE: u16 = 255;
pub const MAX_WORDS: u16 = 64;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AccessKind {
    Auto,
    Bit,
    Word,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Address {
    pub data_code: String,
    pub number: u32,
    pub is_discrete: bool,
    pub canonical: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Response {
    pub station: u8,
    pub command: String,
    pub status: char,
    pub data: Vec<u8>,
}

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

fn invalid(message: impl Into<String>, address: &str) -> CoreError {
    error(
        "FATEK_PARAM_INVALID",
        message,
        Some(serde_json::json!({ "address": address })),
    )
}

fn unsupported(message: impl Into<String>, address: &str) -> CoreError {
    error(
        "FATEK_ADDRESS_UNSUPPORTED",
        message,
        Some(serde_json::json!({ "address": address })),
    )
}

fn hex_digit(value: u8) -> u8 {
    match value & 0x0F {
        0..=9 => b'0' + (value & 0x0F),
        n => b'A' + (n - 10),
    }
}

fn hex_value(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'A'..=b'F' => Some(value - b'A' + 10),
        b'a'..=b'f' => Some(value - b'a' + 10),
        _ => None,
    }
}

fn checksum(bytes: &[u8]) -> u8 {
    bytes.iter().fold(0u8, |sum, byte| sum.wrapping_add(*byte))
}

pub fn parse_address(input: &str, kind: AccessKind) -> Result<Address, CoreError> {
    let original = input.trim();
    if original.is_empty() {
        return Err(invalid("FATEK 地址不能为空", input));
    }
    let normalized = original.to_ascii_uppercase();
    let (data_code, number_text) = if normalized.starts_with("RT") || normalized.starts_with("RC") {
        normalized.split_at(2)
    } else {
        normalized.split_at(1)
    };
    if number_text.is_empty() || !number_text.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(invalid("FATEK 地址数字必须是十进制整数", original));
    }
    let discrete = matches!(data_code, "X" | "Y" | "M" | "S" | "T" | "C");
    let register = matches!(data_code, "R" | "D" | "RT" | "RC");
    if !discrete && !register {
        return Err(unsupported("不支持的 FATEK 数据区", original));
    }
    if matches!(kind, AccessKind::Bit) && !discrete {
        return Err(unsupported("FATEK Bit 访问只允许 X/Y/M/S/T/C", original));
    }
    if matches!(kind, AccessKind::Word) && !register && !discrete {
        return Err(unsupported("FATEK Word 访问区无效", original));
    }
    let number = number_text
        .parse::<u32>()
        .map_err(|_| invalid("FATEK 地址数字溢出", original))?;
    let maximum = if matches!(data_code, "R" | "D") {
        99_999
    } else {
        9_999
    };
    if number > maximum {
        return Err(invalid(
            format!("FATEK {data_code} 地址范围为 0..{maximum}"),
            original,
        ));
    }
    Ok(Address {
        data_code: data_code.to_string(),
        number,
        is_discrete: discrete,
        canonical: format!("{data_code}{number}"),
    })
}

fn discrete_operand(address: &Address) -> String {
    format!("{}{:04}", address.data_code, address.number)
}

fn word_operand(address: &Address) -> String {
    let code = if address.is_discrete {
        format!("W{}", address.data_code)
    } else {
        address.data_code.clone()
    };
    let width = if matches!(address.data_code.as_str(), "R" | "D") {
        5
    } else {
        4
    };
    format!("{code}{:0width$}", address.number, width = width)
}

fn range_valid(address: &Address, count: u16) -> bool {
    let last = if address.is_discrete {
        address
            .number
            .saturating_add(u32::from(count).saturating_mul(16))
            .saturating_sub(1)
    } else {
        address
            .number
            .saturating_add(u32::from(count))
            .saturating_sub(1)
    };
    let maximum = if matches!(address.data_code.as_str(), "R" | "D") {
        99_999
    } else {
        9_999
    };
    last <= maximum
}

pub fn pack_command(station: u8, command: &str) -> Result<Vec<u8>, CoreError> {
    if station == 0 || station == 0xFF {
        return Err(error(
            "FATEK_STATION_INVALID",
            "FATEK 站号必须为 01H..FEH",
            None,
        ));
    }
    let command = command.trim().to_ascii_uppercase();
    if command.is_empty() || !command.bytes().all(|byte| byte.is_ascii_graphic()) {
        return Err(error(
            "FATEK_COMMAND_INVALID",
            "FATEK 命令必须是 ASCII 可打印字符",
            None,
        ));
    }
    let mut frame = Vec::with_capacity(command.len() + 6);
    frame.push(STX);
    frame.push(hex_digit(station >> 4));
    frame.push(hex_digit(station));
    frame.extend_from_slice(command.as_bytes());
    let sum = checksum(&frame);
    frame.push(hex_digit(sum >> 4));
    frame.push(hex_digit(sum));
    frame.push(ETX);
    Ok(frame)
}

pub fn build_read_discrete(station: u8, address: &str, count: u16) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(address, AccessKind::Bit)?;
    if !parsed.is_discrete {
        return Err(unsupported("位读取地址必须是 X/Y/M/S/T/C", address));
    }
    if !(1..=MAX_DISCRETE).contains(&count) {
        return Err(invalid(
            format!("FATEK 位读取数量必须为 1..{MAX_DISCRETE}"),
            address,
        ));
    }
    if !range_valid(&parsed, count) {
        return Err(invalid("FATEK 位地址与数量超出四位地址范围", address));
    }
    pack_command(
        station,
        &format!("44{:02X}{}", count, discrete_operand(&parsed)),
    )
}

pub fn build_write_discrete(
    station: u8,
    address: &str,
    values: &[bool],
) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(address, AccessKind::Bit)?;
    if !parsed.is_discrete {
        return Err(unsupported("位写入地址必须是 X/Y/M/S/T/C", address));
    }
    if values.is_empty() || values.len() > usize::from(MAX_DISCRETE) {
        return Err(invalid(
            format!("FATEK 位写入数量必须为 1..{MAX_DISCRETE}"),
            address,
        ));
    }
    if !range_valid(&parsed, values.len() as u16) {
        return Err(invalid("FATEK 位地址与数量超出四位地址范围", address));
    }
    let mut command = format!("45{:02X}{}", values.len(), discrete_operand(&parsed));
    command.extend(values.iter().map(|value| if *value { '1' } else { '0' }));
    pack_command(station, &command)
}

pub fn build_read_words(station: u8, address: &str, count: u16) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(address, AccessKind::Word)?;
    if !(1..=MAX_WORDS).contains(&count) {
        return Err(invalid(
            format!("FATEK 字读取数量必须为 1..{MAX_WORDS}"),
            address,
        ));
    }
    if !range_valid(&parsed, count) {
        return Err(invalid("FATEK 地址与字数超出协议地址范围", address));
    }
    pack_command(
        station,
        &format!("46{:02X}{}", count, word_operand(&parsed)),
    )
}

pub fn build_write_words(station: u8, address: &str, data: &[u8]) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(address, AccessKind::Word)?;
    if data.is_empty() || data.len() % 2 != 0 {
        return Err(invalid("FATEK 字写入数据必须是非空偶数字节", address));
    }
    let count = data.len() / 2;
    if count > usize::from(MAX_WORDS) || !range_valid(&parsed, count as u16) {
        return Err(invalid("FATEK 地址与写入字数超出协议范围", address));
    }
    let mut command = format!("47{:02X}{}", count, word_operand(&parsed));
    for chunk in data.chunks_exact(2) {
        let value = u16::from_le_bytes([chunk[0], chunk[1]]);
        command.push_str(&format!("{value:04X}"));
    }
    pack_command(station, &command)
}

pub fn build_status(station: u8) -> Result<Vec<u8>, CoreError> {
    pack_command(station, "40")
}

pub fn parse_response(response: &[u8], station: u8, command: &str) -> Result<Response, CoreError> {
    if response.len() < 9 || response[0] != STX || response[response.len() - 1] != ETX {
        return Err(error(
            "FATEK_RESPONSE_INVALID",
            "FATEK 响应缺少 STX/ETX 或长度不足",
            None,
        ));
    }
    if response[1] != hex_digit(station >> 4) || response[2] != hex_digit(station) {
        return Err(error(
            "FATEK_STATION_MISMATCH",
            "FATEK 响应站号与请求不一致",
            None,
        ));
    }
    let command = command.trim().to_ascii_uppercase();
    if command.len() != 2
        || response[3] != command.as_bytes()[0]
        || response[4] != command.as_bytes()[1]
    {
        return Err(error(
            "FATEK_COMMAND_MISMATCH",
            "FATEK 响应命令与请求不一致",
            None,
        ));
    }
    let Some(high) = hex_value(response[response.len() - 3]) else {
        return Err(error(
            "FATEK_CHECKSUM_INVALID",
            "FATEK 响应校验码不是十六进制",
            None,
        ));
    };
    let Some(low) = hex_value(response[response.len() - 2]) else {
        return Err(error(
            "FATEK_CHECKSUM_INVALID",
            "FATEK 响应校验码不是十六进制",
            None,
        ));
    };
    let expected = checksum(&response[..response.len() - 3]);
    if expected != (high << 4 | low) {
        return Err(error(
            "FATEK_CHECKSUM_INVALID",
            "FATEK 响应校验码错误",
            None,
        ));
    }
    let status = response[5] as char;
    if status != '0' {
        return Err(error(
            "FATEK_DEVICE_ERROR",
            format!("FATEK PLC 返回错误码 {status}"),
            Some(serde_json::json!({ "status": status.to_string() })),
        ));
    }
    Ok(Response {
        station,
        command,
        status,
        data: response[6..response.len() - 3].to_vec(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_documented_frames_and_checksum() {
        let frame = build_read_words(1, "R12", 3).unwrap();
        assert_eq!(String::from_utf8_lossy(&frame), "\x02014603R0001275\x03");
        let bits = build_write_discrete(1, "Y0", &[true, false, true, true]).unwrap();
        assert_eq!(bits.last(), Some(&ETX));
        assert_eq!(frame[frame.len() - 3], b'7');
    }

    #[test]
    fn parses_response_and_rejects_bad_checksum_or_station() {
        let mut response = pack_command(1, "4600000").unwrap();
        let parsed = parse_response(&response, 1, "46").unwrap();
        assert_eq!(parsed.data, b"0000");
        response[1] = b'2';
        assert!(parse_response(&response, 1, "46").is_err());
    }

    #[test]
    fn rejects_unsafe_addresses_and_ranges() {
        assert!(parse_address("R100", AccessKind::Auto).is_ok());
        assert!(parse_address("R100", AccessKind::Bit).is_err());
        assert!(parse_address("Q0", AccessKind::Auto).is_err());
        assert!(build_read_discrete(1, "X9999", 2).is_err());
        assert!(build_read_words(1, "R99999", 2).is_err());
    }
}
