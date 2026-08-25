//! Delta DVP/AS Modbus address profiles.
//!
//! Delta DVP and AS devices use standard Modbus transports, but their
//! soft-device names map to different wire areas and function codes.  This
//! module keeps that vendor mapping separate from the Modbus transport; it
//! deliberately does not claim a complete model matrix or live-device L2.

use crate::error::CoreError;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Series {
    Dvp,
    As,
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
}

fn delta_error(
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
    delta_error(
        "DELTA_PARAM_INVALID",
        message,
        Some(serde_json::json!({ "address": address })),
    )
}

fn parse_decimal(text: &str, minimum: u32, maximum: u32, original: &str) -> Result<u32, CoreError> {
    if text.is_empty() || !text.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(invalid(
            format!("Delta 地址数字部分无效: {original}"),
            original,
        ));
    }
    let value = text
        .parse::<u32>()
        .map_err(|_| invalid(format!("Delta 地址数字部分溢出: {original}"), original))?;
    if !(minimum..=maximum).contains(&value) {
        return Err(invalid(
            format!("Delta 地址超出范围 {minimum}..{maximum}: {original}"),
            original,
        ));
    }
    Ok(value)
}

fn parse_octal(text: &str, maximum: u32, original: &str) -> Result<u32, CoreError> {
    if text.is_empty() || !text.bytes().all(|byte| (b'0'..=b'7').contains(&byte)) {
        return Err(invalid(
            format!("Delta DVP X/Y 地址必须使用八进制数字: {original}"),
            original,
        ));
    }
    let value = u32::from_str_radix(text, 8)
        .map_err(|_| invalid(format!("Delta DVP 八进制地址溢出: {original}"), original))?;
    if value > maximum {
        return Err(invalid(
            format!("Delta DVP 八进制地址超出 0..{maximum:o}: {original}"),
            original,
        ));
    }
    Ok(value)
}

fn bit(
    series: Series,
    canonical: &str,
    area: &str,
    address: u32,
    read_function: u8,
    write_function: Option<u8>,
) -> Address {
    Address {
        series,
        canonical: canonical.to_string(),
        area: area.to_string(),
        modbus_address: address as u16,
        read_function,
        write_function,
        is_bit: true,
        read_only: write_function.is_none(),
    }
}

fn word(
    series: Series,
    canonical: &str,
    area: &str,
    address: u32,
    read_function: u8,
    write_function: Option<u8>,
) -> Address {
    Address {
        series,
        canonical: canonical.to_string(),
        area: area.to_string(),
        modbus_address: address as u16,
        read_function,
        write_function,
        is_bit: false,
        read_only: write_function.is_none(),
    }
}

fn split_prefix(value: &str) -> Option<(&str, &str)> {
    let index = value.find(|character: char| character.is_ascii_digit())?;
    Some((&value[..index], &value[index..]))
}

fn parse_dvp(prefix: &str, numeric: &str, original: &str) -> Result<Address, CoreError> {
    if numeric.contains('.') {
        return Err(invalid(
            format!("Delta DVP 地址不支持点号位寻址: {original}"),
            original,
        ));
    }
    let canonical = format!("{prefix}{numeric}");
    match prefix {
        "S" => Ok(bit(
            Series::Dvp,
            &canonical,
            "S",
            parse_decimal(numeric, 0, 1023, original)?,
            0x01,
            Some(0x05),
        )),
        "X" => Ok(bit(
            Series::Dvp,
            &canonical,
            "X",
            0x0400 + parse_octal(numeric, 0o377, original)?,
            0x02,
            None,
        )),
        "Y" => Ok(bit(
            Series::Dvp,
            &canonical,
            "Y",
            0x0500 + parse_octal(numeric, 0o377, original)?,
            0x01,
            Some(0x05),
        )),
        "T" => Ok(bit(
            Series::Dvp,
            &canonical,
            "T",
            0x0600 + parse_decimal(numeric, 0, 255, original)?,
            0x01,
            Some(0x05),
        )),
        "C" => Ok(bit(
            Series::Dvp,
            &canonical,
            "C",
            0x0E00 + parse_decimal(numeric, 0, 255, original)?,
            0x01,
            Some(0x05),
        )),
        "M" => {
            let value = parse_decimal(numeric, 0, 4095, original)?;
            let address = if value < 1536 {
                0x0800 + value
            } else {
                0xB000 + value - 1536
            };
            Ok(bit(Series::Dvp, &canonical, "M", address, 0x01, Some(0x05)))
        }
        "D" => {
            let value = parse_decimal(numeric, 0, 11_775, original)?;
            let address = if value < 4096 {
                0x1000 + value
            } else {
                0x9000 + value - 4096
            };
            Ok(word(
                Series::Dvp,
                &canonical,
                "D",
                address,
                0x03,
                Some(0x06),
            ))
        }
        _ => Err(invalid(
            format!("Delta DVP 不支持软元件前缀 {prefix}: {original}"),
            original,
        )),
    }
}

