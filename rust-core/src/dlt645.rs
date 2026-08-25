//! DL/T 645-1997 and DL/T 645-2007 read-only meter protocol codec.
//!
//! The two revisions deliberately share only the physical frame primitives.
//! Their data identifiers and request/response control bytes remain separate.
//! This module does not expose address changes, time writes, broadcast writes,
//! freezing, tariff programming, or breaker/control operations.

use std::fmt;
use std::str::FromStr;

use serde::Serialize;

use crate::error::CoreError;

pub const FRAME_START: u8 = 0x68;
pub const FRAME_END: u8 = 0x16;
pub const PREAMBLE: u8 = 0xFE;
pub const DATA_OFFSET: u8 = 0x33;
pub const MAX_PREAMBLE_COUNT: u8 = 4;
pub const MAX_READ_DATA_LENGTH: usize = 200;
pub const BROADCAST_ADDRESS: &str = "999999999999";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum Version {
    #[serde(rename = "1997")]
    Dlt1997,
    #[serde(rename = "2007")]
    Dlt2007,
}

impl Version {
    pub const fn data_id_length(self) -> usize {
        match self {
            Self::Dlt1997 => 2,
            Self::Dlt2007 => 4,
        }
    }

    pub const fn read_request_control(self) -> u8 {
        match self {
            Self::Dlt1997 => 0x01,
            Self::Dlt2007 => 0x11,
        }
    }

    pub const fn normal_read_response_control(self) -> u8 {
        match self {
            Self::Dlt1997 => 0x81,
            Self::Dlt2007 => 0x91,
        }
    }

    pub const fn follow_read_response_control(self) -> u8 {
        match self {
            Self::Dlt1997 => 0xA1,
            Self::Dlt2007 => 0xB1,
        }
    }

    pub const fn error_read_response_control(self) -> u8 {
        match self {
            Self::Dlt1997 => 0xC1,
            Self::Dlt2007 => 0xD1,
        }
    }
}

impl fmt::Display for Version {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::Dlt1997 => "1997",
            Self::Dlt2007 => "2007",
        })
    }
}

