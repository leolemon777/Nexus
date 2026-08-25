//! Inovance H3U/H5U Modbus vendor-address profiles.
//!
//! H3U and H5U use standard Modbus RTU/TCP transports.  This module only
//! translates documented soft-device names to a Modbus area, address and
//! function-code profile.  AM/AC/Easy variants are intentionally not mapped
//! here until a model-specific address table is supplied.

use crate::error::CoreError;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Series {
    H3u,
    H5u,
    Am,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AccessKind {
    Auto,
    Bit,
    Word,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Address {
    pub series: Series,
    pub canonical: String,
    pub area: String,
    pub modbus_address: u16,
    pub read_function: u8,
    pub write_function: Option<u8>,
    pub is_bit: bool,
    pub read_only: bool,
    pub register_width: u8,
}

fn error(code: &'static str, message: impl Into<String>, address: &str) -> CoreError {
    CoreError::Modbus {
        code,
        message: message.into(),
        details: Some(serde_json::json!({ "address": address })),
    }
}

fn invalid(message: impl Into<String>, address: &str) -> CoreError {
    error("INOVANCE_PARAM_INVALID", message, address)
}

fn unsupported(message: impl Into<String>, address: &str) -> CoreError {
    error("INOVANCE_ADDRESS_UNSUPPORTED", message, address)
}

fn checked_address(value: u32, address: &str) -> Result<u16, CoreError> {
    u16::try_from(value)
        .map_err(|_| invalid(format!("Modbus 地址超出 0..65535: {address}"), address))
}

fn parse_decimal(text: &str, minimum: u32, maximum: u32, original: &str) -> Result<u32, CoreError> {
    if text.is_empty() || !text.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(invalid(
            format!("地址数字必须是十进制整数: {original}"),
            original,
        ));
    }
    let value = text
        .parse::<u32>()
        .map_err(|_| invalid(format!("地址数字溢出: {original}"), original))?;
    if !(minimum..=maximum).contains(&value) {
        return Err(invalid(
            format!("地址范围应为 {minimum}..{maximum}: {original}"),
            original,
        ));
    }
    Ok(value)
}

fn parse_octal_location(text: &str, maximum: u32, original: &str) -> Result<u32, CoreError> {
    let (group, bit) = match text.split_once('.') {
        Some((group, bit)) => {
            if bit.is_empty() || !bit.bytes().all(|byte| byte.is_ascii_digit()) {
                return Err(invalid(
                    format!("X/Y 点号位必须是 0..7: {original}"),
                    original,
                ));
            }
            let bit = bit
                .parse::<u32>()
                .map_err(|_| invalid(format!("X/Y 点号位无效: {original}"), original))?;
            if bit > 7 {
                return Err(invalid(
                    format!("X/Y 点号位必须是 0..7: {original}"),
                    original,
                ));
            }
            (group, Some(bit))
        }
        None => (text, None),
    };
    if group.is_empty() || !group.bytes().all(|byte| (b'0'..=b'7').contains(&byte)) {
        return Err(invalid(
            format!("X/Y 地址必须使用八进制: {original}"),
            original,
        ));
    }
    let group = u32::from_str_radix(group, 8)
        .map_err(|_| invalid(format!("X/Y 八进制地址无效: {original}"), original))?;
    let value = group
        .checked_mul(if bit.is_some() { 8 } else { 1 })
        .and_then(|value| bit.map_or(Some(value), |bit| value.checked_add(bit)))
        .ok_or_else(|| invalid(format!("X/Y 地址溢出: {original}"), original))?;
    if value > maximum {
        return Err(invalid(
            format!("X/Y 八进制地址超出范围 0..{maximum:o}: {original}"),
            original,
        ));
    }
    Ok(value)
}

fn split_prefix(input: &str) -> Option<(&str, &str)> {
    let index = input.find(|character: char| character.is_ascii_digit())?;
    Some((&input[..index], &input[index..]))
}

fn bit_address(
    series: Series,
    canonical: &str,
    area: &str,
    address: u32,
    write_function: Option<u8>,
) -> Result<Address, CoreError> {
    Ok(Address {
        series,
        canonical: canonical.to_string(),
        area: area.to_string(),
        modbus_address: checked_address(address, canonical)?,
        read_function: 0x01,
        write_function,
        is_bit: true,
        read_only: write_function.is_none(),
        register_width: 1,
    })
}

fn word_address(
    series: Series,
    canonical: &str,
    address: u32,
    write_function: u8,
    register_width: u8,
) -> Result<Address, CoreError> {
    Ok(Address {
        series,
        canonical: canonical.to_string(),
        area: "holding-register".to_string(),
        modbus_address: checked_address(address, canonical)?,
        read_function: 0x03,
        write_function: Some(write_function),
        is_bit: false,
        read_only: false,
        register_width,
    })
}

fn parse_h3u_bit(
    prefix: &str,
    numeric: &str,
    original: &str,
    canonical: &str,
) -> Result<Address, CoreError> {
    match prefix {
        "M" => {
            let value = parse_decimal(numeric, 0, 8511, original)?;
            if (7680..8000).contains(&value) {
                return Err(unsupported(
                    "H3U M 仅支持 M0..M7679 与 M8000..M8511",
                    original,
                ));
            }
            let address = if value < 7680 {
                value
            } else {
                0x2400 + value - 8000
            };
            bit_address(Series::H3u, canonical, "coil", address, Some(0x05))
        }
        "SM" => bit_address(
            Series::H3u,
            canonical,
            "coil",
            0x2400 + parse_decimal(numeric, 0, 1023, original)?,
            Some(0x05),
        ),
        "S" => bit_address(
            Series::H3u,
            canonical,
            "coil",
            0xE000 + parse_decimal(numeric, 0, 4095, original)?,
            Some(0x05),
        ),
        "T" => bit_address(
            Series::H3u,
            canonical,
            "coil",
            0xF000 + parse_decimal(numeric, 0, 511, original)?,
            Some(0x05),
        ),
        "C" => bit_address(
            Series::H3u,
            canonical,
            "coil",
            0xF400 + parse_decimal(numeric, 0, 255, original)?,
            Some(0x05),
        ),
        "X" => bit_address(
            Series::H3u,
            canonical,
            "coil",
            0xF800 + parse_octal_location(numeric, 0o377, original)?,
            None,
        ),
        "Y" => bit_address(
            Series::H3u,
            canonical,
            "coil",
            0xFC00 + parse_octal_location(numeric, 0o377, original)?,
            Some(0x05),
        ),
        _ => Err(unsupported("H3U 位地址支持 M/SM/S/T/C/X/Y", original)),
    }
}

fn parse_h3u_word(
    prefix: &str,
    numeric: &str,
    original: &str,
    canonical: &str,
) -> Result<Address, CoreError> {
    if numeric.contains('.') {
        return Err(unsupported("H3U 字地址不支持字内点寻址", original));
    }
    match prefix {
        "D" => word_address(
            Series::H3u,
            canonical,
            parse_decimal(numeric, 0, 8511, original)?,
            0x06,
            1,
        ),
        "SD" => word_address(
            Series::H3u,
            canonical,
            0x2400 + parse_decimal(numeric, 0, 1023, original)?,
            0x06,
            1,
        ),
        "R" => word_address(
            Series::H3u,
            canonical,
            0x3000 + parse_decimal(numeric, 0, 32767, original)?,
            0x10,
            1,
        ),
        "T" => word_address(
            Series::H3u,
            canonical,
            0xF000 + parse_decimal(numeric, 0, 511, original)?,
            0x06,
            1,
        ),
        "C" => {
            let value = parse_decimal(numeric, 0, 255, original)?;
            if value < 200 {
                word_address(Series::H3u, canonical, 0xF400 + value, 0x06, 1)
            } else {
                word_address(Series::H3u, canonical, 0xF700 + (value - 200) * 2, 0x10, 2)
            }
        }
        _ => Err(unsupported("H3U 字地址支持 D/SD/R/T/C", original)),
    }
}

fn parse_h5u_bit(
    prefix: &str,
    numeric: &str,
    original: &str,
    canonical: &str,
) -> Result<Address, CoreError> {
    match prefix {
        "M" => bit_address(
            Series::H5u,
            canonical,
            "coil",
            parse_decimal(numeric, 0, 7999, original)?,
            Some(0x05),
        ),
        "B" => bit_address(
            Series::H5u,
            canonical,
            "coil",
            0x3000 + parse_decimal(numeric, 0, 32767, original)?,
            Some(0x05),
        ),
        "S" => bit_address(
            Series::H5u,
            canonical,
            "coil",
            0xE000 + parse_decimal(numeric, 0, 4095, original)?,
            Some(0x05),
        ),
        "X" => bit_address(
            Series::H5u,
            canonical,
            "coil",
            0xF800 + parse_octal_location(numeric, 0o1777, original)?,
            None,
        ),
        "Y" => bit_address(
            Series::H5u,
            canonical,
            "coil",
            0xFC00 + parse_octal_location(numeric, 0o1777, original)?,
            Some(0x05),
        ),
        _ => Err(unsupported("H5U 位地址支持 M/B/S/X/Y", original)),
    }
}

fn parse_h5u_word(
    prefix: &str,
    numeric: &str,
    original: &str,
    canonical: &str,
) -> Result<Address, CoreError> {
    if numeric.contains('.') {
        return Err(unsupported("H5U 字地址不支持字内点寻址", original));
    }
    match prefix {
        "D" => word_address(
            Series::H5u,
            canonical,
            parse_decimal(numeric, 0, 7999, original)?,
            0x06,
            1,
        ),
        "R" => word_address(
            Series::H5u,
            canonical,
            0x3000 + parse_decimal(numeric, 0, 32767, original)?,
            0x10,
            1,
        ),
        _ => Err(unsupported("H5U 字地址支持 D/R", original)),
    }
}

pub fn parse_address(series: Series, input: &str, kind: AccessKind) -> Result<Address, CoreError> {
    let original = input.trim();
    if original.is_empty() {
        return Err(invalid("汇川软元件地址不能为空", input));
    }
    if matches!(series, Series::Am) {
        return Err(error(
            "INOVANCE_SERIES_UNSUPPORTED",
            "AM/AC/Easy 当前没有经过型号确认的统一 Modbus 地址表；请先提供具体型号、固件和手册",
            original,
        ));
    }
    let normalized = original.to_ascii_uppercase();
    let (prefix, numeric) = split_prefix(&normalized)
        .ok_or_else(|| invalid(format!("地址必须以软元件前缀开头: {original}"), original))?;
    if numeric.is_empty() {
        return Err(invalid("地址缺少数字部分", original));
    }
    let selected = match kind {
        AccessKind::Auto => {
            if ["D", "SD", "R", "T", "C"].contains(&prefix) {
                AccessKind::Word
            } else {
                AccessKind::Bit
            }
        }
        other => other,
    };
    match (series, selected) {
        (Series::H3u, AccessKind::Bit) => parse_h3u_bit(prefix, numeric, original, &normalized),
        (Series::H3u, AccessKind::Word) => parse_h3u_word(prefix, numeric, original, &normalized),
        (Series::H5u, AccessKind::Bit) => parse_h5u_bit(prefix, numeric, original, &normalized),
        (Series::H5u, AccessKind::Word) => parse_h5u_word(prefix, numeric, original, &normalized),
        (Series::Am, _) | (_, AccessKind::Auto) => unreachable!(),
    }
}

pub fn series_name(series: Series) -> &'static str {
    match series {
        Series::H3u => "H3U",
        Series::H5u => "H5U",
        Series::Am => "AM",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn h3u_bit_word_and_counter_mappings() {
        let x = parse_address(Series::H3u, "X10", AccessKind::Bit).unwrap();
        assert_eq!(
            (x.modbus_address, x.read_function, x.write_function),
            (0xF808, 1, None)
        );
        assert_eq!(
            parse_address(Series::H3u, "Y17", AccessKind::Bit)
                .unwrap()
                .modbus_address,
            0xFC0F
        );
        assert_eq!(
            parse_address(Series::H3u, "D8511", AccessKind::Word)
                .unwrap()
                .modbus_address,
            8511
        );
        let c = parse_address(Series::H3u, "C200", AccessKind::Word).unwrap();
        assert_eq!(
            (c.modbus_address, c.write_function, c.register_width),
            (0xF700, Some(0x10), 2)
        );
    }

    #[test]
    fn h5u_boundaries_and_octal_points() {
        assert_eq!(
            parse_address(Series::H5u, "Y1777", AccessKind::Bit)
                .unwrap()
                .modbus_address,
            0xFFFF
        );
        assert_eq!(
            parse_address(Series::H5u, "X177.7", AccessKind::Bit)
                .unwrap()
                .modbus_address,
            0xFFFF - 0x400
        );
        assert_eq!(
            parse_address(Series::H5u, "R32767", AccessKind::Word)
                .unwrap()
                .modbus_address,
            0xAFFF
        );
    }

    #[test]
    fn rejects_gaps_mismatched_kinds_and_am() {
        assert!(parse_address(Series::H3u, "M7680", AccessKind::Bit).is_err());
        assert!(parse_address(Series::H3u, "D100.5", AccessKind::Word).is_err());
        assert!(parse_address(Series::H5u, "Y2000", AccessKind::Bit).is_err());
        assert!(parse_address(Series::Am, "D0", AccessKind::Auto).is_err());
    }
}
