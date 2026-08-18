//! 三菱 MC 协议地址解析器。
//!
//! 解析富文本地址(如 "D100"、"M100"、"X0"、"D100.3"、"TS100")为
//! 结构化地址 { device_code, head_number, is_bit },供 3E/4E 帧编码器使用。
//!
//! 规范来源:《三菱全协议设计文档.md》§6(软元件总表、进制、范围)。
//!
//! 关键规则:
//! - 3E 帧软元件代码是 **1 字节十六进制**(如 D=0xA8),不是 ASCII 字母
//! - 地址进制按区域不同:X/Y 八进制,B/W/ZR 十六进制,其余十进制
//! - 头设备号 3 字节小端,最大 0xFFFFFF

use crate::error::CoreError;

/// 软元件区域定义。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DeviceSpec {
    /// 帧内 1 字节软元件代码(3E/4E Binary)
    pub code: u8,
    /// 地址进制
    pub radix: AddressRadix,
    /// 位元件(true)还是字元件(false)
    pub is_bit: bool,
}

/// 地址进制。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AddressRadix {
    /// 十进制(M/D/S/L/F/V/SM/T/C/Z...)
    Decimal,
    /// 八进制(X/Y/DX/DY)
    Octal,
    /// 十六进制(B/W/SB/SW/ZR)
    Hex,
}

/// 解析后的 MC 地址。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McAddress {
    /// 帧内 1 字节软元件代码
    pub device_code: u8,
    /// 头设备号(已按进制解析为数值)
    pub head_number: u32,
    /// 位元件(true)/字元件(false)
    pub is_bit: bool,
}

/// 软元件总表(§6.1):代号 → 规格。
/// 键为用户输入的字母代号(大写)。
pub fn device_spec(prefix: &str) -> Option<DeviceSpec> {
    use AddressRadix::*;
    let spec = match prefix {
        // 位元件
        "X" => DeviceSpec { code: 0x9C, radix: Octal, is_bit: true },
        "Y" => DeviceSpec { code: 0x9D, radix: Octal, is_bit: true },
        "M" => DeviceSpec { code: 0x90, radix: Decimal, is_bit: true },
        "L" => DeviceSpec { code: 0x92, radix: Decimal, is_bit: true },
        "F" => DeviceSpec { code: 0x93, radix: Decimal, is_bit: true },
        "V" => DeviceSpec { code: 0x94, radix: Decimal, is_bit: true },
        "B" => DeviceSpec { code: 0xA0, radix: Hex, is_bit: true },
        "SB" => DeviceSpec { code: 0xA1, radix: Hex, is_bit: true },
        "DX" => DeviceSpec { code: 0xA2, radix: Octal, is_bit: true },
        "DY" => DeviceSpec { code: 0xA3, radix: Octal, is_bit: true },
        "S" => DeviceSpec { code: 0x98, radix: Decimal, is_bit: true },
        "SM" => DeviceSpec { code: 0x91, radix: Decimal, is_bit: true },
        "TS" => DeviceSpec { code: 0xC1, radix: Decimal, is_bit: true },
        "TC" => DeviceSpec { code: 0xC0, radix: Decimal, is_bit: true },
        "SS" => DeviceSpec { code: 0xC7, radix: Decimal, is_bit: true },
        "SC" => DeviceSpec { code: 0xC6, radix: Decimal, is_bit: true },
        "CS" => DeviceSpec { code: 0xC4, radix: Decimal, is_bit: true },
        "CC" => DeviceSpec { code: 0xC3, radix: Decimal, is_bit: true },
        // 字元件
        "D" => DeviceSpec { code: 0xA8, radix: Decimal, is_bit: false },
        "W" => DeviceSpec { code: 0xB4, radix: Hex, is_bit: false },
        "SW" => DeviceSpec { code: 0xB5, radix: Hex, is_bit: false },
        "SD" => DeviceSpec { code: 0xA9, radix: Decimal, is_bit: false },
        "R" => DeviceSpec { code: 0xAF, radix: Decimal, is_bit: false },
        "ZR" => DeviceSpec { code: 0xB0, radix: Hex, is_bit: false },
        "TN" => DeviceSpec { code: 0xC2, radix: Decimal, is_bit: false },
        "SN" => DeviceSpec { code: 0xC8, radix: Decimal, is_bit: false },
        "CN" => DeviceSpec { code: 0xC5, radix: Decimal, is_bit: false },
        "Z" => DeviceSpec { code: 0xCC, radix: Decimal, is_bit: false },
        _ => return None,
    };
    Some(spec)
}

