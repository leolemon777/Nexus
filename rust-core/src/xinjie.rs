//! Xinje XC/XD/XL Modbus vendor profile.
//!
//! The legacy audit only confirms the D data-register mapping (zero based,
//! FC03 read / FC06 write).  Other soft-device areas differ by family and are
//! intentionally rejected until a model-specific Xinje manual and capture are
//! supplied.  This module therefore represents an honest, fail-closed profile
//! rather than copying the legacy speculative offsets.

use crate::error::CoreError;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Series {
    Xc,
    Xd,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AccessKind {
    Auto,
    Word,
    Bit,
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
    error("XINJE_PARAM_INVALID", message, address)
}

fn unsupported(message: impl Into<String>, address: &str) -> CoreError {
    error("XINJE_ADDRESS_UNSUPPORTED", message, address)
}

pub fn parse_address(series: Series, input: &str, kind: AccessKind) -> Result<Address, CoreError> {
    let original = input.trim();
    if original.is_empty() {
        return Err(invalid("信捷地址不能为空", input));
    }
    if !matches!(kind, AccessKind::Auto | AccessKind::Word) {
        return Err(unsupported(
            "当前信捷首轮只确认 D 字寄存器；位区等待型号手册",
            original,
        ));
    }
    let normalized = original.to_ascii_uppercase();
    let Some(number) = normalized.strip_prefix('D') else {
        return Err(unsupported(
            "当前信捷 profile 只确认 D 数据寄存器；HD/SD/SM/M/X/Y/C/T/S 等区域必须先提供型号化手册",
            original,
        ));
    };
    if number.is_empty() || !number.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(invalid("D 地址必须是十进制整数", original));
    }
    if number.contains('.') {
        return Err(invalid("D 寄存器不接受点号位寻址", original));
    }
    let value = number
        .parse::<u32>()
        .map_err(|_| invalid("D 地址数字溢出", original))?;
    let address = u16::try_from(value).map_err(|_| invalid("D 地址必须在 0..65535", original))?;
    Ok(Address {
        series,
        canonical: normalized,
        area: "holding-register".to_string(),
        modbus_address: address,
        read_function: 0x03,
        write_function: Some(0x06),
        is_bit: false,
        read_only: false,
        register_width: 1,
    })
}

pub fn series_name(series: Series) -> &'static str {
    match series {
        Series::Xc => "XC",
        Series::Xd => "XD/XL",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn confirms_d_mapping_for_both_families() {
        for series in [Series::Xc, Series::Xd] {
            let parsed = parse_address(series, "D100", AccessKind::Auto).unwrap();
            assert_eq!(parsed.modbus_address, 100);
            assert_eq!((parsed.read_function, parsed.write_function), (3, Some(6)));
            assert!(!parsed.is_bit);
        }
    }

    #[test]
    fn rejects_unconfirmed_regions_and_invalid_input() {
        assert!(parse_address(Series::Xc, "X10", AccessKind::Bit).is_err());
        assert!(parse_address(Series::Xd, "M0", AccessKind::Auto).is_err());
        assert!(parse_address(Series::Xc, "D12.1", AccessKind::Word).is_err());
        assert!(parse_address(Series::Xc, "D65536", AccessKind::Word).is_err());
    }
}
