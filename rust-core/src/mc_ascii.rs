//! 三菱 MC 协议 **ASCII 帧**编码层(§2.3,TCP 5001)。
//!
//! ⚠️ 与 Binary 的关键差异(易错点,文档 §2.3.1/§2.3.2):
//! - 多字节字段按**逻辑值大端**十六进制呈现,不是字节级转写:
//!   Binary 长度 `0C 00`(小端)→ ASCII `"000C"`(大端)
//! - 软元件首地址固定 **6 字符高位对齐**:`64 00 00`(D100)→ `"000064"`
//! - 软元件代码 2 字符;十六进制编号区(X/Y/B/W 等)带 `*` 后缀(如 `"9C*"`)
//! - 位数据:请求/响应每点 "0"/"1" 单字符 **[实机验证]**
//! - 字数据:每字 4 字符大端(0x1234 → "1234")
//!
//! 长度字段语义与 Binary 相同:请求 = 定时器(2)+指令区;响应 = 结束代码(2)+数据区。

use crate::error::CoreError;
use crate::mc_address::{parse_mc_address, McAddress};
use crate::mc_frame::{AccessRoute, FrameType};
use crate::mc_pdu::{CMD_READ_BATCH, CMD_WRITE_BATCH, MAX_READ_BITS, MAX_READ_WORDS, SUBCMD_BIT, SUBCMD_WORD};

/// 软元件代码的 ASCII 表示(§6.1 `*` 规则)。
/// 星号集合 = X/Y/B/SB/SW/ZR/DX/DY(文档 §6.1 ASCII 代码列:X=`9C*` Y=`9D*` B=`A0*`...);
/// 注意 X/Y 内部地址进制是八进制,但 ASCII 帧内编号按十六进制表述,故仍带星号。
fn device_code_ascii(addr: &McAddress) -> String {
    let with_star = matches!(
        addr.device_code,
        0x9C | 0x9D | 0xA0 | 0xA1 | 0xB4 | 0xB5 | 0xB0 | 0xA2 | 0xA3 // X Y B SB W SW ZR DX DY
    );
    let base = format!("{:02X}", addr.device_code);
    if with_star { format!("{base}*") } else { base }
}

/// 构建 ASCII 请求帧(0401 成批读)。
///
/// 文档 §2.3.2 向量(读 D100 一字,默认路由):
/// `500000FFFF0300` `000C` `0010` `0401` `0001` `000064` `A8` `0001`
pub fn build_ascii_read_request(
    frame_type: FrameType,
    sequence: u16,
    route: &AccessRoute,
    watchdog: u16,
    address: &str,
    points: u16,
) -> Result<String, CoreError> {
    let addr = parse_mc_address(address)?;
    let (max, kind) = if addr.is_bit {
        (MAX_READ_BITS, "位")
    } else {
        (MAX_READ_WORDS, "字")
    };
    if points == 0 || points > max {
        return Err(CoreError::Modbus {
            code: "MC_POINTS_EXCEEDED",
            message: format!("读点数 {points} 超出上限({kind}最多 {max})"),
            details: None,
        });
    }
    let subcmd = if addr.is_bit { SUBCMD_BIT } else { SUBCMD_WORD };
    // 指令数据区(二进制等效):指令2+子命令2+地址3+代码1+点数2 = 10 字节
    let body_bin_len = 10usize;
    let head_len = 2 + 2 + 5 + 2; // 副帧头+序列号(4E)+路由+长度
    let data_len = 2 + body_bin_len; // 定时器 + 指令区

    let mut s = String::with_capacity(64);
    match frame_type {
        FrameType::Type3E => s.push_str("5000"),
        FrameType::Type4E => {
            s.push_str("5400");
            s.push_str(&format!("{:04X}", sequence));
        }
    }
    s.push_str(&format!("{:02X}", route.network_no));
    s.push_str(&format!("{:02X}", route.pc_no));
    // IO 字段 ASCII 按小端字节顺序呈现(0x03FF → "FF03",文档 §2.3.2)
    s.push_str(&format!("{:04X}", route.module_io.swap_bytes()));
    s.push_str(&format!("{:02X}", route.station_no));
    s.push_str(&format!("{:04X}", data_len));
    s.push_str(&format!("{:04X}", watchdog));
    s.push_str(&format!("{:04X}", CMD_READ_BATCH));
    s.push_str(&format!("{:04X}", subcmd));
    s.push_str(&format!("{:06X}", addr.head_number)); // 6 字符高位对齐
    s.push_str(&device_code_ascii(&addr));
    s.push_str(&format!("{:04X}", points));
    let _ = head_len;
    Ok(s)
}

