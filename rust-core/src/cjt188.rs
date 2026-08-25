//! CJ/T 188-2004 utility-meter read-only codec.
//!
//! The first boundary deliberately locks the mainstream 2004 frame layout:
//! `FE(0..4) + 68H + T + A0..A6 + C + L + DATA + CS + 16H`.
//! CS is the modulo-256 arithmetic sum from 68H through the last data byte.
//! The data field is plain (no +33H offset), the identifier is DI0 DI1 in
//! canonical order followed by the one-byte sequence number SER.
//!
//! Old Nexus.Cjt used a second 68H, XOR checksum and +33H encryption; those
//! deviations are kept out of this implementation and recorded in the audit.
//! No address write, valve control, parameter write or follow-up read is
//! exposed here.

use std::fmt;
use std::str::FromStr;

use serde::Serialize;

use crate::error::CoreError;

pub const FRAME_START: u8 = 0x68;
pub const FRAME_END: u8 = 0x16;
pub const PREAMBLE: u8 = 0xFE;
pub const MAX_PREAMBLE_COUNT: u8 = 4;
pub const MAX_READ_DATA_LENGTH: usize = 200;
pub const BROADCAST_ADDRESS: &str = "AAAAAAAAAAAAAA";
pub const READ_DATA_CONTROL: u8 = 0x01;
pub const NORMAL_READ_RESPONSE_CONTROL: u8 = 0x81;
pub const ERROR_READ_RESPONSE_CONTROL: u8 = 0xC1;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum MeterType {
    #[serde(rename = "cold-water")]
    ColdWater,
    #[serde(rename = "hot-water")]
    HotWater,
    #[serde(rename = "heat")]
    Heat,
    #[serde(rename = "gas")]
    Gas,
}

impl MeterType {
    pub const fn code(self) -> u8 {
        match self {
            Self::ColdWater => 0x10,
            Self::HotWater => 0x11,
            Self::Heat => 0x20,
            Self::Gas => 0x30,
        }
    }

    pub const fn label(self) -> &'static str {
        match self {
            Self::ColdWater => "冷水水表",
            Self::HotWater => "热水水表",
            Self::Heat => "热量表",
            Self::Gas => "燃气表",
        }
    }

    pub const fn cumulative_flow_unit(self) -> Option<&'static str> {
        match self {
            Self::ColdWater | Self::HotWater | Self::Gas => Some("m3"),
            // Heat-meter engineering-quantity format and unit are not confirmed
            // by the audited 2004 evidence, so 901F stays raw for heat meters.
            Self::Heat => None,
        }
    }

    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            0x10 => Some(Self::ColdWater),
            0x11 => Some(Self::HotWater),
            0x20 => Some(Self::Heat),
            0x30 => Some(Self::Gas),
            _ => None,
        }
    }
}

impl fmt::Display for MeterType {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.label())
    }
}

