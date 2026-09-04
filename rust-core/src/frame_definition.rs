//! 自定义帧解析 —— 表单式帧定义(spec-plan-serial-plot-parse-replay.md 批次 2)。
//!
//! 两种模式:
//! - `binary`: 可选帧头 + 定长 + 可选校验(none/sum8/xor8/crc16-modbus) + 字节偏移字段
//! - `ascii-delimited`: 行结束符 + 分隔符 + 字段序号
//!
//! 解析失败一律返回显式错误码,绝不静默。所有错误对渲染层可见:
//! {status:"error", error:{code, message}}。

use serde::{Deserialize, Serialize};

use crate::frame_parser::parse_hex_string;
use crate::modbus_rtu::crc16_modbus;

const FIELD_TYPES: &[(&str, usize)] = &[
    ("u8", 1),
    ("u16", 2),
    ("i16", 2),
    ("u32", 4),
    ("i32", 4),
    ("f32", 4),
];

pub fn field_size(field_type: &str) -> Option<usize> {
    FIELD_TYPES
        .iter()
        .find(|(name, _)| *name == field_type)
        .map(|(_, size)| *size)
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FieldDef {
    pub name: String,
    /// binary 模式必填: 数据起始字节(帧首字节为 0)
    #[serde(default)]
    pub offset: Option<u16>,
    /// ascii-delimited 模式必填: 字段序号(从 0 起)
    #[serde(default)]
    pub index: Option<u16>,
    #[serde(rename = "fieldType", default = "default_field_type")]
    pub field_type_tag: String,
    #[serde(default = "default_byte_order")]
    pub byte_order: String,
    #[serde(default = "default_scale")]
    pub scale: f64,
    #[serde(default)]
    pub unit: String,
}

fn default_field_type() -> String {
    "u16".to_string()
}
fn default_byte_order() -> String {
    "be".to_string()
}
fn default_scale() -> f64 {
    1.0
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ChecksumDef {
    #[serde(rename = "type", default = "default_checksum")]
    pub checksum_type: String,
}

fn default_checksum() -> String {
    "none".to_string()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FrameDefinition {
    #[serde(default)]
    pub schema_version: Option<u16>,
    pub name: String,
    /// "binary" | "ascii-delimited"
    pub mode: String,
    /// binary: 可选帧头 hex 字符串("01 03");空/None 表示不过滤
    #[serde(default)]
    pub head: Option<String>,
    /// binary: 定长(帧总字节数,含校验)
    #[serde(default)]
    pub length: Option<u16>,
    #[serde(default)]
    pub checksum: Option<ChecksumDef>,
    /// ascii-delimited: 行结束符,默认 "\n"
    #[serde(default = "default_line_ending")]
    pub line_ending: String,
    /// ascii-delimited: 字段分隔符,默认 ","
    #[serde(default = "default_separator")]
    pub separator: String,
    #[serde(default)]
    pub fields: Vec<FieldDef>,
}

fn default_line_ending() -> String {
    "\n".to_string()
}
fn default_separator() -> String {
    ",".to_string()
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ParsedField {
    pub name: String,
    pub value: f64,
    pub unit: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameDefError {
    pub code: String,
    pub message: String,
}

impl FrameDefError {
    fn new(code: &str, message: impl Into<String>) -> Self {
        Self {
            code: code.to_string(),
            message: message.into(),
        }
    }
}

fn checksum_tail_len(kind: &str) -> usize {
    match kind {
        "sum8" | "xor8" => 1,
        "crc16-modbus" => 2,
        _ => 0,
    }
}

/// 校验定义本身(表单实时反馈)。返回问题列表;空列表 = 合法。
pub fn validate_definition(def: &FrameDefinition) -> Vec<String> {
    let mut issues = Vec::new();
    if def.name.trim().is_empty() {
        issues.push("名称不能为空".to_string());
    }
    if def.fields.is_empty() {
        issues.push("至少需要一个字段".to_string());
    }
    if def.fields.len() > 64 {
        issues.push("字段数超过上限 64".to_string());
    }
    let mut names = std::collections::HashSet::new();
    for (i, field) in def.fields.iter().enumerate() {
        if field.name.trim().is_empty() {
            issues.push(format!("字段 #{}` 名称不能为空", i + 1));
        }
        if !names.insert(field.name.trim().to_string()) {
            issues.push(format!("字段名重复: {}", field.name));
        }
        if field_size(&field.field_type_tag).is_none() {
            issues.push(format!(
                "字段 {} 类型非法: {}",
                field.name, field.field_type_tag
            ));
        }
        if !(field.scale.is_finite()) {
            issues.push(format!("字段 {} 缩放非法", field.name));
        }
    }
    match def.mode.as_str() {
        "binary" => {
            if let Some(head) = &def.head {
                if let Err(e) = parse_hex_string(head) {
                    issues.push(format!("帧头 HEX 不合法: {e}"));
                }
            }
            if let Some(len) = def.length {
                if len == 0 {
                    issues.push("定长必须大于 0".to_string());
                }
                let tail = checksum_tail_len(
                    def.checksum
                        .as_ref()
                        .map(|c| c.checksum_type.as_str())
                        .unwrap_or("none"),
                );
                for field in &def.fields {
                    let Some(offset) = field.offset else {
                        issues.push(format!("binary 模式字段 {} 缺少字节偏移", field.name));
                        continue;
                    };
                    let Some(size) = field_size(&field.field_type_tag) else {
                        continue;
                    };
                    if offset as usize + size + tail > len as usize {
                        issues.push(format!(
                            "字段 {}({}B@{}) 超出定长 {len}",
                            field.name, size, offset
                        ));
                    }
                }
            } else {
                for field in &def.fields {
                    if field.offset.is_none() {
                        issues.push(format!("binary 模式字段 {} 缺少字节偏移", field.name));
                    }
                }
            }
            if let Some(checksum) = &def.checksum {
                if !matches!(
                    checksum.checksum_type.as_str(),
                    "none" | "sum8" | "xor8" | "crc16-modbus"
                ) {
                    issues.push(format!("校验类型非法: {}", checksum.checksum_type));
                }
            }
        }
        "ascii-delimited" => {
            if def.separator.is_empty() {
                issues.push("分隔符不能为空".to_string());
            }
            for field in &def.fields {
                if field.index.is_none() {
                    issues.push(format!("ascii 模式字段 {} 缺少字段序号", field.name));
                }
            }
        }
        other => issues.push(format!("模式非法: {other}(应为 binary 或 ascii-delimited)")),
    }
    issues
}

fn read_binary_value(
    bytes: &[u8],
    offset: usize,
    field_type: &str,
    little_endian: bool,
) -> Option<f64> {
    let b = bytes;
    match field_type {
        "u8" => Some(*b.get(offset)? as f64),
        "u16" => {
            let lo = *b.get(offset)? as u64;
            let hi = *b.get(offset + 1)? as u64;
            let v = if little_endian {
                lo | (hi << 8)
            } else {
                (lo << 8) | hi
            };
            Some(v as f64)
        }
        "i16" => {
            let lo = *b.get(offset)? as u64;
            let hi = *b.get(offset + 1)? as u64;
            let v = if little_endian {
                lo | (hi << 8)
            } else {
                (lo << 8) | hi
            };
            let signed = if v & 0x8000 != 0 {
                v as i64 - 0x1_0000
            } else {
                v as i64
            };
            Some(signed as f64)
        }
        "u32" => {
            let b0 = *b.get(offset)? as u64;
            let b1 = *b.get(offset + 1)? as u64;
            let b2 = *b.get(offset + 2)? as u64;
            let b3 = *b.get(offset + 3)? as u64;
            let v = if little_endian {
                b0 | (b1 << 8) | (b2 << 16) | (b3 << 24)
            } else {
                (b0 << 24) | (b1 << 16) | (b2 << 8) | b3
            };
            Some(v as f64)
        }
        "i32" => {
            let b0 = *b.get(offset)? as u64;
            let b1 = *b.get(offset + 1)? as u64;
            let b2 = *b.get(offset + 2)? as u64;
            let b3 = *b.get(offset + 3)? as u64;
            let v = if little_endian {
                b0 | (b1 << 8) | (b2 << 16) | (b3 << 24)
            } else {
                (b0 << 24) | (b1 << 16) | (b2 << 8) | b3
            };
            let signed = if v & 0x8000_0000 != 0 {
                v as i64 - 0x1_0000_0000
            } else {
                v as i64
            };
            Some(signed as f64)
        }
        "f32" => {
            let b0 = *b.get(offset)? as u64;
            let b1 = *b.get(offset + 1)? as u64;
            let b2 = *b.get(offset + 2)? as u64;
            let b3 = *b.get(offset + 3)? as u64;
            let bits = if little_endian {
                (b0 | (b1 << 8) | (b2 << 16) | (b3 << 24)) as u32
            } else {
                ((b0 << 24) | (b1 << 16) | (b2 << 8) | b3) as u32
            };
            Some(f32::from_bits(bits) as f64)
        }
        _ => None,
    }
}

fn verify_checksum(bytes: &[u8], kind: &str) -> Result<(), FrameDefError> {
    match kind {
        "none" => Ok(()),
        "sum8" => {
            if bytes.len() < 2 {
                return Err(FrameDefError::new(
                    "FRAME_TOO_SHORT",
                    "帧太短,无法校验 sum8",
                ));
            }
            let expected = bytes[..bytes.len() - 1]
                .iter()
                .fold(0u8, |acc, b| acc.wrapping_add(*b));
            let actual = bytes[bytes.len() - 1];
            if expected == actual {
                Ok(())
            } else {
                Err(FrameDefError::new(
                    "CHECKSUM_MISMATCH",
                    format!("sum8 期望 {expected:02X} 实际 {actual:02X}"),
                ))
            }
        }
        "xor8" => {
            if bytes.len() < 2 {
                return Err(FrameDefError::new(
                    "FRAME_TOO_SHORT",
                    "帧太短,无法校验 xor8",
                ));
            }
            let expected = bytes[..bytes.len() - 1].iter().fold(0u8, |acc, b| acc ^ *b);
            let actual = bytes[bytes.len() - 1];
            if expected == actual {
                Ok(())
            } else {
                Err(FrameDefError::new(
                    "CHECKSUM_MISMATCH",
                    format!("xor8 期望 {expected:02X} 实际 {actual:02X}"),
                ))
            }
        }
        "crc16-modbus" => {
            if bytes.len() < 4 {
                return Err(FrameDefError::new(
                    "FRAME_TOO_SHORT",
                    "帧太短,无法校验 crc16-modbus",
                ));
            }
            let crc = crc16_modbus(&bytes[..bytes.len() - 2]);
            let expected = (crc & 0xff) as u8;
            let expected_hi = ((crc >> 8) & 0xff) as u8;
            let (actual_lo, actual_hi) = (bytes[bytes.len() - 2], bytes[bytes.len() - 1]);
            if expected == actual_lo && expected_hi == actual_hi {
                Ok(())
            } else {
                Err(FrameDefError::new(
                    "CHECKSUM_MISMATCH",
                    format!(
                        "crc16-modbus 期望 {:02X} {:02X} 实际 {:02X} {:02X}",
                        expected, expected_hi, actual_lo, actual_hi
                    ),
                ))
            }
        }
        other => Err(FrameDefError::new(
            "FRAME_DEF_INVALID",
            format!("未知校验类型: {other}"),
        )),
    }
}

fn parse_binary(bytes: &[u8], def: &FrameDefinition) -> Result<Vec<ParsedField>, FrameDefError> {
    if let Some(head_hex) = &def.head {
        let head = parse_hex_string(head_hex).map_err(|e| {
            FrameDefError::new("FRAME_DEF_INVALID", format!("帧头 HEX 不合法: {e}"))
        })?;
        if !head.is_empty() {
            if bytes.len() < head.len() {
                return Err(FrameDefError::new("HEAD_MISMATCH", "帧比帧头还短"));
            }
            if bytes[..head.len()] != head[..] {
                return Err(FrameDefError::new("HEAD_MISMATCH", "帧头不匹配"));
            }
        }
    }
    if let Some(len) = def.length {
        if bytes.len() != len as usize {
            return Err(FrameDefError::new(
                "LENGTH_MISMATCH",
                format!("定长 {} 实际 {} 字节", len, bytes.len()),
            ));
        }
    }
    let checksum_kind = def
        .checksum
        .as_ref()
        .map(|c| c.checksum_type.as_str())
        .unwrap_or("none");
    verify_checksum(bytes, checksum_kind)?;
    let tail = checksum_tail_len(checksum_kind);
    let data_end = bytes.len().saturating_sub(tail);
    let mut result = Vec::with_capacity(def.fields.len());
    for field in &def.fields {
        let Some(offset) = field.offset else {
            return Err(FrameDefError::new(
                "FRAME_DEF_INVALID",
                format!("字段 {} 缺少字节偏移", field.name),
            ));
        };
        let Some(size) = field_size(&field.field_type_tag) else {
            return Err(FrameDefError::new(
                "FRAME_DEF_INVALID",
                format!("字段 {} 类型非法", field.name),
            ));
        };
        if offset as usize + size > data_end {
            return Err(FrameDefError::new(
                "FIELD_OUT_OF_RANGE",
                format!("字段 {}({}B@{}) 超出数据区", field.name, size, offset),
            ));
        }
        let little_endian = field.byte_order.eq_ignore_ascii_case("le");
        let Some(raw) =
            read_binary_value(bytes, offset as usize, &field.field_type_tag, little_endian)
        else {
            return Err(FrameDefError::new(
                "FIELD_OUT_OF_RANGE",
                format!("字段 {} 读取失败", field.name),
            ));
        };
        result.push(ParsedField {
            name: field.name.clone(),
            value: raw * field.scale,
            unit: field.unit.clone(),
        });
    }
    Ok(result)
}

fn parse_ascii(bytes: &[u8], def: &FrameDefinition) -> Result<Vec<ParsedField>, FrameDefError> {
    let mut text = String::from_utf8_lossy(bytes).to_string();
    // 容忍 \r\n / \n / \r 任一行尾(剥离一次)
    for ending in ["\r\n", "\n", "\r"] {
        if let Some(stripped) = text.strip_suffix(ending) {
            text = stripped.to_string();
            break;
        }
    }
    let parts: Vec<&str> = text.split(def.separator.as_str()).collect();
    let mut result = Vec::with_capacity(def.fields.len());
    for field in &def.fields {
        let Some(index) = field.index else {
            return Err(FrameDefError::new(
                "FRAME_DEF_INVALID",
                format!("字段 {} 缺少字段序号", field.name),
            ));
        };
        let Some(part) = parts.get(index as usize) else {
            return Err(FrameDefError::new(
                "ASCII_FIELD_MISSING",
                format!(
                    "字段 {} 序号 {} 不存在(共 {} 段)",
                    field.name,
                    index,
                    parts.len()
                ),
            ));
        };
        let trimmed = part.trim();
        let value: f64 = trimmed.parse().map_err(|_| {
            FrameDefError::new(
                "ASCII_PARSE_FAILED",
                format!("字段 {} 的 \"{}\" 不是数字", field.name, trimmed),
            )
        })?;
        if !value.is_finite() {
            return Err(FrameDefError::new(
                "ASCII_PARSE_FAILED",
                format!("字段 {} 数值非法", field.name),
            ));
        }
        result.push(ParsedField {
            name: field.name.clone(),
            value: value * field.scale,
            unit: field.unit.clone(),
        });
    }
    Ok(result)
}

/// 解析一帧。定义/帧不匹配返回显式错误,绝不静默。
pub fn parse_custom_frame(
    bytes: &[u8],
    def: &FrameDefinition,
) -> Result<Vec<ParsedField>, FrameDefError> {
    match def.mode.as_str() {
        "binary" => parse_binary(bytes, def),
        "ascii-delimited" => parse_ascii(bytes, def),
        other => Err(FrameDefError::new(
            "FRAME_DEF_INVALID",
            format!("模式非法: {other}"),
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::modbus_rtu::crc16_modbus;

    fn temp_def(fields: Vec<FieldDef>) -> FrameDefinition {
        FrameDefinition {
            schema_version: Some(1),
            name: "测试".to_string(),
            mode: "binary".to_string(),
            head: None,
            length: None,
            checksum: None,
            line_ending: "\n".to_string(),
            separator: ",".to_string(),
            fields,
        }
    }

    fn field(name: &str, offset: u16, field_type: &str) -> FieldDef {
        FieldDef {
            name: name.to_string(),
            offset: Some(offset),
            index: None,
            field_type_tag: field_type.to_string(),
            byte_order: "be".to_string(),
            scale: 1.0,
            unit: String::new(),
        }
    }

    #[test]
    fn binary_u16_and_i16_negative() {
        // 帧尾 AB CD → i16 = -21555;偏移 3 大端 u16 = 0x1234
        let bytes = [0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD];
        let def = temp_def(vec![field("a", 3, "u16"), field("b", 5, "i16")]);
        let parsed = parse_custom_frame(&bytes, &def).unwrap();
        assert_eq!(parsed[0].value, 4660.0);
        assert_eq!(parsed[1].value, -21555.0);
    }

    #[test]
    fn binary_byte_order_and_scale_and_f32() {
        let bytes = [0x00, 0x00, 0xC0, 0x3F, 0x01, 0x00];
        let mut f = field("f", 0, "f32");
        f.byte_order = "le".to_string();
        let mut u = field("u", 4, "u16");
        u.byte_order = "le".to_string();
        u.scale = 0.1;
        let parsed = parse_custom_frame(&bytes, &temp_def(vec![f, u])).unwrap();
        assert_eq!(parsed[0].value, 1.5);
        assert!((parsed[1].value - 0.1).abs() < 1e-12); // 1 * 0.1
    }

    #[test]
    fn binary_head_match_and_mismatch() {
        let mut def = temp_def(vec![field("a", 2, "u8")]);
        def.head = Some("01 03".to_string());
        let bytes = [0x01, 0x03, 0x2A];
        assert!(parse_custom_frame(&bytes, &def).is_ok());
        let err = parse_custom_frame(&[0x02, 0x03, 0x2A], &def).unwrap_err();
        assert_eq!(err.code, "HEAD_MISMATCH");
    }

    #[test]
    fn binary_fixed_length_mismatch() {
        let mut def = temp_def(vec![field("a", 0, "u8")]);
        def.length = Some(4);
        let err = parse_custom_frame(&[0x01, 0x02, 0x03], &def).unwrap_err();
        assert_eq!(err.code, "LENGTH_MISMATCH");
    }

    #[test]
    fn binary_field_out_of_range_respects_checksum_tail() {
        let mut def = temp_def(vec![field("a", 2, "u16")]);
        def.checksum = Some(ChecksumDef {
            checksum_type: "sum8".to_string(),
        });
        // sum8(01 02 03) = 06;数据区只有 3 字节,字段 @2 需 2 字节 → 越界
        let bytes = [0x01, 0x02, 0x03, 0x06];
        let err = parse_custom_frame(&bytes, &def).unwrap_err();
        assert_eq!(err.code, "FIELD_OUT_OF_RANGE");
    }

    #[test]
    fn binary_crc16_modbus_ok_and_mismatch() {
        let mut def = temp_def(vec![field("t1", 0, "i16"), field("t2", 2, "i16")]);
        def.checksum = Some(ChecksumDef {
            checksum_type: "crc16-modbus".to_string(),
        });
        def.length = Some(6);
        let mut bytes = vec![0x00, 0x64, 0x00, 0xC8, 0, 0]; // 100, 200
        let crc = crc16_modbus(&bytes[..4]);
        bytes[4] = (crc & 0xff) as u8;
        bytes[5] = (crc >> 8) as u8;
        let parsed = parse_custom_frame(&bytes, &def).unwrap();
        assert_eq!(parsed[0].value, 100.0);
        assert_eq!(parsed[1].value, 200.0);
        bytes[4] ^= 0xFF;
        let err = parse_custom_frame(&bytes, &def).unwrap_err();
        assert_eq!(err.code, "CHECKSUM_MISMATCH");
    }

    #[test]
    fn binary_sum8_and_xor8() {
        let mut def = temp_def(vec![field("a", 0, "u8")]);
        def.checksum = Some(ChecksumDef {
            checksum_type: "sum8".to_string(),
        });
        assert!(parse_custom_frame(&[0x0A, 0x0B, 0x15], &def).is_ok()); // 0x0A+0x0B=0x15
        let mut xdef = temp_def(vec![field("a", 0, "u8")]);
        xdef.checksum = Some(ChecksumDef {
            checksum_type: "xor8".to_string(),
        });
        assert!(parse_custom_frame(&[0x0A, 0x0B, 0x01], &xdef).is_ok()); // 0x0A^0x0B=0x01
        let err = parse_custom_frame(&[0x0A, 0x0B, 0x99], &xdef).unwrap_err();
        assert_eq!(err.code, "CHECKSUM_MISMATCH");
    }

    #[test]
    fn ascii_comma_fields_with_scale_and_unit() {
        let mut def = temp_def(vec![]);
        def.mode = "ascii-delimited".to_string();
        def.fields = vec![FieldDef {
            name: "temp".to_string(),
            offset: None,
            index: Some(1),
            field_type_tag: "u16".to_string(),
            byte_order: "be".to_string(),
            scale: 0.1,
            unit: "℃".to_string(),
        }];
        let bytes = b"ST,251,760\n";
        let parsed = parse_custom_frame(bytes, &def).unwrap();
        assert_eq!(parsed[0].name, "temp");
        assert!((parsed[0].value - 25.1).abs() < 1e-12);
        assert_eq!(parsed[0].unit, "℃");
    }

    #[test]
    fn ascii_crlf_and_missing_field() {
        let mut def = temp_def(vec![]);
        def.mode = "ascii-delimited".to_string();
        def.line_ending = "\r\n".to_string();
        def.fields = vec![field_idx("w", 0)];
        assert!(parse_custom_frame(b"12.5\r\n", &def).is_ok());
        let err = parse_custom_frame(b"12.5\r\n", &{
            let mut d = def.clone();
            d.fields[0].index = Some(3);
            d
        })
        .unwrap_err();
        assert_eq!(err.code, "ASCII_FIELD_MISSING");
    }

    fn field_idx(name: &str, index: u16) -> FieldDef {
        FieldDef {
            name: name.to_string(),
            offset: None,
            index: Some(index),
            field_type_tag: "f32".to_string(),
            byte_order: "be".to_string(),
            scale: 1.0,
            unit: String::new(),
        }
    }

    #[test]
    fn ascii_non_numeric_fails() {
        let mut def = temp_def(vec![]);
        def.mode = "ascii-delimited".to_string();
        def.fields = vec![field_idx("x", 0)];
        let err = parse_custom_frame(b"abc\n", &def).unwrap_err();
        assert_eq!(err.code, "ASCII_PARSE_FAILED");
    }

    #[test]
    fn unknown_mode_rejected() {
        let mut def = temp_def(vec![]);
        def.mode = "websocket".to_string();
        let err = parse_custom_frame(&[0x01], &def).unwrap_err();
        assert_eq!(err.code, "FRAME_DEF_INVALID");
    }

    #[test]
    fn validate_definition_reports_issues() {
        let mut def = temp_def(vec![field("a", 0, "u8")]);
        assert!(validate_definition(&def).is_empty());
        def.fields[0].name = " ".to_string();
        def.fields[0].field_type_tag = "f64".to_string();
        def.mode = "haha".to_string();
        let issues = validate_definition(&def);
        assert!(issues.iter().any(|i| i.contains("名称")));
        assert!(issues.iter().any(|i| i.contains("类型非法")));
        assert!(issues.iter().any(|i| i.contains("模式非法")));
    }

    #[test]
    fn validate_definition_binary_bounds_and_head() {
        let mut def = temp_def(vec![field("a", 3, "u16")]);
        def.length = Some(4);
        def.head = Some("ZZ".to_string());
        let issues = validate_definition(&def);
        assert!(issues.iter().any(|i| i.contains("超出定长")));
        assert!(issues.iter().any(|i| i.contains("帧头 HEX 不合法")));
    }

    #[test]
    fn validate_definition_ascii_requires_index() {
        let mut def = temp_def(vec![field("a", 0, "u8")]);
        def.mode = "ascii-delimited".to_string();
        let issues = validate_definition(&def);
        assert!(issues.iter().any(|i| i.contains("缺少字段序号")));
    }

    #[test]
    fn serde_camel_case_roundtrip() {
        let raw = serde_json::json!({
            "schemaVersion": 1,
            "name": "RS485 温度模块",
            "mode": "binary",
            "head": "01 03",
            "length": 7,
            "checksum": { "type": "crc16-modbus" },
            "fields": [
                { "name": "temp1", "offset": 3, "fieldType": "i16", "byteOrder": "be", "scale": 0.1, "unit": "℃" }
            ]
        });
        let def: FrameDefinition = serde_json::from_value(raw).unwrap();
        assert_eq!(def.fields[0].field_type_tag, "i16");
        assert_eq!(def.checksum.as_ref().unwrap().checksum_type, "crc16-modbus");
        assert!((def.fields[0].scale - 0.1).abs() < 1e-12);
        // 未知字段拒绝
        assert!(
            serde_json::from_value::<FrameDefinition>(serde_json::json!({
                "name": "x", "mode": "binary", "fields": [], "who": 1
            }))
            .is_err()
        );
    }
}