/// 解析富文本地址为结构化 MC 地址。
///
/// 支持语法(§6.3):
/// - `D100`   字元件
/// - `M100`   位元件
/// - `X0`     八进制地址(内部转数值)
/// - `D100.3` 字元件的位(D100 的 bit3,仅在调用方需要位拆装时使用)
///
/// 错误返回 `CoreError::Modbus`(复用错误通道,code = MC_ADDRESS_INVALID)。
pub fn parse_mc_address(input: &str) -> Result<McAddress, CoreError> {
    let s = input.trim();
    if s.is_empty() {
        return Err(invalid_address("地址为空"));
    }

    // 拆分字母前缀与数字部分(允许 D100.3 位形式)
    let (prefix, rest) = split_prefix(s)?;
    let spec = device_spec(&prefix).ok_or_else(|| {
        invalid_address(&format!("未知软元件代号「{prefix}」(支持 X/Y/M/L/F/V/B/SB/DX/DY/S/SM/TS/TC/SS/SC/CS/CC/D/W/SW/SD/R/ZR/TN/SN/CN/Z)"))
    })?;

    // 数字部分:主编号(可带 .bit 后缀)
    let (num_str, _bit_suffix) = match rest.split_once('.') {
        Some((n, b)) => (n, Some(b)),
        None => (rest, None),
    };
    if num_str.is_empty() {
        return Err(invalid_address(&format!("「{s}」缺少软元件编号")));
    }

    // 按区域进制解析编号
    let head = parse_number_with_radix(num_str, spec.radix)
        .ok_or_else(|| invalid_address(&format!("「{num_str}」不是合法的{:?}地址", spec.radix)))?;

    // 头设备号 3 字节上限
    if head > 0xFF_FFFF {
        return Err(invalid_address(&format!("软元件编号 {head} 超出 3 字节上限(0xFFFFFF)")));
    }

    Ok(McAddress {
        device_code: spec.code,
        head_number: head,
        is_bit: spec.is_bit,
    })
}

/// 拆分字母前缀与剩余部分。前缀按最长匹配(如 "SB" 优先于 "S")。
fn split_prefix(s: &str) -> Result<(String, &str), CoreError> {
    // 所有 2 字母代号优先,再 1 字母
    const TWO_LETTER: [&str; 12] = ["SB", "DX", "DY", "SM", "TS", "TC", "SS", "SC", "CS", "CC", "TN", "SN", ];
    const _THREE: [&str; 2] = ["ZR", "SW"];
    let two_list: Vec<&str> = TWO_LETTER.iter().chain(_THREE.iter()).copied().collect();

    let upper: String = s.chars().take_while(|c| c.is_ascii_alphabetic()).collect::<String>().to_uppercase();
    if upper.len() >= 2 {
        let two = &upper[..2];
        if two_list.contains(&two) {
            return Ok((two.to_string(), &s[2..]));
        }
    }
    if upper.len() == 1 {
        return Ok((upper.clone(), &s[1..]));
    }
    if upper.is_empty() {
        return Err(invalid_address(&format!("「{s}」缺少软元件代号")));
    }
    Err(invalid_address(&format!("「{s}」的软元件代号「{upper}」无效")))
}

/// 按进制解析数字。八进制地址含 8/9 数字时报错(如 X8 非法)。
fn parse_number_with_radix(s: &str, radix: AddressRadix) -> Option<u32> {
    match radix {
        AddressRadix::Decimal => s.parse::<u32>().ok(),
        AddressRadix::Octal => u32::from_str_radix(s, 8).ok(),
        AddressRadix::Hex => {
            // 十六进制区(B/W/ZR 等):默认按十进制解释用户输入(对齐 HSL 习惯与
            // 文档 §2.1.4-(5) 报文示例——W10 编码为 0A),显式 0x 前缀才按十六进制。
            if let Some(t) = s.strip_prefix("0x").or_else(|| s.strip_prefix("0X")) {
                u32::from_str_radix(t, 16).ok()
            } else {
                s.parse::<u32>().ok()
            }
        }
    }
}

fn invalid_address(msg: &str) -> CoreError {
    CoreError::Modbus {
        code: "MC_ADDRESS_INVALID",
        message: msg.to_string(),
        details: None,
    }
}