impl FromStr for MeterType {
    type Err = CoreError;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        match value.trim().to_ascii_lowercase().as_str() {
            "cold-water" | "coldwater" | "water-cold" | "10" => Ok(Self::ColdWater),
            "hot-water" | "hotwater" | "water-hot" | "11" => Ok(Self::HotWater),
            "heat" | "20" => Ok(Self::Heat),
            "gas" | "30" => Ok(Self::Gas),
            "electric" | "40" => Err(cjt_error(
                "CJT188_METER_TYPE_UNCONFIRMED",
                "电表不属于本轮 CJ/T 188 已确认范围；电能量请使用 DL/T 645 页面",
                Some(serde_json::json!({ "meterType": value.trim() })),
            )),
            other => Err(cjt_error(
                "CJT188_METER_TYPE_INVALID",
                "CJ/T 188 表类型只能是 cold-water、hot-water、heat 或 gas",
                Some(serde_json::json!({ "meterType": other })),
            )),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DataIdentifier {
    /// Canonical high-byte-first hexadecimal form (4 digits).
    pub canonical: String,
    /// CJ/T 188 transmits DI0 DI1 in canonical order.
    pub wire_bytes: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Frame {
    pub preamble_count: u8,
    pub meter_type: MeterType,
    pub meter_type_code: u8,
    pub address: String,
    pub address_bytes: [u8; 7],
    pub control: u8,
    pub data_length: u8,
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
    pub control: u8,
    /// Raw error payload after the echoed DI0 DI1 SER prefix. The audited
    /// 2004 evidence does not lock a vendor-independent error-code table.
    pub payload: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReadResponse {
    pub meter_type: MeterType,
    pub meter_type_code: u8,
    pub address: String,
    pub data_id: String,
    pub sequence: u8,
    pub control: u8,
    pub payload: Vec<u8>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub known_value: Option<KnownValue>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exception: Option<MeterException>,
    pub read_only: bool,
    pub transport: &'static str,
}

fn cjt_error(
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

fn validate_bcd(byte: u8, field: &'static str, index: usize) -> Result<(), CoreError> {
    if byte >> 4 > 9 || byte & 0x0F > 9 {
        return Err(cjt_error(
            "CJT188_BCD_INVALID",
            format!("CJ/T 188 {field} 含有非 BCD 字节"),
            Some(serde_json::json!({ "field": field, "index": index, "byte": byte })),
        ));
    }
    Ok(())
}

/// Parse the fourteen-character display address into seven BCD bytes,
/// least-significant pair first. AA×7 is the only accepted non-BCD broadcast.
pub fn parse_address(address: &str) -> Result<[u8; 7], CoreError> {
    let normalized = address.trim().to_ascii_uppercase();
    if normalized.len() != 14 || !normalized.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(cjt_error(
            "CJT188_ADDRESS_INVALID",
            "CJ/T 188 表地址必须是恰好 14 位十六进制/BCD 字符",
            Some(serde_json::json!({ "address": normalized })),
        ));
    }
    let mut pairs = [0u8; 7];
    for (index, pair) in normalized.as_bytes().chunks_exact(2).enumerate() {
        let text = std::str::from_utf8(pair).expect("hex digits");
        pairs[index] = u8::from_str_radix(text, 16)
            .map_err(|_| cjt_error("CJT188_ADDRESS_INVALID", "CJ/T 188 表地址字节无效", None))?;
    }
    if pairs == [0xAA; 7] {
        return Ok(pairs);
    }
    for (index, byte) in pairs.iter().enumerate() {
        validate_bcd(*byte, "address", index)?;
    }
    let mut wire = [0u8; 7];
    wire.copy_from_slice(&pairs);
    wire.reverse();
    Ok(wire)
}

pub fn format_address(address: &[u8; 7]) -> Result<String, CoreError> {
    if *address == [0xAA; 7] {
        return Ok(BROADCAST_ADDRESS.to_string());
    }
    let mut result = String::with_capacity(14);
    for (index, byte) in address.iter().rev().enumerate() {
        validate_bcd(*byte, "address", 6 - index)?;
        result.push_str(&format!("{byte:02X}"));
    }
    Ok(result)
}

fn decode_hex(value: &str, field: &'static str) -> Result<Vec<u8>, CoreError> {
    let normalized: String = value
        .chars()
        .filter(|character| !character.is_ascii_whitespace())
        .collect();
    if normalized.is_empty() || normalized.len() % 2 != 0 {
        return Err(cjt_error(
            "CJT188_HEX_INVALID",
            format!("CJ/T 188 {field} 必须是偶数位十六进制字符串"),
            Some(serde_json::json!({ "field": field, "value": value })),
        ));
    }
    let mut result = Vec::with_capacity(normalized.len() / 2);
    for index in (0..normalized.len()).step_by(2) {
        let byte = u8::from_str_radix(&normalized[index..index + 2], 16).map_err(|_| {
            cjt_error(
                "CJT188_HEX_INVALID",
                format!("CJ/T 188 {field} 含有无效十六进制字符"),
                Some(serde_json::json!({ "field": field, "value": value, "index": index })),
            )
        })?;
        result.push(byte);
    }
    Ok(result)
}

pub fn parse_data_identifier(value: &str) -> Result<DataIdentifier, CoreError> {
    let canonical_bytes = decode_hex(value, "dataId")?;
    if canonical_bytes.len() != 2 {
        return Err(cjt_error(
            "CJT188_DATA_ID_LENGTH_INVALID",
            "CJ/T 188-2004 数据标识必须是 2 个字节（DI0 DI1）",
            Some(serde_json::json!({ "expected": 2, "actual": canonical_bytes.len() })),
        ));
    }
    let canonical = canonical_bytes
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<String>();
    Ok(DataIdentifier {
        canonical,
        // The audited worked examples transmit DI0 DI1 in canonical order.
        wire_bytes: canonical_bytes,
    })
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
    meter_type: MeterType,
    address: [u8; 7],
    control: u8,
    data: &[u8],
    preamble_count: u8,
) -> Result<Vec<u8>, CoreError> {
    if preamble_count > MAX_PREAMBLE_COUNT {
        return Err(cjt_error(
            "CJT188_PREAMBLE_INVALID",
            "CJ/T 188 前导 FE 字节数量只能是 0 到 4",
            Some(serde_json::json!({ "preambleCount": preamble_count })),
        ));
    }
    if data.len() > MAX_READ_DATA_LENGTH {
        return Err(cjt_error(
            "CJT188_DATA_TOO_LONG",
            format!("CJ/T 188 只读数据域不能超过 {MAX_READ_DATA_LENGTH} 字节"),
            Some(serde_json::json!({ "length": data.len() })),
        ));
    }
    let data_length = u8::try_from(data.len())
        .map_err(|_| cjt_error("CJT188_DATA_TOO_LONG", "CJ/T 188 数据域长度溢出", None))?;
    let mut frame = Vec::with_capacity(usize::from(preamble_count) + 12 + data.len());
    frame.extend(std::iter::repeat_n(PREAMBLE, usize::from(preamble_count)));
    let core_start = frame.len();
    frame.push(FRAME_START);
    frame.push(meter_type.code());
    frame.extend_from_slice(&address);
    frame.extend_from_slice(&[control, data_length]);
    frame.extend_from_slice(data);
    frame.push(checksum(&frame[core_start..]));
    frame.push(FRAME_END);
    Ok(frame)
}

pub fn build_read_request(
    meter_type: MeterType,
    address: &str,
    data_id: &str,
    sequence: u8,
    preamble_count: u8,
) -> Result<Vec<u8>, CoreError> {
    let normalized_address = address.trim();
    if normalized_address.eq_ignore_ascii_case(BROADCAST_ADDRESS) {
        return Err(cjt_error(
            "CJT188_BROADCAST_READ_FORBIDDEN",
            "CJ/T 188 本轮读数据不允许使用 AA×7 广播地址",
            None,
        ));
    }
    let address = parse_address(normalized_address)?;
    let data_id = parse_data_identifier(data_id)?;
    let mut data = data_id.wire_bytes;
    data.push(sequence);
    build_frame(
        meter_type,
        address,
        READ_DATA_CONTROL,
        &data,
        preamble_count,
    )
}

pub fn parse_frame(bytes: &[u8]) -> Result<Frame, CoreError> {
    let preamble_count = bytes.iter().take_while(|byte| **byte == PREAMBLE).count();
    if preamble_count > usize::from(MAX_PREAMBLE_COUNT) {
        return Err(cjt_error(
            "CJT188_PREAMBLE_INVALID",
            "CJ/T 188 帧前最多允许 4 个 FE 前导字节",
            Some(serde_json::json!({ "preambleCount": preamble_count })),
        ));
    }
    let core = bytes
        .get(preamble_count..)
        .ok_or_else(|| cjt_error("CJT188_FRAME_TRUNCATED", "CJ/T 188 帧为空", None))?;
    if core.len() < 13 {
        return Err(cjt_error(
            "CJT188_FRAME_TRUNCATED",
            "CJ/T 188 帧短于最小 13 字节",
            Some(serde_json::json!({ "length": core.len() })),
        ));
    }
    if core[0] != FRAME_START {
        return Err(cjt_error(
            "CJT188_FRAME_START_INVALID",
            "CJ/T 188-2004 帧只有一个起始 68H，位于类型字节之前",
            Some(serde_json::json!({ "first": core[0] })),
        ));
    }
    let meter_type = MeterType::from_code(core[1]).ok_or_else(|| {
        cjt_error(
            "CJT188_METER_TYPE_UNCONFIRMED",
            "CJ/T 188 帧表类型未在本轮确认范围内（10H/11H/20H/30H）",
            Some(serde_json::json!({ "meterTypeCode": core[1] })),
        )
    })?;
    // Layout after the optional preamble: 68 T A0..A6 C L DATA CS 16.
    let data_length = usize::from(core[10]);
    if data_length > MAX_READ_DATA_LENGTH {
        return Err(cjt_error(
            "CJT188_DATA_TOO_LONG",
            format!("CJ/T 188 只读数据域不能超过 {MAX_READ_DATA_LENGTH} 字节"),
            Some(serde_json::json!({ "length": data_length })),
        ));
    }
    let expected_length = 13 + data_length;
    if core.len() != expected_length {
        return Err(cjt_error(
            "CJT188_FRAME_LENGTH_MISMATCH",
            format!(
                "CJ/T 188 帧长度不符，期望 {expected_length}，实际 {}",
                core.len()
            ),
            Some(serde_json::json!({ "expected": expected_length, "actual": core.len() })),
        ));
    }
    if core[expected_length - 1] != FRAME_END {
        return Err(cjt_error(
            "CJT188_FRAME_END_INVALID",
            "CJ/T 188 帧结束字节必须是 16H",
            Some(serde_json::json!({ "actual": core[expected_length - 1] })),
        ));
    }
    let expected_checksum = checksum(&core[..expected_length - 2]);
    let actual_checksum = core[expected_length - 2];
    if actual_checksum != expected_checksum {
        return Err(cjt_error(
            "CJT188_CHECKSUM_MISMATCH",
            "CJ/T 188 算术和校验失败",
            Some(serde_json::json!({
                "expected": expected_checksum,
                "actual": actual_checksum
            })),
        ));
    }
    let address_bytes: [u8; 7] = core[2..9].try_into().expect("fixed address slice");
    let address = format_address(&address_bytes)?;
    let control = core[9];
    Ok(Frame {
        preamble_count: u8::try_from(preamble_count).expect("bounded preamble"),
        meter_type,
        meter_type_code: core[1],
        address,
        address_bytes,
        control,
        data_length: core[9],
        data: core[11..11 + data_length].to_vec(),
        checksum: actual_checksum,
        is_response: control & 0x80 != 0,
        is_exception: control & 0x40 != 0,
        has_follow_frame: control & 0x20 != 0,
        function: control & 0x1F,
    })
}

fn flow_value(meter_type: MeterType, payload: &[u8]) -> Result<KnownValue, CoreError> {
    if payload.len() != 6 {
        return Err(cjt_error(
            "CJT188_VALUE_LENGTH_INVALID",
            "CJ/T 188 901F 读响应载荷必须是 4 字节 BCD 流量 + 2 字节状态",
            Some(serde_json::json!({ "expected": 6, "actual": payload.len() })),
        ));
    }
    let data = &payload[..4];
    for (index, byte) in data.iter().enumerate() {
        validate_bcd(*byte, "cumulative flow", index)?;
    }
    let mut digits = String::with_capacity(8);
    for byte in data.iter().rev() {
        digits.push(char::from(b'0' + (byte >> 4)));
        digits.push(char::from(b'0' + (byte & 0x0F)));
    }
    let display = format!("{}.{}", &digits[..6], &digits[6..]);
    let status = payload[4];
    let mut flags = Vec::new();
    if status & 0x01 != 0 {
        flags.push("valveClosed");
    }
    if status & 0x02 != 0 {
        flags.push("valveAbnormal");
    }
    if status & 0x04 != 0 {
        flags.push("batteryUndervoltage");
    }
    let vendor_bits: Vec<u8> = (3..8)
        .filter(|bit| status & (1 << bit) != 0)
        .map(|bit| u8::try_from(bit).expect("bounded bit"))
        .collect();
    if !vendor_bits.is_empty() {
        flags.push("vendorDefined");
    }
    Ok(KnownValue {
        kind: "cumulativeFlow",
        value: serde_json::json!({
            "decimal": display,
            "rawDigits": digits,
            "statusRaw": status,
            "statusFlags": flags,
            "vendorStatusBits": vendor_bits,
            "reservedStatusByte": payload[5],
        }),
        unit: meter_type.cumulative_flow_unit(),
    })
}

pub fn decode_known_value(
    meter_type: MeterType,
    data_id: &str,
    data: &[u8],
) -> Result<Option<KnownValue>, CoreError> {
    match (meter_type, data_id) {
        (MeterType::ColdWater | MeterType::HotWater | MeterType::Gas, "901F") => {
            flow_value(meter_type, data).map(Some)
        }
        // Heat-meter 901F and every unknown identifier stay raw; their units,
        // scales and vendor extensions are not inferred from DL/T or Nexus.Cjt.
        _ => Ok(None),
    }
}

pub fn parse_read_response(
    bytes: &[u8],
    meter_type: MeterType,
    expected_address: &str,
    expected_data_id: &str,
    expected_sequence: u8,
) -> Result<ReadResponse, CoreError> {
    let expected_address = expected_address.trim();
    let parsed_address = parse_address(expected_address)?;
    let expected_data_id = parse_data_identifier(expected_data_id)?;
    let frame = parse_frame(bytes)?;

    if frame.meter_type != meter_type {
        return Err(cjt_error(
            "CJT188_METER_TYPE_MISMATCH",
            "CJ/T 188 响应表类型与请求不一致",
            Some(serde_json::json!({
                "expected": meter_type.code(),
                "actual": frame.meter_type_code
            })),
        ));
    }
    if frame.control != NORMAL_READ_RESPONSE_CONTROL && frame.control != ERROR_READ_RESPONSE_CONTROL
    {
        return Err(cjt_error(
            "CJT188_RESPONSE_CONTROL_INVALID",
            "CJ/T 188-2004 本轮只接受 81H 普通读响应或 C1H 异常读响应",
            Some(serde_json::json!({ "control": frame.control })),
        ));
    }
    if frame.address_bytes != parsed_address {
        return Err(cjt_error(
            "CJT188_ADDRESS_MISMATCH",
            "CJ/T 188 响应表地址与请求不一致",
            Some(serde_json::json!({
                "expected": format_address(&parsed_address)
                    .expect("validated address must format"),
                "actual": frame.address
            })),
        ));
    }
    if frame.data.len() < 3 {
        return Err(cjt_error(
            "CJT188_RESPONSE_DATA_TRUNCATED",
            "CJ/T 188 响应缺少 DI0 DI1 SER 回显前缀",
            Some(serde_json::json!({ "expectedMinimum": 3, "actual": frame.data.len() })),
        ));
    }
    let actual_data_id = frame.data[0..2]
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<String>();
    if actual_data_id != expected_data_id.canonical {
        return Err(cjt_error(
            "CJT188_DATA_ID_MISMATCH",
            "CJ/T 188 响应数据标识与请求不一致",
            Some(serde_json::json!({
                "expected": expected_data_id.canonical,
                "actual": actual_data_id
            })),
        ));
    }
    let actual_sequence = frame.data[2];
    if actual_sequence != expected_sequence {
        return Err(cjt_error(
            "CJT188_SEQUENCE_MISMATCH",
            "CJ/T 188 响应序列号 SER 与请求不一致",
            Some(serde_json::json!({
                "expected": expected_sequence,
                "actual": actual_sequence
            })),
        ));
    }
    let payload = frame.data[3..].to_vec();

    if frame.control == ERROR_READ_RESPONSE_CONTROL {
        return Ok(ReadResponse {
            meter_type,
            meter_type_code: frame.meter_type_code,
            address: frame.address,
            data_id: actual_data_id,
            sequence: actual_sequence,
            control: frame.control,
            payload: payload.clone(),
            known_value: None,
            exception: Some(MeterException {
                control: frame.control,
                payload,
            }),
            read_only: true,
            transport: "serial",
        });
    }

    let known_value = decode_known_value(meter_type, &actual_data_id, &payload)?;
    Ok(ReadResponse {
        meter_type,
        meter_type_code: frame.meter_type_code,
        address: frame.address,
        data_id: actual_data_id,
        sequence: actual_sequence,
        control: frame.control,
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
        meter_type: MeterType,
        address: &str,
        data_id: &str,
        sequence: u8,
        payload: &[u8],
        control: u8,
    ) -> Vec<u8> {
        let mut data = parse_data_identifier(data_id).unwrap().wire_bytes;
        data.push(sequence);
        data.extend_from_slice(payload);
        build_frame(
            meter_type,
            parse_address(address).unwrap(),
            control,
            &data,
            0,
        )
        .unwrap()
    }

    #[test]
    fn address_is_fourteen_bcd_digits_and_low_pair_first() {
        let bytes = parse_address("11223344556677").unwrap();
        assert_eq!(bytes, [0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11]);
        assert_eq!(format_address(&bytes).unwrap(), "11223344556677");
        assert_eq!(parse_address("AAAAAAAAAAAAAA").unwrap(), [0xAA; 7]);
        assert_eq!(format_address(&[0xAA; 7]).unwrap(), BROADCAST_ADDRESS);
        assert!(parse_address("1122334455667").is_err());
        assert!(parse_address("112233445566ZZ").is_err());
        assert!(parse_address("1122334455667A").is_err());
    }

    #[test]
    fn meter_types_reject_electric_and_wildcard() {
        assert_eq!("gas".parse::<MeterType>().unwrap(), MeterType::Gas);
        assert_eq!("cold-water".parse::<MeterType>().unwrap().code(), 0x10);
        assert_eq!("hot-water".parse::<MeterType>().unwrap().code(), 0x11);
        assert_eq!("heat".parse::<MeterType>().unwrap().code(), 0x20);
        let electric = "electric".parse::<MeterType>().unwrap_err();
        assert_eq!(error_code(&electric), "CJT188_METER_TYPE_UNCONFIRMED");
        assert!(parse_frame(&[0x68, 0x40, 0, 0, 0, 0, 0, 0, 0, 0x01, 0x00, 0x00, 0x16]).is_err());
    }

    fn error_code(error: &CoreError) -> &'static str {
        match error {
            CoreError::Modbus { code, .. } => code,
            _ => "UNEXPECTED_ERROR",
        }
    }

    #[test]
    fn builds_exact_audited_request_vector_with_sum_checksum() {
        let request =
            build_read_request(MeterType::ColdWater, "11223344556677", "901F", 0x01, 0).unwrap();
        assert_eq!(
            request,
            [
                0x68, 0x10, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x01, 0x03, 0x90, 0x1F, 0x01,
                0x08, 0x16,
            ]
        );
        let with_preamble =
            build_read_request(MeterType::ColdWater, "11223344556677", "901F", 0x01, 3).unwrap();
        assert_eq!(&with_preamble[..3], &[0xFE, 0xFE, 0xFE]);
        assert_eq!(&with_preamble[3..], &request[..]);
    }

    #[test]
    fn parses_audited_response_and_decodes_flow_and_status() {
        let bytes = [
            0xFE, 0xFE, 0xFE, 0x68, 0x10, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x81, 0x09,
            0x90, 0x1F, 0x01, 0x78, 0x56, 0x34, 0x12, 0x00, 0xFF, 0xA1, 0x16,
        ];
        let parsed =
            parse_read_response(&bytes, MeterType::ColdWater, "11223344556677", "901F", 0x01)
                .unwrap();
        assert_eq!(parsed.control, 0x81);
        assert_eq!(parsed.data_id, "901F");
        assert_eq!(parsed.sequence, 0x01);
        let known = parsed.known_value.unwrap();
        assert_eq!(known.value["decimal"], "123456.78");
        assert_eq!(known.value["statusRaw"], 0);
        assert_eq!(known.unit, Some("m3"));

        let valve = response(
            MeterType::Gas,
            "11223344556677",
            "901F",
            0x02,
            &[0x78, 0x56, 0x34, 0x12, 0x07, 0xFF],
            0x81,
        );
        let parsed =
            parse_read_response(&valve, MeterType::Gas, "11223344556677", "901F", 0x02).unwrap();
        let known = parsed.known_value.unwrap();
        assert_eq!(
            known.value["statusFlags"],
            serde_json::json!(["valveClosed", "valveAbnormal", "batteryUndervoltage"])
        );
        assert_eq!(known.value["vendorStatusBits"], serde_json::json!([]));
    }

    #[test]
    fn heat_meter_901f_stays_raw_and_error_response_keeps_payload() {
        let heat = response(
            MeterType::Heat,
            "11223344556677",
            "901F",
            0x01,
            &[0x78, 0x56, 0x34, 0x12, 0x00, 0xFF],
            0x81,
        );
        let parsed =
            parse_read_response(&heat, MeterType::Heat, "11223344556677", "901F", 0x01).unwrap();
        assert!(parsed.known_value.is_none());
        assert_eq!(parsed.payload, [0x78, 0x56, 0x34, 0x12, 0x00, 0xFF]);

        let error = response(
            MeterType::Gas,
            "11223344556677",
            "901F",
            0x01,
            &[0x02],
            0xC1,
        );
        let parsed =
            parse_read_response(&error, MeterType::Gas, "11223344556677", "901F", 0x01).unwrap();
        let exception = parsed.exception.unwrap();
        assert_eq!(exception.control, 0xC1);
        assert_eq!(exception.payload, [0x02]);
        assert!(parsed.known_value.is_none());
    }

    #[test]
    fn rejects_checksum_broadcast_echo_and_unconfirmed_control() {
        let mut bad = response(
            MeterType::ColdWater,
            "11223344556677",
            "901F",
            0x01,
            &[0x78, 0x56, 0x34, 0x12, 0x00, 0xFF],
            0x81,
        );
        let last = bad.len() - 2;
        bad[last] = bad[last].wrapping_add(1);
        let error = parse_frame(&bad).unwrap_err();
        assert_eq!(error_code(&error), "CJT188_CHECKSUM_MISMATCH");

        let broadcast =
            build_read_request(MeterType::Gas, BROADCAST_ADDRESS, "901F", 0x01, 0).unwrap_err();
        assert_eq!(error_code(&broadcast), "CJT188_BROADCAST_READ_FORBIDDEN");

        let wrong_sequence = response(
            MeterType::Gas,
            "11223344556677",
            "901F",
            0x02,
            &[0x78, 0x56, 0x34, 0x12, 0x00, 0xFF],
            0x81,
        );
        let error = parse_read_response(
            &wrong_sequence,
            MeterType::Gas,
            "11223344556677",
            "901F",
            0x01,
        )
        .unwrap_err();
        assert_eq!(error_code(&error), "CJT188_SEQUENCE_MISMATCH");

        let follow_up = response(
            MeterType::Gas,
            "11223344556677",
            "901F",
            0x01,
            &[0x78, 0x56, 0x34, 0x12, 0x00, 0xFF],
            0xA1,
        );
        let error = parse_read_response(&follow_up, MeterType::Gas, "11223344556677", "901F", 0x01)
            .unwrap_err();
        assert_eq!(error_code(&error), "CJT188_RESPONSE_CONTROL_INVALID");
    }

    #[test]
    fn data_id_is_canonical_order_and_response_prefix_is_validated() {
        let identifier = parse_data_identifier("90 1F").unwrap();
        assert_eq!(identifier.canonical, "901F");
        assert_eq!(identifier.wire_bytes, [0x90, 0x1F]);
        assert!(parse_data_identifier("901F00").is_err());

        let wrong_id = response(
            MeterType::Gas,
            "11223344556677",
            "9020",
            0x01,
            &[0x78, 0x56, 0x34, 0x12, 0x00, 0xFF],
            0x81,
        );
        let error = parse_read_response(&wrong_id, MeterType::Gas, "11223344556677", "901F", 0x01)
            .unwrap_err();
        assert_eq!(error_code(&error), "CJT188_DATA_ID_MISMATCH");
    }
}