fn parse_as_io(
    prefix: &str,
    numeric: &str,
    original: &str,
    bit_base: u32,
    word_base: u32,
    read_only: bool,
) -> Result<Address, CoreError> {
    let canonical = format!("{prefix}{numeric}");
    if let Some((word_text, bit_text)) = numeric.split_once('.') {
        if word_text.is_empty() || bit_text.is_empty() || bit_text.contains('.') {
            return Err(invalid(
                format!("Delta AS 位地址格式无效: {original}"),
                original,
            ));
        }
        let word_index = parse_decimal(word_text, 0, 63, original)?;
        let bit_index = parse_decimal(bit_text, 0, 15, original)?;
        let address = bit_base + word_index * 16 + bit_index;
        Ok(bit(
            Series::As,
            &canonical,
            prefix,
            address,
            if read_only { 0x02 } else { 0x01 },
            if read_only { None } else { Some(0x05) },
        ))
    } else {
        let word_index = parse_decimal(numeric, 0, 63, original)?;
        Ok(word(
            Series::As,
            &canonical,
            prefix,
            word_base + word_index,
            if read_only { 0x04 } else { 0x03 },
            if read_only { None } else { Some(0x06) },
        ))
    }
}

fn parse_as(prefix: &str, numeric: &str, original: &str) -> Result<Address, CoreError> {
    let canonical = format!("{prefix}{numeric}");
    match prefix {
        "M" => {
            if numeric.contains('.') {
                return Err(invalid(
                    format!("Delta AS M 不支持点号位寻址: {original}"),
                    original,
                ));
            }
            Ok(bit(
                Series::As,
                &canonical,
                "M",
                parse_decimal(numeric, 0, 8191, original)?,
                0x01,
                Some(0x05),
            ))
        }
        "SM" => {
            if numeric.contains('.') {
                return Err(invalid(
                    format!("Delta AS SM 不支持点号位寻址: {original}"),
                    original,
                ));
            }
            Ok(bit(
                Series::As,
                &canonical,
                "SM",
                0x4000 + parse_decimal(numeric, 0, 4095, original)?,
                0x01,
                Some(0x05),
            ))
        }
        "S" => {
            if numeric.contains('.') {
                return Err(invalid(
                    format!("Delta AS S 不支持点号位寻址: {original}"),
                    original,
                ));
            }
            Ok(bit(
                Series::As,
                &canonical,
                "S",
                0x5000 + parse_decimal(numeric, 0, 2047, original)?,
                0x01,
                Some(0x05),
            ))
        }
        "X" => parse_as_io("X", numeric, original, 0x6000, 0x8000, true),
        "Y" => parse_as_io("Y", numeric, original, 0xA000, 0xA000, false),
        "T" => Ok(bit(
            Series::As,
            &canonical,
            "T",
            0xE000 + parse_decimal(numeric, 0, 511, original)?,
            0x01,
            Some(0x05),
        )),
        "C" => Ok(bit(
            Series::As,
            &canonical,
            "C",
            0xF000 + parse_decimal(numeric, 0, 511, original)?,
            0x01,
            Some(0x05),
        )),
        "HC" => Ok(bit(
            Series::As,
            &canonical,
            "HC",
            0xFC00 + parse_decimal(numeric, 0, 255, original)?,
            0x01,
            Some(0x05),
        )),
        "D" => {
            if numeric.contains('.') {
                return Err(invalid(
                    format!("Delta AS D 寄存器取位尚未开放: {original}"),
                    original,
                ));
            }
            Ok(word(
                Series::As,
                &canonical,
                "D",
                parse_decimal(numeric, 0, 29_999, original)?,
                0x03,
                Some(0x06),
            ))
        }
        "SR" => {
            if numeric.contains('.') {
                return Err(invalid(
                    format!("Delta AS SR 不支持点号位寻址: {original}"),
                    original,
                ));
            }
            Ok(word(
                Series::As,
                &canonical,
                "SR",
                0xC000 + parse_decimal(numeric, 0, 2047, original)?,
                0x03,
                Some(0x06),
            ))
        }
        "E" => {
            if numeric.contains('.') {
                return Err(invalid(
                    format!("Delta AS E 不支持点号位寻址: {original}"),
                    original,
                ));
            }
            Ok(word(
                Series::As,
                &canonical,
                "E",
                0xFE00 + parse_decimal(numeric, 0, 14, original)?,
                0x03,
                Some(0x06),
            ))
        }
        _ => Err(invalid(
            format!("Delta AS 不支持软元件前缀 {prefix}: {original}"),
            original,
        )),
    }
}