/// 构建 ASCII 请求帧(1401 成批写)。
/// 位:数据每点 "0"/"1";字:每字 {:04X}。
pub fn build_ascii_write_request(
    frame_type: FrameType,
    sequence: u16,
    route: &AccessRoute,
    watchdog: u16,
    address: &str,
    values: &[u16],
) -> Result<String, CoreError> {
    let addr = parse_mc_address(address)?;
    let count = u16::try_from(values.len()).map_err(|_| CoreError::Modbus {
        code: "MC_TOO_MANY_VALUES",
        message: format!("写入数量 {} 超出 u16", values.len()),
        details: None,
    })?;
    if count == 0 {
        return Err(CoreError::Modbus {
            code: "MC_EMPTY_VALUES",
            message: "写入数据不能为空".into(),
            details: None,
        });
    }
    if addr.is_bit {
        for (i, v) in values.iter().enumerate() {
            if *v > 1 {
                return Err(CoreError::Modbus {
                    code: "MC_INVALID_BIT_VALUE",
                    message: format!("位元件第 {i} 项值 {v} 非法(只能 0/1)"),
                    details: None,
                });
            }
        }
    }
    let subcmd = if addr.is_bit { SUBCMD_BIT } else { SUBCMD_WORD };
    // 二进制等效:指令区 = 10 + 位count/字count*2
    let body_bin_len = 10 + if addr.is_bit { values.len() } else { values.len() * 2 };
    let data_len = 2 + body_bin_len;

    let mut s = String::with_capacity(80);
    match frame_type {
        FrameType::Type3E => s.push_str("5000"),
        FrameType::Type4E => {
            s.push_str("5400");
            s.push_str(&format!("{:04X}", sequence));
        }
    }
    s.push_str(&format!("{:02X}", route.network_no));
    s.push_str(&format!("{:02X}", route.pc_no));
    // IO 字段 ASCII 按小端字节顺序呈现(0x03FF → "FF03",文档 §2.3.2)
    s.push_str(&format!("{:04X}", route.module_io.swap_bytes()));
    s.push_str(&format!("{:02X}", route.station_no));
    s.push_str(&format!("{:04X}", data_len));
    s.push_str(&format!("{:04X}", watchdog));
    s.push_str(&format!("{:04X}", CMD_WRITE_BATCH));
    s.push_str(&format!("{:04X}", subcmd));
    s.push_str(&format!("{:06X}", addr.head_number));
    s.push_str(&device_code_ascii(&addr));
    s.push_str(&format!("{:04X}", count));
    if addr.is_bit {
        for v in values {
            s.push_str(if *v == 1 { "1" } else { "0" });
        }
    } else {
        for v in values {
            s.push_str(&format!("{:04X}", v));
        }
    }
    Ok(s)
}

/// 解析后的 ASCII 响应。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AsciiResponse {
    pub frame_type: FrameType,
    pub sequence: u16,
    pub end_code: u16,
    /// 剩余数据区原始 ASCII(调用方按位/字解析)
    pub data_ascii: String,
}

/// 解析 ASCII 响应帧。
pub fn parse_ascii_response(s: &str) -> Result<AsciiResponse, CoreError> {
    let s: String = s.trim().to_uppercase();
    // 头部:D000 + 路由(10) + 长度(4) = 18;4E 多序列号 4 = 22
    if s.len() < 22 {
        return Err(CoreError::Modbus {
            code: "MC_ASCII_RESPONSE_TOO_SHORT",
            message: format!("ASCII 响应 {} 字符,短于最小 22", s.len()),
            details: None,
        });
    }
    let (frame_type, seq, off) = if s.starts_with("D000") {
        (FrameType::Type3E, 0u16, 4)
    } else if s.starts_with("D400") {
        let seq = u16::from_str_radix(&s[4..8], 16).map_err(|_| bad_ascii(&s[4..8]))?;
        (FrameType::Type4E, seq, 8)
    } else {
        return Err(CoreError::Modbus {
            code: "MC_BAD_SUBHEADER",
            message: format!("ASCII 响应副帧头不是 D000/D400: {}", &s[..4.min(s.len())]),
            details: None,
        });
    };
    // [路由 10 字符][长度 4][结束代码 4][数据...]
    let len_off = off + 10;
    let data_len = usize::from_str_radix(&s[len_off..len_off + 4], 16).map_err(|_| bad_ascii(&s[len_off..len_off + 4]))?;
    let end_code = u16::from_str_radix(&s[len_off + 4..len_off + 8], 16)
        .map_err(|_| bad_ascii(&s[len_off + 4..len_off + 8]))?;
    let data_ascii = s[len_off + 8..].to_string();
    // 长度自校验:数据区字符数 = (data_len - 2) * 2(字)或 data_len - 2(位,单字符)
    let data_bin = data_len.saturating_sub(2);
    let expect_chars = data_bin * 2;
    if data_ascii.len() > expect_chars {
        return Err(CoreError::Modbus {
            code: "MC_LENGTH_MISMATCH",
            message: format!("ASCII 长度字段 {data_len} 与数据 {} 字符不符(最多 {expect_chars})", data_ascii.len()),
            details: None,
        });
    }
    Ok(AsciiResponse { frame_type, sequence: seq, end_code, data_ascii })
}

