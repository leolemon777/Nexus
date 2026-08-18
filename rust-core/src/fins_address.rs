//! 欧姆龙 FINS 协议地址解析(CJ/CS/CP 系列)。
//!
//! 区代码(官方 FINS 规范 W342 稳定常识):
//! | 区        | 位(bit) | 字(word) |
//! | CIO       | 0x30    | 0xB0      |
//! | W  auxiliary| 0x31  | 0xB1      |
//! | H  holding | 0x32    | 0xB2      |
//! | A 保持继电器| 0x33   | 0xB3      |
//! | DM        | 0x02    | 0x82      |
//! | TIM/CNT 当前值 | — | 0x80      |
//! 位元件的地址编码 = 字偏移 × 16 + 位(线性 bit 地址,3 字节 BE)。
//!
//! 无把握段(TIM/CNT 完成触点 TS/CS、CF 标志、EM bank)返回 MANUAL,
//! 宁可不支持也不读错数据。

use crate::error::CoreError;

pub mod area {
    pub const CIO_BIT: u8 = 0x30;
    pub const W_BIT: u8 = 0x31;
    pub const H_BIT: u8 = 0x32;
    pub const A_BIT: u8 = 0x33;
    pub const DM_BIT: u8 = 0x02;
    pub const CIO_WORD: u8 = 0xB0;
    pub const W_WORD: u8 = 0xB1;
    pub const H_WORD: u8 = 0xB2;
    pub const A_WORD: u8 = 0xB3;
    pub const DM_WORD: u8 = 0x82;
    pub const TIMER_CNT_WORD: u8 = 0x80;
}