pub fn parse_address(series: Series, input: &str) -> Result<Address, CoreError> {
    let original = input.trim();
    if original.is_empty() {
        return Err(invalid("Delta 地址不能为空", input));
    }
    let normalized = original.to_ascii_uppercase();
    let (prefix, numeric) = split_prefix(&normalized).ok_or_else(|| {
        invalid(
            format!("Delta 地址必须以软元件前缀开头: {original}"),
            original,
        )
    })?;
    if numeric.is_empty() {
        return Err(invalid("Delta 地址缺少数字部分", original));
    }
    match series {
        Series::Dvp => parse_dvp(prefix, numeric, original),
        Series::As => parse_as(prefix, numeric, original),
    }
}

pub fn series_name(series: Series) -> &'static str {
    match series {
        Series::Dvp => "DVP",
        Series::As => "AS",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dvp_published_ranges_and_octal_io() {
        let d = parse_address(Series::Dvp, "D4096").unwrap();
        assert_eq!(
            (d.modbus_address, d.read_function, d.write_function),
            (0x9000, 0x03, Some(0x06))
        );
        assert_eq!(
            parse_address(Series::Dvp, "Y17").unwrap().modbus_address,
            0x050F
        );
        assert_eq!(
            parse_address(Series::Dvp, "M1536").unwrap().modbus_address,
            0xB000
        );
        assert_eq!(
            parse_address(Series::Dvp, "X377").unwrap().read_function,
            0x02
        );
    }

    #[test]
    fn as_io_and_register_ranges() {
        let x = parse_address(Series::As, "X1.2").unwrap();
        assert_eq!(
            (x.modbus_address, x.read_function, x.write_function),
            (0x6012, 0x02, None)
        );
        let y = parse_address(Series::As, "Y1.2").unwrap();
        assert_eq!(
            (y.modbus_address, y.read_function, y.write_function),
            (0xA012, 0x01, Some(0x05))
        );
        assert_eq!(
            parse_address(Series::As, "D29999").unwrap().modbus_address,
            0x752F
        );
        assert!(parse_address(Series::As, "D100.5").is_err());
    }

    #[test]
    fn rejects_malformed_and_out_of_range() {
        for (series, address) in [
            (Series::Dvp, "X378"),
            (Series::Dvp, "D11776"),
            (Series::As, "X64"),
            (Series::As, "E15"),
            (Series::As, "D30000"),
        ] {
            assert!(
                parse_address(series, address).is_err(),
                "{series:?} {address}"
            );
        }
    }
}