/// 头设备号编码为 3 字节小端(§2.1.2 字段 10)。
pub fn encode_head_number(head: u32) -> [u8; 3] {
    [(head & 0xFF) as u8, ((head >> 8) & 0xFF) as u8, ((head >> 16) & 0xFF) as u8]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_d100_decimal_word() {
        let a = parse_mc_address("D100").unwrap();
        assert_eq!(a.device_code, 0xA8);
        assert_eq!(a.head_number, 100);
        assert!(!a.is_bit);
    }

    #[test]
    fn parses_m100_bit() {
        let a = parse_mc_address("M100").unwrap();
        assert_eq!(a.device_code, 0x90);
        assert_eq!(a.head_number, 100);
        assert!(a.is_bit);
    }

    #[test]
    fn parses_x0_octal() {
        let a = parse_mc_address("X0").unwrap();
        assert_eq!(a.device_code, 0x9C);
        assert_eq!(a.head_number, 0);
        assert!(a.is_bit);
    }

    #[test]
    fn x10_octal_equals_decimal_8() {
        // 八进制 X10 = 十进制 8
        let a = parse_mc_address("X10").unwrap();
        assert_eq!(a.head_number, 8);
    }

    #[test]
    fn x8_is_invalid_octal() {
        assert!(parse_mc_address("X8").is_err(), "八进制地址不含数字 8");
    }

    #[test]
    fn parses_b_hex() {
        // 十六进制区默认按十进制解释(对齐 HSL 与文档报文示例)
        let b = parse_mc_address("B31").unwrap();
        assert_eq!(b.device_code, 0xA0);
        assert_eq!(b.head_number, 31);
        // 显式 0x 前缀才按十六进制
        let bx = parse_mc_address("B0x1F").unwrap();
        assert_eq!(bx.head_number, 0x1F);
    }

    #[test]
    fn parses_two_letter_prefixes() {
        let sb = parse_mc_address("SB10").unwrap();
        assert_eq!(sb.device_code, 0xA1);
        assert_eq!(sb.head_number, 10); // 十六进制区默认按十进制解释

        let ts = parse_mc_address("TS100").unwrap();
        assert_eq!(ts.device_code, 0xC1);
        assert_eq!(ts.head_number, 100); // TS 十进制
        assert!(ts.is_bit);

        let tn = parse_mc_address("TN100").unwrap();
        assert_eq!(tn.device_code, 0xC2);
        assert!(!tn.is_bit);

        let zr = parse_mc_address("ZR100").unwrap();
        assert_eq!(zr.device_code, 0xB0);
        assert_eq!(zr.head_number, 100); // 十六进制区默认按十进制解释
        assert!(!zr.is_bit);
    }

    #[test]
    fn s_vs_sm_disambiguation() {
        // S 是步进继电器(0x98),SM 是特殊继电器(0x91)——最长匹配
        let s = parse_mc_address("S100").unwrap();
        assert_eq!(s.device_code, 0x98);
        let sm = parse_mc_address("SM100").unwrap();
        assert_eq!(sm.device_code, 0x91);
    }

    #[test]
    fn d100_with_bit_suffix() {
        let a = parse_mc_address("D100.3").unwrap();
        assert_eq!(a.device_code, 0xA8);
        assert_eq!(a.head_number, 100);
    }

    #[test]
    fn case_insensitive() {
        let a = parse_mc_address("d100").unwrap();
        assert_eq!(a.device_code, 0xA8);
    }

    #[test]
    fn rejects_unknown_device() {
        assert!(parse_mc_address("Q100").is_err());
        assert!(parse_mc_address("").is_err());
        assert!(parse_mc_address("D").is_err());
    }

    #[test]
    fn rejects_over_max_address() {
        // 0x1000000 > 0xFFFFFF
        assert!(parse_mc_address("D16777216").is_err());
        // 恰好上限应通过
        assert!(parse_mc_address("D16777215").is_ok());
    }

    #[test]
    fn head_number_encoding_little_endian_3bytes() {
        // D100 → 100 = 0x64 → [64 00 00]
        assert_eq!(encode_head_number(100), [0x64, 0x00, 0x00]);
        // D7000 → 7000 = 0x1B58 → [58 1B 00]
        assert_eq!(encode_head_number(7000), [0x58, 0x1B, 0x00]);
        // 0x123456 → [56 34 12]
        assert_eq!(encode_head_number(0x123456), [0x56, 0x34, 0x12]);
    }

    #[test]
    fn error_contains_code() {
        let err = parse_mc_address("X8").unwrap_err();
        match err {
            CoreError::Modbus { code, .. } => assert_eq!(code, "MC_ADDRESS_INVALID"),
            other => panic!("unexpected error type: {other:?}"),
        }
    }
}