/// 字/位访问
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FinsKind {
    /// 位访问(CIO10.00 / D100.15 / W5.3)
    Bit,
    /// 字访问(D100 / CIO100 / W0 / H50)
    Word,
    /// 定时器/计数器当前值(字,区代码 0x80)
    TimerCntWord,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FinsAddress {
    pub area_code: u8,
    /// 位访问:字偏移×16+位;字访问:字偏移
    pub address: u32,
    pub kind: FinsKind,
}

fn invalid(msg: &str) -> CoreError {
    CoreError::Modbus { code: "FINS_ADDRESS_INVALID", message: msg.to_string(), details: None }
}

fn manual(what: &str) -> CoreError {
    CoreError::Modbus {
        code: "FINS_MAP_MANUAL",
        message: format!(
            "{what}:该区未内置(避免猜错读错数据)。TS/CS(定时计数完成触点)与 CF(标志)请查欧姆龙 FINS 手册 W342 的内存区代码表;EM 扩展文件存储器按 bank 前缀查询后加入"
        ),
        details: None,
    }
}

/// 解析 FINS 地址。
///
/// 支持:`D100`(字) `D100.15`(位) `CIO50.03` `W0` `H100.7` `A10` `T0`(当前值) `C5`(计数值)
/// 不支持(返回 MANUAL):`TS0`/`CS0`(触点)、`CF1.2`、`E0_100`(EM)。
pub fn parse_fins_address(input: &str) -> Result<FinsAddress, CoreError> {
    let s = input.trim().to_ascii_uppercase();
    if s.is_empty() {
        return Err(invalid("地址为空"));
    }

    // TIM/CNT 当前值:T0 / C5(无小数位)
    if let Some(rest) = s.strip_prefix('T') {
        if let Some(n) = parse_dec(rest) {
            return Ok(FinsAddress { area_code: area::TIMER_CNT_WORD, address: n, kind: FinsKind::TimerCntWord });
        }
    }
    if let Some(rest) = s.strip_prefix('C') {
        // C5 = 计数器当前值(不是 CS 触点)
        if let Some(n) = parse_dec(rest) {
            return Ok(FinsAddress { area_code: area::TIMER_CNT_WORD, address: n, kind: FinsKind::TimerCntWord });
        }
    }
    if s.starts_with("TS") || s.starts_with("CS") || s.starts_with("CF") || s.starts_with("E") {
        return Err(manual(&format!("软元件「{input}」")));
    }

    // (前缀, 剩余) 最长前缀匹配 CIO → 单字母
    let (prefix, rest) = if let Some(r) = s.strip_prefix("CIO") {
        ("CIO", r)
    } else if let Some(r) = s.strip_prefix('D') {
        ("D", r)
    } else if let Some(r) = s.strip_prefix('W') {
        ("W", r)
    } else if let Some(r) = s.strip_prefix('H') {
        ("H", r)
    } else if let Some(r) = s.strip_prefix('A') {
        ("A", r)
    } else {
        return Err(invalid(&format!(
            "「{input}」无法识别(支持 D/CIO/W/H/A/T/C,如 D100 / CIO10.00 / T0;位用 .00-.15)"
        )));
    };

    // 字偏移[.位]
    let (word_str, bit_str) = match rest.split_once('.') {
        Some((w, b)) => (w, Some(b)),
        None => (rest, None),
    };
    let word = parse_dec(word_str).ok_or_else(|| invalid(&format!("「{input}」字偏移不合法")))?;

    match (prefix, bit_str) {
        // 纯字访问
        ("D", None) => Ok(FinsAddress { area_code: area::DM_WORD, address: word, kind: FinsKind::Word }),
        ("CIO", None) => Ok(FinsAddress { area_code: area::CIO_WORD, address: word, kind: FinsKind::Word }),
        ("W", None) => Ok(FinsAddress { area_code: area::W_WORD, address: word, kind: FinsKind::Word }),
        ("H", None) => Ok(FinsAddress { area_code: area::H_WORD, address: word, kind: FinsKind::Word }),
        ("A", None) => Ok(FinsAddress { area_code: area::A_WORD, address: word, kind: FinsKind::Word }),
        // 位访问:字×16+位
        (_, Some(b)) if matches!(prefix, "D" | "CIO" | "W" | "H" | "A") => {
            let bit: u32 = b.parse().map_err(|_| invalid(&format!("「{input}」位偏移「{b}」不合法")))?;
            if bit > 15 {
                return Err(invalid(&format!("「{input}」位偏移应为 00-15")));
            }
            let code = match prefix {
                "D" => area::DM_BIT,
                "CIO" => area::CIO_BIT,
                "W" => area::W_BIT,
                "H" => area::H_BIT,
                "A" => area::A_BIT,
                _ => unreachable!(),
            };
            Ok(FinsAddress { area_code: code, address: word * 16 + bit, kind: FinsKind::Bit })
        }
        _ => unreachable!("前缀已限定"),
    }
}

impl FinsAddress {
    /// FINS 帧内 3 字节地址(大端)。
    pub fn encode(&self) -> [u8; 3] {
        [(self.address >> 16) as u8, (self.address >> 8) as u8, self.address as u8]
    }

    /// 0101/0102 的 word/bit 标志:0x00 位 / 0x01 字(TIM/CNT 0x80 固定按字)。
    pub fn word_bit_flag(&self) -> u8 {
        match self.kind {
            FinsKind::Bit => 0x00,
            _ => 0x01,
        }
    }
}

fn parse_dec(s: &str) -> Option<u32> {
    if s.is_empty() || !s.chars().all(|c| c.is_ascii_digit()) {
        return None;
    }
    s.parse().ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn word_addresses() {
        let d = parse_fins_address("D100").unwrap();
        assert_eq!((d.area_code, d.address, d.kind), (area::DM_WORD, 100, FinsKind::Word));
        let cio = parse_fins_address("CIO50").unwrap();
        assert_eq!((cio.area_code, cio.address), (area::CIO_WORD, 50));
        let w = parse_fins_address("W0").unwrap();
        assert_eq!(w.area_code, area::W_WORD);
        let h = parse_fins_address("H100").unwrap();
        assert_eq!(h.area_code, area::H_WORD);
        let a = parse_fins_address("A10").unwrap();
        assert_eq!(a.area_code, area::A_WORD);
    }

    #[test]
    fn bit_addresses_linear_encoding() {
        // D100.15 → DM_BIT, 100×16+15 = 1615
        let d = parse_fins_address("D100.15").unwrap();
        assert_eq!((d.area_code, d.address, d.kind), (area::DM_BIT, 1615, FinsKind::Bit));
        assert_eq!(d.encode(), [(1615 >> 16) as u8, (1615 >> 8) as u8, (1615 & 0xFF) as u8]);
        // CIO10.00 → 160
        let cio = parse_fins_address("CIO10.00").unwrap();
        assert_eq!((cio.area_code, cio.address), (area::CIO_BIT, 160));
        // W5.3 → 83
        assert_eq!(parse_fins_address("W5.3").unwrap().address, 83);
    }

    #[test]
    fn timer_counter_current_values() {
        let t = parse_fins_address("T0").unwrap();
        assert_eq!((t.area_code, t.kind), (area::TIMER_CNT_WORD, FinsKind::TimerCntWord));
        let c = parse_fins_address("C5").unwrap();
        assert_eq!(c.area_code, area::TIMER_CNT_WORD);
        assert_eq!(c.word_bit_flag(), 0x01);
    }

    #[test]
    fn unsupported_returns_manual_not_guess() {
        for addr in ["TS0", "CS1", "CF1.2", "E0_100", "EM50"] {
            let e = parse_fins_address(addr).unwrap_err();
            match e {
                CoreError::Modbus { code, .. } => assert_eq!(code, "FINS_MAP_MANUAL", "{addr}"),
                _ => panic!("{addr} 应返回 MANUAL"),
            }
        }
    }

    #[test]
    fn invalid_inputs() {
        for addr in ["", "Z10", "D", "D100.16", "CIO", "D-5"] {
            assert!(parse_fins_address(addr).is_err(), "应拒绝「{addr}」");
        }
    }

    #[test]
    fn case_insensitive() {
        assert_eq!(parse_fins_address(" d100 ").unwrap(), parse_fins_address("D100").unwrap());
        assert_eq!(parse_fins_address("cio10.00").unwrap().area_code, area::CIO_BIT);
    }
}