impl FromStr for Version {
    type Err = CoreError;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        match value.trim() {
            "1997" | "dlt645-1997" | "DL/T 645-1997" => Ok(Self::Dlt1997),
            "2007" | "dlt645-2007" | "DL/T 645-2007" => Ok(Self::Dlt2007),
            other => Err(dlt_error(
                "DLT645_VERSION_INVALID",
                "DL/T 645 版本只能是 1997 或 2007",
                Some(serde_json::json!({ "version": other })),
            )),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DataIdentifier {
    pub version: Version,
    /// Canonical high-byte-first hexadecimal form (4 digits for 1997, 8 for 2007).
    pub canonical: String,
    /// Low byte first, before the +33H wire transform.
    pub wire_bytes: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Frame {
    pub preamble_count: u8,
    pub address: String,
    pub address_bytes: [u8; 6],
    pub control: u8,
    pub data_length: u8,
    /// Decoded data bytes after subtracting 33H. Byte order remains wire order.
    pub data: Vec<u8>,
    pub checksum: u8,
    pub is_response: bool,
    pub is_exception: bool,
    pub has_follow_frame: bool,
    pub function: u8,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct KnownValue {
    pub kind: &'static str,
    pub value: serde_json::Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unit: Option<&'static str>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MeterException {
    pub code: u8,
    pub flags: Vec<&'static str>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReadResponse {
    pub version: Version,
    pub address: String,
    pub data_id: String,
    pub control: u8,
    pub has_follow_frame: bool,
    pub payload: Vec<u8>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub known_value: Option<KnownValue>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exception: Option<MeterException>,
    pub read_only: bool,
    pub transport: &'static str,
}

fn dlt_error(
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

fn bcd_byte(high_digit: u8, low_digit: u8) -> u8 {
    (high_digit << 4) | low_digit
}

fn validate_bcd(byte: u8, field: &'static str, index: usize) -> Result<(), CoreError> {
    if byte >> 4 > 9 || byte & 0x0F > 9 {
        return Err(dlt_error(
            "DLT645_BCD_INVALID",
            format!("DL/T 645 {field} 含有非 BCD 字节"),
            Some(serde_json::json!({
                "field": field,
                "index": index,
                "byte": byte
            })),
        ));
    }
    Ok(())
}

/// Parse twelve decimal address digits into six BCD bytes, least-significant pair first.
pub fn parse_address(address: &str) -> Result<[u8; 6], CoreError> {
    let normalized = address.trim();
    if normalized.len() != 12 || !normalized.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(dlt_error(
            "DLT645_ADDRESS_INVALID",
            "DL/T 645 表地址必须是恰好 12 位十进制数字",
            Some(serde_json::json!({ "address": normalized })),
        ));
    }
    let digits = normalized.as_bytes();
    let mut result = [0u8; 6];
    for (wire_index, pair_start) in (0..12).step_by(2).rev().enumerate() {
        result[wire_index] = bcd_byte(digits[pair_start] - b'0', digits[pair_start + 1] - b'0');
    }
    Ok(result)
}

pub fn format_address(address: &[u8; 6]) -> Result<String, CoreError> {
    let mut result = String::with_capacity(12);
    for (index, byte) in address.iter().rev().enumerate() {
        validate_bcd(*byte, "address", 5 - index)?;
        result.push(char::from(b'0' + (byte >> 4)));
        result.push(char::from(b'0' + (byte & 0x0F)));
    }
    Ok(result)
}

fn decode_hex(value: &str, field: &'static str) -> Result<Vec<u8>, CoreError> {
    let normalized: String = value
        .chars()
        .filter(|character| !character.is_ascii_whitespace())
        .collect();
    if normalized.is_empty() || normalized.len() % 2 != 0 {
        return Err(dlt_error(
            "DLT645_HEX_INVALID",
            format!("DL/T 645 {field} 必须是偶数位十六进制字符串"),
            Some(serde_json::json!({ "field": field, "value": value })),
        ));
    }
    let mut result = Vec::with_capacity(normalized.len() / 2);
    for index in (0..normalized.len()).step_by(2) {
        let byte = u8::from_str_radix(&normalized[index..index + 2], 16).map_err(|_| {
            dlt_error(
                "DLT645_HEX_INVALID",
                format!("DL/T 645 {field} 含有无效十六进制字符"),
                Some(serde_json::json!({ "field": field, "value": value, "index": index })),
            )
        })?;
        result.push(byte);
    }
    Ok(result)
}

pub fn parse_data_identifier(version: Version, value: &str) -> Result<DataIdentifier, CoreError> {
    let canonical_bytes = decode_hex(value, "dataId")?;
    let expected = version.data_id_length();
    if canonical_bytes.len() != expected {
        return Err(dlt_error(
            "DLT645_DATA_ID_LENGTH_INVALID",
            format!("DL/T 645-{version} 数据标识必须是 {} 个字节", expected),
            Some(serde_json::json!({
                "version": version,
                "expected": expected,
                "actual": canonical_bytes.len()
            })),
        ));
    }
    let canonical = canonical_bytes
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<String>();
    let wire_bytes = canonical_bytes.into_iter().rev().collect();
    Ok(DataIdentifier {
        version,
        canonical,
        wire_bytes,
    })
}

fn format_data_identifier(version: Version, wire_bytes: &[u8]) -> Result<String, CoreError> {
    if wire_bytes.len() != version.data_id_length() {
        return Err(dlt_error(
            "DLT645_DATA_ID_LENGTH_INVALID",
            "DL/T 645 响应数据标识长度与版本不符",
            Some(serde_json::json!({
                "version": version,
                "expected": version.data_id_length(),
                "actual": wire_bytes.len()
            })),
        ));
    }
    Ok(wire_bytes
        .iter()
        .rev()
        .map(|byte| format!("{byte:02X}"))
        .collect())
}

pub fn checksum(core_without_checksum: &[u8]) -> u8 {
    core_without_checksum
        .iter()
        .fold(0u8, |sum, byte| sum.wrapping_add(*byte))
}

pub fn frame_hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<Vec<_>>()
        .join(" ")
}

pub fn build_frame(
    address: [u8; 6],
    control: u8,
    decoded_data: &[u8],
    preamble_count: u8,
) -> Result<Vec<u8>, CoreError> {
    if preamble_count > MAX_PREAMBLE_COUNT {
        return Err(dlt_error(
            "DLT645_PREAMBLE_INVALID",
            "DL/T 645 前导 FE 字节数量只能是 0 到 4",
            Some(serde_json::json!({ "preambleCount": preamble_count })),
        ));
    }
    if decoded_data.len() > MAX_READ_DATA_LENGTH {
        return Err(dlt_error(
            "DLT645_DATA_TOO_LONG",
            format!("DL/T 645 只读数据域不能超过 {MAX_READ_DATA_LENGTH} 字节"),
            Some(serde_json::json!({ "length": decoded_data.len() })),
        ));
    }
    let data_length = u8::try_from(decoded_data.len())
        .map_err(|_| dlt_error("DLT645_DATA_TOO_LONG", "DL/T 645 数据域长度溢出", None))?;
    let mut frame = Vec::with_capacity(usize::from(preamble_count) + 12 + decoded_data.len());
    frame.extend(std::iter::repeat_n(PREAMBLE, usize::from(preamble_count)));
    let core_start = frame.len();
    frame.push(FRAME_START);
    frame.extend_from_slice(&address);
    frame.extend_from_slice(&[FRAME_START, control, data_length]);
    frame.extend(
        decoded_data
            .iter()
            .map(|byte| byte.wrapping_add(DATA_OFFSET)),
    );
    frame.push(checksum(&frame[core_start..]));
    frame.push(FRAME_END);
    Ok(frame)
}

pub fn build_read_request(
    version: Version,
    address: &str,
    data_id: &str,
    preamble_count: u8,
) -> Result<Vec<u8>, CoreError> {
    let normalized_address = address.trim();
    if normalized_address == BROADCAST_ADDRESS {
        return Err(dlt_error(
            "DLT645_BROADCAST_READ_FORBIDDEN",
            "DL/T 645 只读请求不允许使用广播地址 999999999999",
            None,
        ));
    }
    let address = parse_address(normalized_address)?;
    let data_id = parse_data_identifier(version, data_id)?;
    build_frame(
        address,
        version.read_request_control(),
        &data_id.wire_bytes,
        preamble_count,
    )
}

pub fn parse_frame(bytes: &[u8]) -> Result<Frame, CoreError> {
    let preamble_count = bytes.iter().take_while(|byte| **byte == PREAMBLE).count();
    if preamble_count > usize::from(MAX_PREAMBLE_COUNT) {
        return Err(dlt_error(
            "DLT645_PREAMBLE_INVALID",
            "DL/T 645 帧前最多允许 4 个 FE 前导字节",
            Some(serde_json::json!({ "preambleCount": preamble_count })),
        ));
    }
    let core = bytes
        .get(preamble_count..)
        .ok_or_else(|| dlt_error("DLT645_FRAME_TRUNCATED", "DL/T 645 帧为空", None))?;
    if core.len() < 12 {
        return Err(dlt_error(
            "DLT645_FRAME_TRUNCATED",
            "DL/T 645 帧短于最小 12 字节",
            Some(serde_json::json!({ "length": core.len() })),
        ));
    }
    if core[0] != FRAME_START || core[7] != FRAME_START {
        return Err(dlt_error(
            "DLT645_FRAME_START_INVALID",
            "DL/T 645 帧必须在地址前后各包含一个 68H",
            Some(serde_json::json!({ "first": core[0], "second": core[7] })),
        ));
    }
    let data_length = usize::from(core[9]);
    if data_length > MAX_READ_DATA_LENGTH {
        return Err(dlt_error(
            "DLT645_DATA_TOO_LONG",
            format!("DL/T 645 只读数据域不能超过 {MAX_READ_DATA_LENGTH} 字节"),
            Some(serde_json::json!({ "length": data_length })),
        ));
    }
    let expected_length = 12 + data_length;
    if core.len() != expected_length {
        return Err(dlt_error(
            "DLT645_FRAME_LENGTH_MISMATCH",
            format!(
                "DL/T 645 帧长度不符，期望 {expected_length}，实际 {}",
                core.len()
            ),
            Some(serde_json::json!({ "expected": expected_length, "actual": core.len() })),
        ));
    }
    if core[expected_length - 1] != FRAME_END {
        return Err(dlt_error(
            "DLT645_FRAME_END_INVALID",
            "DL/T 645 帧结束字节必须是 16H",
            Some(serde_json::json!({ "actual": core[expected_length - 1] })),
        ));
    }
    let expected_checksum = checksum(&core[..expected_length - 2]);
    let actual_checksum = core[expected_length - 2];
    if actual_checksum != expected_checksum {
        return Err(dlt_error(
            "DLT645_CHECKSUM_MISMATCH",
            "DL/T 645 算术和校验失败",
            Some(serde_json::json!({
                "expected": expected_checksum,
                "actual": actual_checksum
            })),
        ));
    }
    let address_bytes: [u8; 6] = core[1..7].try_into().expect("fixed address slice");
    let address = format_address(&address_bytes)?;
    let control = core[8];
    Ok(Frame {
        preamble_count: u8::try_from(preamble_count).expect("bounded preamble"),
        address,
        address_bytes,
        control,
        data_length: core[9],
        data: core[10..10 + data_length]
            .iter()
            .map(|byte| byte.wrapping_sub(DATA_OFFSET))
            .collect(),
        checksum: actual_checksum,
        is_response: control & 0x80 != 0,
        is_exception: control & 0x40 != 0,
        has_follow_frame: control & 0x20 != 0,
        function: control & 0x1F,
    })
}

fn parse_exception(code: u8) -> MeterException {
    let mappings = [
        (0x01, "otherError"),
        (0x02, "noRequestedData"),
        (0x04, "passwordOrAuthorizationError"),
        (0x08, "communicationRateCannotChange"),
        (0x10, "yearTimeZoneExceeded"),
        (0x20, "dayPeriodExceeded"),
        (0x40, "tariffNumberExceeded"),
        (0x80, "reserved"),
    ];
    let flags = mappings
        .into_iter()
        .filter_map(|(mask, label)| (code & mask != 0).then_some(label))
        .collect();
    MeterException { code, flags }
}

fn bcd_wire_to_digits(data: &[u8], field: &'static str) -> Result<String, CoreError> {
    let mut result = String::with_capacity(data.len() * 2);
    for (reverse_index, byte) in data.iter().rev().enumerate() {
        validate_bcd(*byte, field, data.len() - reverse_index - 1)?;
        result.push(char::from(b'0' + (byte >> 4)));
        result.push(char::from(b'0' + (byte & 0x0F)));
    }
    Ok(result)
}

fn energy_value(data: &[u8]) -> Result<KnownValue, CoreError> {
    if data.len() != 4 {
        return Err(dlt_error(
            "DLT645_VALUE_LENGTH_INVALID",
            "DL/T 645 电能量必须是 4 个 BCD 字节",
            Some(serde_json::json!({ "expected": 4, "actual": data.len() })),
        ));
    }
    let digits = bcd_wire_to_digits(data, "energy")?;
    let display = format!("{}.{}", &digits[..6], &digits[6..]);
    Ok(KnownValue {
        kind: "energy",
        value: serde_json::json!({ "decimal": display, "rawDigits": digits }),
        unit: Some("kWh"),
    })
}

fn bcd_to_number(byte: u8, field: &'static str, index: usize) -> Result<u8, CoreError> {
    validate_bcd(byte, field, index)?;
    Ok((byte >> 4) * 10 + (byte & 0x0F))
}

fn is_leap_year(year: u16) -> bool {
    year.is_multiple_of(400) || (year.is_multiple_of(4) && !year.is_multiple_of(100))
}

fn date_value(data: &[u8]) -> Result<KnownValue, CoreError> {
    if data.len() != 4 {
        return Err(dlt_error(
            "DLT645_VALUE_LENGTH_INVALID",
            "DL/T 645 日期星期数据必须是 4 个 BCD 字节",
            Some(serde_json::json!({ "expected": 4, "actual": data.len() })),
        ));
    }
    let weekday = bcd_to_number(data[0], "weekday", 0)?;
    let day = bcd_to_number(data[1], "day", 1)?;
    let month = bcd_to_number(data[2], "month", 2)?;
    let year = 2000 + u16::from(bcd_to_number(data[3], "year", 3)?);
    let days = match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 if is_leap_year(year) => 29,
        2 => 28,
        _ => 0,
    };
    if weekday > 6 || day == 0 || day > days {
        return Err(dlt_error(
            "DLT645_DATE_INVALID",
            "DL/T 645 日期或星期字段超出有效范围",
            Some(serde_json::json!({
                "year": year,
                "month": month,
                "day": day,
                "weekday": weekday
            })),
        ));
    }
    Ok(KnownValue {
        kind: "dateWeekday",
        value: serde_json::json!({
            "isoDate": format!("{year:04}-{month:02}-{day:02}"),
            "weekday": weekday
        }),
        unit: None,
    })
}

fn time_value(data: &[u8]) -> Result<KnownValue, CoreError> {
    if data.len() != 3 {
        return Err(dlt_error(
            "DLT645_VALUE_LENGTH_INVALID",
            "DL/T 645 时间数据必须是 3 个 BCD 字节",
            Some(serde_json::json!({ "expected": 3, "actual": data.len() })),
        ));
    }
    let second = bcd_to_number(data[0], "second", 0)?;
    let minute = bcd_to_number(data[1], "minute", 1)?;
    let hour = bcd_to_number(data[2], "hour", 2)?;
    if second > 59 || minute > 59 || hour > 23 {
        return Err(dlt_error(
            "DLT645_TIME_INVALID",
            "DL/T 645 时间字段超出有效范围",
            Some(serde_json::json!({ "hour": hour, "minute": minute, "second": second })),
        ));
    }
    Ok(KnownValue {
        kind: "time",
        value: serde_json::json!({
            "isoTime": format!("{hour:02}:{minute:02}:{second:02}"),
            "hour": hour,
            "minute": minute,
            "second": second
        }),
        unit: None,
    })
}

fn status_value(data: &[u8]) -> Result<KnownValue, CoreError> {
    if data.len() != 2 {
        return Err(dlt_error(
            "DLT645_VALUE_LENGTH_INVALID",
            "DL/T 645 电表运行状态字必须是 2 字节",
            Some(serde_json::json!({ "expected": 2, "actual": data.len() })),
        ));
    }
    let raw = u16::from_le_bytes([data[0], data[1]]);
    let set_bits: Vec<u8> = (0..16).filter(|bit| raw & (1 << bit) != 0).collect();
    Ok(KnownValue {
        kind: "meterStatusWord",
        value: serde_json::json!({ "raw": format!("0x{raw:04X}"), "setBits": set_bits }),
        unit: None,
    })
}

pub fn decode_known_value(
    version: Version,
    data_id: &str,
    data: &[u8],
) -> Result<Option<KnownValue>, CoreError> {
    match (version, data_id) {
        (Version::Dlt1997, "9010") => energy_value(data).map(Some),
        (Version::Dlt2007, "00000000" | "00010000" | "00020000") => energy_value(data).map(Some),
        (Version::Dlt2007, "04000101") => date_value(data).map(Some),
        (Version::Dlt2007, "04000102") => time_value(data).map(Some),
        (Version::Dlt2007, identifier)
            if matches!(
                identifier,
                "04000501"
                    | "04000502"
                    | "04000503"
                    | "04000504"
                    | "04000505"
                    | "04000506"
                    | "04000507"
            ) =>
        {
            status_value(data).map(Some)
        }
        _ => Ok(None),
    }
}

pub fn parse_read_response(
    bytes: &[u8],
    version: Version,
    expected_address: &str,
    expected_data_id: &str,
) -> Result<ReadResponse, CoreError> {
    let expected_address = expected_address.trim();
    parse_address(expected_address)?;
    let expected_data_id = parse_data_identifier(version, expected_data_id)?;
    let frame = parse_frame(bytes)?;

    if !frame.is_response {
        return Err(dlt_error(
            "DLT645_RESPONSE_DIRECTION_INVALID",
            "DL/T 645 收到的帧不是电表响应方向",
            Some(serde_json::json!({ "control": frame.control })),
        ));
    }
    let valid_control = frame.control == version.normal_read_response_control()
        || frame.control == version.follow_read_response_control()
        || frame.control == version.error_read_response_control();
    if !valid_control {
        return Err(dlt_error(
            "DLT645_RESPONSE_CONTROL_INVALID",
            format!("DL/T 645-{version} 响应控制码不是读数据响应"),
            Some(serde_json::json!({ "control": frame.control })),
        ));
    }
    if frame.address != expected_address {
        return Err(dlt_error(
            "DLT645_ADDRESS_MISMATCH",
            "DL/T 645 响应表地址与请求不一致",
            Some(serde_json::json!({ "expected": expected_address, "actual": frame.address })),
        ));
    }

    if frame.control == version.error_read_response_control() {
        if frame.data.len() != 1 {
            return Err(dlt_error(
                "DLT645_EXCEPTION_LENGTH_INVALID",
                "DL/T 645 异常响应数据域必须恰好 1 字节",
                Some(serde_json::json!({ "actual": frame.data.len() })),
            ));
        }
        return Ok(ReadResponse {
            version,
            address: frame.address,
            data_id: expected_data_id.canonical,
            control: frame.control,
            has_follow_frame: false,
            payload: Vec::new(),
            known_value: None,
            exception: Some(parse_exception(frame.data[0])),
            read_only: true,
            transport: "serial",
        });
    }

    let identifier_length = version.data_id_length();
    if frame.data.len() < identifier_length {
        return Err(dlt_error(
            "DLT645_RESPONSE_DATA_TRUNCATED",
            "DL/T 645 响应缺少回显的数据标识",
            Some(serde_json::json!({
                "expectedMinimum": identifier_length,
                "actual": frame.data.len()
            })),
        ));
    }
    let actual_data_id = format_data_identifier(version, &frame.data[..identifier_length])?;
    if actual_data_id != expected_data_id.canonical {
        return Err(dlt_error(
            "DLT645_DATA_ID_MISMATCH",
            "DL/T 645 响应数据标识与请求不一致",
            Some(serde_json::json!({
                "expected": expected_data_id.canonical,
                "actual": actual_data_id
            })),
        ));
    }
    let payload = frame.data[identifier_length..].to_vec();
    let known_value = decode_known_value(version, &actual_data_id, &payload)?;
    Ok(ReadResponse {
        version,
        address: frame.address,
        data_id: actual_data_id,
        control: frame.control,
        has_follow_frame: frame.has_follow_frame,
        payload,
        known_value,
        exception: None,
        read_only: true,
        transport: "serial",
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn response(
        version: Version,
        address: &str,
        data_id: &str,
        payload: &[u8],
        control: u8,
    ) -> Vec<u8> {
        let mut data = parse_data_identifier(version, data_id).unwrap().wire_bytes;
        data.extend_from_slice(payload);
        build_frame(parse_address(address).unwrap(), control, &data, 0).unwrap()
    }

    #[test]
    fn address_is_real_bcd_and_low_pair_first() {
        let bytes = parse_address("123456789012").unwrap();
        assert_eq!(bytes, [0x12, 0x90, 0x78, 0x56, 0x34, 0x12]);
        assert_eq!(format_address(&bytes).unwrap(), "123456789012");
        assert!(parse_address("1234ABC89012").is_err());
        assert!(format_address(&[0x12, 0x90, 0xFA, 0x56, 0x34, 0x12]).is_err());
    }

    #[test]
    fn revisions_keep_data_identifier_lengths_and_controls_separate() {
        assert_eq!(
            parse_data_identifier(Version::Dlt1997, "9010")
                .unwrap()
                .wire_bytes,
            [0x10, 0x90]
        );
        assert_eq!(
            parse_data_identifier(Version::Dlt2007, "00010000")
                .unwrap()
                .wire_bytes,
            [0x00, 0x00, 0x01, 0x00]
        );
        assert!(parse_data_identifier(Version::Dlt1997, "00010000").is_err());
        assert!(parse_data_identifier(Version::Dlt2007, "9010").is_err());
    }

    #[test]
    fn builds_exact_2007_read_vector_with_full_checksum_scope() {
        let request = build_read_request(Version::Dlt2007, "123456789012", "00010000", 4).unwrap();
        assert_eq!(
            request,
            [
                0xFE, 0xFE, 0xFE, 0xFE, 0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0x11, 0x04,
                0x33, 0x33, 0x34, 0x33, 0x68, 0x16,
            ]
        );
    }

    #[test]
    fn builds_exact_1997_read_vector_and_applies_33h() {
        let request = build_read_request(Version::Dlt1997, "123456789012", "9010", 0).unwrap();
        assert_eq!(
            request,
            [
                0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0x01, 0x02, 0x43, 0xC3, 0x8F, 0x16,
            ]
        );
    }

    #[test]
    fn parses_2007_energy_date_time_and_status_values() {
        let energy = response(
            Version::Dlt2007,
            "123456789012",
            "00010000",
            &[0x78, 0x56, 0x34, 0x12],
            0x91,
        );
        let parsed =
            parse_read_response(&energy, Version::Dlt2007, "123456789012", "00010000").unwrap();
        assert_eq!(parsed.known_value.unwrap().value["decimal"], "123456.78");

        let date = response(
            Version::Dlt2007,
            "123456789012",
            "04000101",
            &[0x06, 0x23, 0x08, 0x26],
            0x91,
        );
        let parsed =
            parse_read_response(&date, Version::Dlt2007, "123456789012", "04000101").unwrap();
        assert_eq!(parsed.known_value.unwrap().value["isoDate"], "2026-08-23");

        let time = response(
            Version::Dlt2007,
            "123456789012",
            "04000102",
            &[0x59, 0x58, 0x23],
            0x91,
        );
        let parsed =
            parse_read_response(&time, Version::Dlt2007, "123456789012", "04000102").unwrap();
        assert_eq!(parsed.known_value.unwrap().value["isoTime"], "23:58:59");

        let status = response(
            Version::Dlt2007,
            "123456789012",
            "04000503",
            &[0x05, 0x80],
            0x91,
        );
        let parsed =
            parse_read_response(&status, Version::Dlt2007, "123456789012", "04000503").unwrap();
        assert_eq!(parsed.known_value.unwrap().value["raw"], "0x8005");
    }

    #[test]
    fn parses_1997_energy_without_confusing_response_direction_for_error() {
        let frame = response(
            Version::Dlt1997,
            "123456789012",
            "9010",
            &[0x00, 0x00, 0x01, 0x00],
            0x81,
        );
        let parsed = parse_read_response(&frame, Version::Dlt1997, "123456789012", "9010").unwrap();
        assert!(parsed.exception.is_none());
        assert_eq!(parsed.known_value.unwrap().value["decimal"], "000100.00");
    }

    #[test]
    fn parses_version_specific_exception_controls() {
        let frame = build_frame(parse_address("123456789012").unwrap(), 0xD1, &[0x06], 0).unwrap();
        let parsed =
            parse_read_response(&frame, Version::Dlt2007, "123456789012", "00010000").unwrap();
        let exception = parsed.exception.unwrap();
        assert_eq!(exception.code, 0x06);
        assert_eq!(
            exception.flags,
            ["noRequestedData", "passwordOrAuthorizationError"]
        );
    }

    #[test]
    fn rejects_checksum_address_identifier_and_broadcast_mismatches() {
        let mut frame = response(
            Version::Dlt2007,
            "123456789012",
            "00010000",
            &[0x78, 0x56, 0x34, 0x12],
            0x91,
        );
        let checksum_index = frame.len() - 2;
        frame[checksum_index] ^= 0x01;
        assert!(parse_read_response(&frame, Version::Dlt2007, "123456789012", "00010000").is_err());

        let frame = response(
            Version::Dlt2007,
            "123456789013",
            "00010000",
            &[0x78, 0x56, 0x34, 0x12],
            0x91,
        );
        assert!(parse_read_response(&frame, Version::Dlt2007, "123456789012", "00010000").is_err());

        let frame = response(
            Version::Dlt2007,
            "123456789012",
            "00020000",
            &[0x78, 0x56, 0x34, 0x12],
            0x91,
        );
        assert!(parse_read_response(&frame, Version::Dlt2007, "123456789012", "00010000").is_err());
        assert!(build_read_request(Version::Dlt2007, BROADCAST_ADDRESS, "00010000", 4).is_err());
    }

    #[test]
    fn rejects_malformed_frame_and_invalid_known_bcd() {
        let mut frame =
            build_read_request(Version::Dlt2007, "123456789012", "00010000", 0).unwrap();
        frame[7] = 0x67;
        assert!(parse_frame(&frame).is_err());

        let invalid_energy = response(
            Version::Dlt2007,
            "123456789012",
            "00010000",
            &[0xFA, 0x56, 0x34, 0x12],
            0x91,
        );
        assert!(
            parse_read_response(
                &invalid_energy,
                Version::Dlt2007,
                "123456789012",
                "00010000"
            )
            .is_err()
        );
    }
}