/// 从 ASCII 响应数据区解出字值(每 4 字符一字)。
pub fn ascii_words(resp: &AsciiResponse, count: usize) -> Result<Vec<u16>, CoreError> {
    let need = count * 4;
    if resp.data_ascii.len() < need {
        return Err(CoreError::Modbus {
            code: "MC_RESPONSE_TOO_SHORT",
            message: format!("ASCII 数据 {} 字符,需 {need}", resp.data_ascii.len()),
            details: None,
        });
    }
    (0..count)
        .map(|i| {
            u16::from_str_radix(&resp.data_ascii[i * 4..i * 4 + 4], 16)
                .map_err(|_| bad_ascii(&resp.data_ascii[i * 4..i * 4 + 4]))
        })
        .collect()
}

/// 从 ASCII 响应数据区解出位值("0"/"1" 单字符,§2.3.1 [实机验证])。
pub fn ascii_bits(resp: &AsciiResponse, count: usize) -> Result<Vec<u16>, CoreError> {
    if resp.data_ascii.len() < count {
        return Err(CoreError::Modbus {
            code: "MC_RESPONSE_TOO_SHORT",
            message: format!("ASCII 数据 {} 字符,需 {count}", resp.data_ascii.len()),
            details: None,
        });
    }
    Ok((0..count)
        .map(|i| {
            if resp.data_ascii.as_bytes()[i] == b'1' { 1u16 } else { 0u16 }
        })
        .collect())
}

fn bad_ascii(part: &str) -> CoreError {
    CoreError::Modbus {
        code: "MC_ASCII_BAD_CHAR",
        message: format!("「{part}」不是合法十六进制(ASCII 帧要求大写 0-9A-F)"),
        details: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 文档 §2.3.2 请求向量:读 D100 一字
    #[test]
    fn read_request_matches_doc_vector() {
        let s = build_ascii_read_request(
            FrameType::Type3E, 0, &AccessRoute::default(), 0x0010, "D100", 1,
        ).unwrap();
        assert_eq!(s, "500000FFFF0300" .to_string() + "000C" + "0010" + "0401" + "0001" + "000064" + "A8" + "0001");
        assert_eq!(s, "500000FFFF0300000C001004010001000064A80001");
    }

    /// 文档 §2.3.2 响应向量:D000...0004 0000 1234
    #[test]
    fn response_parse_matches_doc_vector() {
        let resp = parse_ascii_response("D00000FFFF0300000400001234").unwrap();
        assert_eq!(resp.end_code, 0x0000);
        assert_eq!(resp.data_ascii, "1234");
        let words = ascii_words(&resp, 1).unwrap();
        assert_eq!(words, vec![0x1234]);
    }

    /// X 软元件(十六进制编号区)ASCII 代码带星号 "9C*"
    #[test]
    fn x_device_code_has_star_suffix() {
        let s = build_ascii_read_request(
            FrameType::Type3E, 0, &AccessRoute::default(), 0x0010, "X0", 1,
        ).unwrap();
        assert!(s.contains("9C*"), "X 的 ASCII 代码应为 9C*: {s}");
        // 位读子命令 0000
        assert!(s.contains("04010000"), "位读子命令 0000: {s}");
    }

    /// M 软元件(十进制区)无星号 "90"
    #[test]
    fn m_device_code_no_star() {
        let s = build_ascii_read_request(
            FrameType::Type3E, 0, &AccessRoute::default(), 0x0010, "M100", 1,
        ).unwrap();
        assert!(s.contains("90"), "M 的 ASCII 代码应为 90: {s}");
        assert!(!s.contains("90*"), "十进制区不带星号");
    }

    /// 位写:数据每点单字符 "1"/"0"
    #[test]
    fn write_bits_single_char_data() {
        let s = build_ascii_write_request(
            FrameType::Type3E, 0, &AccessRoute::default(), 0x0010, "M100", &[1, 0, 1],
        ).unwrap();
        // 尾部 3 位 = "101"
        assert!(s.ends_with("0003101"), "位写数据应为单字符 101: {s}");
    }

    /// 字写:每字 4 字符大端
    #[test]
    fn write_words_four_chars() {
        let s = build_ascii_write_request(
            FrameType::Type3E, 0, &AccessRoute::default(), 0x0010, "D100", &[0x1234, 0xABCD],
        ).unwrap();
        assert!(s.ends_with("00021234ABCD"), "字写数据 1234ABCD: {s}");
    }

    /// 4E ASCII:5400 + 序列号
    #[test]
    fn frame_4e_ascii_with_sequence() {
        let s = build_ascii_read_request(
            FrameType::Type4E, 0x1234, &AccessRoute::default(), 0x0010, "D100", 1,
        ).unwrap();
        assert!(s.starts_with("54001234"), "4E ASCII 副帧头+序列号: {s}");
    }

    #[test]
    fn rejects_bad_subheader() {
        assert!(parse_ascii_response("500000FFFF0300000400001234").is_err());
    }

    #[test]
    fn rejects_too_short() {
        assert!(parse_ascii_response("D000").is_err());
    }

    /// 位读响应解析(单字符)
    #[test]
    fn parse_bit_response() {
        let resp = parse_ascii_response("D00000FFFF030000050000101").unwrap();
        // 数据 "101"
        assert_eq!(resp.data_ascii, "101");
        let bits = ascii_bits(&resp, 3).unwrap();
        assert_eq!(bits, vec![1, 0, 1]);
    }
}
