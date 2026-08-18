//! 三菱 **A-1E / SLMP-1E 帧**编码器(A 系列 E71/C24、FX3U-ENET、FX5U A 兼容 SLMP,§3.4)。
//!
//! 请求帧(§3.4.2 实测布局,来源 mcprotocol npm 库 [FX3U 实测] + CSDN A-1E 详解):
//! `命令(1B) | PC号(1B=FFH) | 监视定时器(2B LE) | 首软元件编号(4B LE) | 软元件代号(2B ASCII) | 软元件点数(2B LE) | [写数据]`
//!
//! ⚠️ 软元件代号是 **2 个 ASCII 字符**("D*"=44 2A、"X*"=58 2A、"TN"=54 4E…),
//! 与 3E 帧的 1 字节十六进制代码(0xA8/0x9C/0xC2)**完全不同**(§3.4.3 对照表)。
//!
//! 响应帧:`副帧头(81H) | 结束代码 | 数据`
//! - 副帧头 0x81 为 FX3U-ENET 实测(mcprotocol 断言首字节==0x81);
//!   部分 A 系列资料记 80H,**以目标机型抓包为准**
//! - 结束代码 00=正常;异常 50~60H;**5BH 时后随详细异常代码(10H~18H)+00H**
//! - 字读数据每字 2 字节小端;位读数据每 16 点 2 字节(位打包,bit0=首点)

use crate::error::CoreError;

/// 位成批读(§3.4.2 命令字节表)
pub const CMD1E_BIT_READ: u8 = 0x00;
/// 字成批读
pub const CMD1E_WORD_READ: u8 = 0x01;
/// 位成批写
pub const CMD1E_BIT_WRITE: u8 = 0x02;
/// 字成批写
pub const CMD1E_WORD_WRITE: u8 = 0x03;

/// 单请求字软元件上限(§3.4.1:≤255 字;FX3U-ENET 受 192B 发送 FIFO 限制实际≈96 字)
pub const MAX_1E_WORD_POINTS: u16 = 255;

/// 响应副帧头(实测 81H)
pub const RESPONSE_SUBHEAD: u8 = 0x81;

/// PC 号固定 FFH(访问对象为本站 CPU)
const PC_NO: u8 = 0xFF;

/// 软元件代号表:(名称, 2 字节 ASCII, 是否位元件)。
///
/// 前排为文档 §3.4.2 实测集合(X*/Y*/M*/D*/B*/W*/TN/CN);
/// L/F/V/TS/CS 为 §6.1 标准 A 系列代号(mcprotocol 家族实现同表)。
/// 注意 **TN/CN 是定时器/计数器当前值(字)**;触点请用 TS/CS。
const DEVICES: &[(&str, [u8; 2], bool)] = &[
    ("X", *b"X*", true),   // 58 2A
    ("Y", *b"Y*", true),   // 59 2A
    ("M", *b"M*", true),   // 4D 2A
    ("L", *b"L*", true),   // 4C 2A
    ("B", *b"B*", true),   // 42 2A
    ("F", *b"F*", true),   // 46 2A
    ("V", *b"V*", true),   // 56 2A
    ("TS", *b"TS", true),  // 54 53 定时器触点
    ("CS", *b"CS", true),  // 43 53 计数器触点
    ("D", *b"D*", false),  // 44 2A
    ("W", *b"W*", false),  // 57 2A
    ("TN", *b"TN", false), // 54 4E 定时器当前值
    ("CN", *b"CN", false), // 43 4E 计数器当前值
];

fn err(code: &'static str, message: String) -> CoreError {
    CoreError::Modbus {
        code,
        message,
        details: None,
    }
}

fn lookup_device(device: &str) -> Result<([u8; 2], bool), CoreError> {
    let device = device.to_ascii_uppercase();
    DEVICES
        .iter()
        .find(|(name, _, _)| *name == device)
        .map(|(_, code, is_bit)| (*code, *is_bit))
        .ok_or_else(|| {
            err(
                "MC_1E_DEVICE_UNKNOWN",
                format!("未知软元件「{device}」(1E 帧支持 X/Y/M/L/B/F/V/TS/CS/D/W/TN/CN)"),
            )
        })
}

/// 软元件名称 → 2 字节 ASCII 代号("D"→`D*`、"TN"→`TN`,§3.4.2)。
pub fn device_code_1e_ascii(device: &str) -> Result<[u8; 2], CoreError> {
    Ok(lookup_device(device)?.0)
}

/// 命令字节合法(00~03)且与软元件位/字类别一致。
fn validate_cmd_device(cmd: u8, device: &str) -> Result<([u8; 2], bool), CoreError> {
    let (code, is_bit) = lookup_device(device)?;
    let cmd_is_bit = match cmd {
        CMD1E_BIT_READ | CMD1E_BIT_WRITE => true,
        CMD1E_WORD_READ | CMD1E_WORD_WRITE => false,
        other => {
            return Err(err(
                "MC_1E_CMD_UNKNOWN",
                format!("命令字节 {other:#04X} 不是 1E 命令(仅 00 位读/01 字读/02 位写/03 字写)"),
            ))
        }
    };
    if cmd_is_bit != is_bit {
        return Err(err(
            "MC_1E_DEVICE_CLASS_MISMATCH",
            format!(
                "命令 {cmd:#04X} 是{}命令,但软元件 {device} 是{}元件{}",
                if cmd_is_bit { "位" } else { "字" },
                if is_bit { "位" } else { "字" },
                if is_bit { "(触点用 TS/CS、当前值用 TN/CN)" } else { "" },
            ),
        ));
    }
    Ok((code, is_bit))
}

/// 构建 1E 读请求帧(位读 00 / 字读 01)。
///
/// 文档 §3.4.2 示例向量(字读 D100 起 12 点,监视定时器 10):
/// `build_1e_read(CMD1E_WORD_READ, "D", 100, 12, 10)` →
/// `01 FF 0A 00 64 00 00 00 44 2A 0C 00`
pub fn build_1e_read(
    cmd: u8,
    device: &str,
    head: u32,
    points: u16,
    watchdog: u16,
) -> Result<Vec<u8>, CoreError> {
    let (code, is_bit) = validate_cmd_device(cmd, device)?;
    if points == 0 {
        return Err(err("MC_1E_POINTS_INVALID", "软元件点数须 ≥ 1".into()));
    }
    if !is_bit && points > MAX_1E_WORD_POINTS {
        return Err(err(
            "MC_1E_POINTS_EXCEEDED",
            format!("字点数 {points} 超出单请求上限 {MAX_1E_WORD_POINTS}(FX3U-ENET 实际≈96)"),
        ));
    }
    // 位读点数无文档化上限(点数字段 u16),由模块侧结束代码兜底
    let mut frame = Vec::with_capacity(12);
    frame.push(cmd);
    frame.push(PC_NO);
    frame.extend_from_slice(&watchdog.to_le_bytes());
    frame.extend_from_slice(&head.to_le_bytes());
    frame.extend_from_slice(&code);
    frame.extend_from_slice(&points.to_le_bytes());
    Ok(frame)
}

/// 构建 1E 写请求帧(位写 02 / 字写 03)。
///
/// - 位写:数据按 **每 16 点 2 字节** 位打包(bit i → 第 i/16 组的第 i%16 位),小端附加;
/// - 字写:数据每字 2 字节小端。
pub fn build_1e_write(
    cmd: u8,
    device: &str,
    head: u32,
    values_words: &[u16],
    values_bits: &[bool],
    watchdog: u16,
) -> Result<Vec<u8>, CoreError> {
    let (code, is_bit) = validate_cmd_device(cmd, device)?;
    let count: usize = if is_bit { values_bits.len() } else { values_words.len() };
    if count == 0 {
        return Err(err("MC_1E_EMPTY_VALUES", "写入数据不能为空".into()));
    }
    let count_u16 = u16::try_from(count).map_err(|_| {
        err(
            "MC_1E_TOO_MANY_VALUES",
            format!("写入数量 {count} 超出 u16 范围"),
        )
    })?;
    if !is_bit && count_u16 > MAX_1E_WORD_POINTS {
        return Err(err(
            "MC_1E_POINTS_EXCEEDED",
            format!("字写入数量 {count} 超出单请求上限 {MAX_1E_WORD_POINTS}"),
        ));
    }
    let mut frame = Vec::with_capacity(12 + count * 2);
    frame.push(cmd);
    frame.push(PC_NO);
    frame.extend_from_slice(&watchdog.to_le_bytes());
    frame.extend_from_slice(&head.to_le_bytes());
    frame.extend_from_slice(&code);
    frame.extend_from_slice(&count_u16.to_le_bytes());
    if is_bit {
        for group in values_bits.chunks(16) {
            let mut packed: u16 = 0;
            for (bit_pos, set) in group.iter().enumerate() {
                if *set {
                    packed |= 1u16 << bit_pos;
                }
            }
            frame.extend_from_slice(&packed.to_le_bytes());
        }
    } else {
        for value in values_words {
            frame.extend_from_slice(&value.to_le_bytes());
        }
    }
    Ok(frame)
}

/// 解析后的 1E 响应。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OneEResponse {
    /// 字读数据(每字 2 字节小端)
    Words(Vec<u16>),
    /// 位读数据(每 16 点 2 字节打包解出)
    Bits(Vec<bool>),
    /// 写响应(仅副帧头+结束代码)
    WriteAck,
    /// 异常结束(5BH 时带详细异常代码)
    Error { code: u8, detail: Option<u8> },
}

/// 解析 1E 响应帧:`副帧头(81H) | 结束代码 | 数据`。
///
/// - 结束代码 5BH 时按 `5B <详细代码> 00` 布局取详细代码(CSDN A-1E 详解);
/// - 位读按每 16 点 2 字节解包;字读每字小端;写命令仅回 `81 00`。
pub fn parse_1e_response(bytes: &[u8], cmd: u8, points: u16) -> Result<OneEResponse, CoreError> {
    if bytes.len() < 2 {
        return Err(err(
            "MC_1E_RESPONSE_TOO_SHORT",
            format!("响应 {} 字节,短于最小 2(副帧头+结束代码)", bytes.len()),
        ));
    }
    if bytes[0] != RESPONSE_SUBHEAD {
        return Err(err(
            "MC_1E_BAD_SUBHEAD",
            format!(
                "副帧头 {:#04X} 不是 81H(FX3U-ENET 实测 81H;部分 A 系列资料记 80H,以实机抓包为准)",
                bytes[0]
            ),
        ));
    }
    let end_code = bytes[1];
    if end_code != 0x00 {
        let detail = if end_code == 0x5B && bytes.len() >= 3 {
            Some(bytes[2])
        } else {
            None
        };
        return Ok(OneEResponse::Error { code: end_code, detail });
    }
    match cmd {
        CMD1E_BIT_READ => {
            let nbytes = (points as usize + 7) / 8;
            if bytes.len() < 2 + nbytes {
                return Err(err(
                    "MC_1E_RESPONSE_TOO_SHORT",
                    format!("位读响应 {} 字节,{} 点需 {} 字节", bytes.len(), points, 2 + nbytes),
                ));
            }
            // bit i → 第 i/8 字节的第 i%8 位(等价于第 i/16 组 LE u16 的第 i%16 位)
            let bits = (0..points as usize)
                .map(|i| (bytes[2 + i / 8] >> (i % 8)) & 1 == 1)
                .collect();
            Ok(OneEResponse::Bits(bits))
        }
        CMD1E_WORD_READ => {
            let need = points as usize * 2;
            if bytes.len() < 2 + need {
                return Err(err(
                    "MC_1E_RESPONSE_TOO_SHORT",
                    format!("字读响应 {} 字节,{} 字需 {} 字节", bytes.len(), points, 2 + need),
                ));
            }
            let words = (0..points as usize)
                .map(|i| u16::from_le_bytes([bytes[2 + i * 2], bytes[2 + i * 2 + 1]]))
                .collect();
            Ok(OneEResponse::Words(words))
        }
        CMD1E_BIT_WRITE | CMD1E_WORD_WRITE => Ok(OneEResponse::WriteAck),
        other => Err(err(
            "MC_1E_CMD_UNKNOWN",
            format!("命令字节 {other:#04X} 不是 1E 命令(仅 00~03)"),
        )),
    }
}

/// 1E 结束代码 → 人类可读消息(§3.4.2:00 正常 / 50~60H 异常 / 5BH+10~18H 详细)。
pub fn onee_error_message(code: u8, detail: Option<u8>) -> String {
    match (code, detail) {
        (0x00, _) => "正常结束".into(),
        (0x5B, Some(0x10)) => "软元件编号异常(编号超出 CPU 允许范围)".into(),
        (0x5B, Some(0x11)) => "软元件代码异常(代号不存在或该机型不支持)".into(),
        (0x5B, Some(0x12)) => "软元件点数异常(超出单请求上限)".into(),
        (0x5B, Some(other)) => format!("详细异常代码 {other:#04X}(10H~18H 区间,详查手册)"),
        (0x5B, None) => "异常 5BH(响应缺少详细异常代码字节)".into(),
        (other, _) => format!("异常结束代码 {other:#04X}(50H~60H 区间,详查手册)"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 文档 §3.4.2 示例向量(逐字节断言):字读 D100 起 12 点,监视定时器 10
    #[test]
    fn word_read_matches_doc_vector() {
        let frame = build_1e_read(CMD1E_WORD_READ, "D", 100, 12, 10).unwrap();
        assert_eq!(frame, [0x01, 0xFF, 0x0A, 0x00, 0x64, 0x00, 0x00, 0x00, 0x44, 0x2A, 0x0C, 0x00]);
    }

    /// 位读布局:命令 00 + M(4D 2A)+ 点数 2B LE
    #[test]
    fn bit_read_layout() {
        let frame = build_1e_read(CMD1E_BIT_READ, "M", 100, 8, 10).unwrap();
        assert_eq!(frame, [0x00, 0xFF, 0x0A, 0x00, 0x64, 0x00, 0x00, 0x00, 0x4D, 0x2A, 0x08, 0x00]);
    }

    /// 字写:数据每字 2 字节小端
    #[test]
    fn word_write_little_endian_data() {
        let frame =
            build_1e_write(CMD1E_WORD_WRITE, "D", 100, &[0x1234, 0xABCD], &[], 10).unwrap();
        assert_eq!(
            frame,
            [0x03, 0xFF, 0x0A, 0x00, 0x64, 0x00, 0x00, 0x00, 0x44, 0x2A, 0x02, 0x00, 0x34, 0x12, 0xCD, 0xAB]
        );
    }

    /// 位写打包:3 点 → 2 字节(bit0/bit2 → 05 00)
    #[test]
    fn bit_write_packs_16_points_per_2_bytes() {
        let frame = build_1e_write(CMD1E_BIT_WRITE, "M", 0, &[], &[true, false, true], 10).unwrap();
        assert_eq!(&frame[..12], &[0x02, 0xFF, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4D, 0x2A, 0x03, 0x00]);
        assert_eq!(&frame[12..], &[0x05, 0x00], "3 点占用 1 组 2 字节,bit0+bit2 → 05 00");
    }

    /// 位写打包:16 点 → 2 字节(bit0/bit8/bit15)
    #[test]
    fn bit_write_full_16_point_group() {
        let mut bits = vec![false; 16];
        bits[0] = true;
        bits[8] = true;
        bits[15] = true;
        let frame = build_1e_write(CMD1E_BIT_WRITE, "Y", 0, &[], &bits, 10).unwrap();
        assert_eq!(&frame[12..], &[0x01, 0x81], "bit0→低字节01,bit8+bit15→高字节81");
    }

    /// 软元件代号 ASCII 映射("D"→D*、"TN"→TN、大小写不敏感、未知拒绝)
    #[test]
    fn device_codes_ascii_mapping() {
        assert_eq!(device_code_1e_ascii("D").unwrap(), *b"D*");
        assert_eq!(device_code_1e_ascii("X").unwrap(), *b"X*");
        assert_eq!(device_code_1e_ascii("M").unwrap(), *b"M*");
        assert_eq!(device_code_1e_ascii("B").unwrap(), *b"B*");
        assert_eq!(device_code_1e_ascii("W").unwrap(), *b"W*");
        assert_eq!(device_code_1e_ascii("TN").unwrap(), *b"TN");
        assert_eq!(device_code_1e_ascii("CN").unwrap(), *b"CN");
        assert_eq!(device_code_1e_ascii("ts").unwrap(), *b"TS", "大小写不敏感");
        let e = device_code_1e_ascii("Q").unwrap_err();
        assert_eq!(e.body().code, "MC_1E_DEVICE_UNKNOWN");
    }

    /// 命令与软元件位/字类别交叉校验
    #[test]
    fn rejects_device_class_mismatch() {
        let e = build_1e_read(CMD1E_WORD_READ, "M", 0, 1, 10).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_DEVICE_CLASS_MISMATCH");
        let e = build_1e_read(CMD1E_BIT_READ, "D", 0, 1, 10).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_DEVICE_CLASS_MISMATCH");
        // TN 是字(当前值):字读合法,位读拒绝
        assert!(build_1e_read(CMD1E_WORD_READ, "TN", 0, 1, 10).is_ok());
        assert!(build_1e_read(CMD1E_BIT_READ, "TN", 0, 1, 10).is_err());
        // 非法命令字节
        let e = build_1e_read(0x04, "D", 0, 1, 10).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_CMD_UNKNOWN");
    }

    /// 点数校验:0 拒绝;字读 > 255 拒绝(§3.4.1)
    #[test]
    fn rejects_bad_points() {
        let e = build_1e_read(CMD1E_WORD_READ, "D", 0, 0, 10).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_POINTS_INVALID");
        let e = build_1e_read(CMD1E_WORD_READ, "D", 0, 256, 10).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_POINTS_EXCEEDED");
        assert!(build_1e_read(CMD1E_WORD_READ, "D", 0, 255, 10).is_ok());
        let e = build_1e_write(CMD1E_WORD_WRITE, "D", 0, &[], &[], 10).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_EMPTY_VALUES");
    }

    /// 字读响应:81 00 + 每字小端
    #[test]
    fn parse_word_response() {
        let resp = parse_1e_response(&[0x81, 0x00, 0x34, 0x12, 0xCD, 0xAB], CMD1E_WORD_READ, 2).unwrap();
        assert_eq!(resp, OneEResponse::Words(vec![0x1234, 0xABCD]));
    }

    /// 位读响应:每 16 点 2 字节解包(16 点 → 05 80 → bit0/bit2/bit15)
    #[test]
    fn parse_bit_response_16_points_in_2_bytes() {
        let resp = parse_1e_response(&[0x81, 0x00, 0x05, 0x80], CMD1E_BIT_READ, 16).unwrap();
        match resp {
            OneEResponse::Bits(bits) => {
                assert_eq!(bits.len(), 16);
                assert!(bits[0] && bits[2] && bits[15]);
                assert!(!bits[1] && !bits[8] && !bits[14]);
            }
            other => panic!("应为 Bits,得到 {other:?}"),
        }
    }

    /// 位读响应:不满 16 点也按整组 2 字节对齐
    #[test]
    fn parse_bit_response_partial_group() {
        let resp = parse_1e_response(&[0x81, 0x00, 0x05, 0x00], CMD1E_BIT_READ, 3).unwrap();
        assert_eq!(resp, OneEResponse::Bits(vec![true, false, true]));
    }

    /// 写响应:仅 81 00
    #[test]
    fn parse_write_ack() {
        let resp = parse_1e_response(&[0x81, 0x00], CMD1E_WORD_WRITE, 0).unwrap();
        assert_eq!(resp, OneEResponse::WriteAck);
    }

    /// 异常响应 5BH + 详细代码 11H(布局:5B <详细> 00)
    #[test]
    fn parse_error_5b_with_detail() {
        let resp = parse_1e_response(&[0x81, 0x5B, 0x11, 0x00], CMD1E_WORD_READ, 1).unwrap();
        assert_eq!(resp, OneEResponse::Error { code: 0x5B, detail: Some(0x11) });
        assert!(onee_error_message(0x5B, Some(0x11)).contains("软元件代码"));
        assert!(onee_error_message(0x5B, Some(0x10)).contains("软元件编号"));
        assert!(onee_error_message(0x5B, Some(0x12)).contains("点数"));
    }

    /// 异常响应 非 5B(50H)无详细代码
    #[test]
    fn parse_error_without_detail() {
        let resp = parse_1e_response(&[0x81, 0x50], CMD1E_WORD_READ, 1).unwrap();
        assert_eq!(resp, OneEResponse::Error { code: 0x50, detail: None });
        assert!(onee_error_message(0x50, None).contains("50H~60H"));
    }

    /// 副帧头/长度校验
    #[test]
    fn parse_rejects_bad_subheader_and_short() {
        let e = parse_1e_response(&[0x00, 0x00], CMD1E_WORD_READ, 1).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_BAD_SUBHEAD");
        let e = parse_1e_response(&[0x81], CMD1E_WORD_READ, 1).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_RESPONSE_TOO_SHORT");
        let e = parse_1e_response(&[0x81, 0x00, 0x34], CMD1E_WORD_READ, 1).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_RESPONSE_TOO_SHORT");
        let e = parse_1e_response(&[0x81, 0x00], 0x99, 1).unwrap_err();
        assert_eq!(e.body().code, "MC_1E_CMD_UNKNOWN");
    }

    /// 位写 → 位读响应的打包/解包一致性(bit i ↔ 字节 i/8 的 i%8 位)
    #[test]
    fn bit_pack_unpack_consistency() {
        let bits: Vec<bool> = (0..16).map(|i| i % 3 == 0).collect(); // 0,3,6,9,12,15 为真
        let frame = build_1e_write(CMD1E_BIT_WRITE, "X", 0, &[], &bits, 10).unwrap();
        // 用写数据区构造读响应(去掉 12 字节头部,换成 81 00 副帧头)
        let mut resp = vec![0x81, 0x00];
        resp.extend_from_slice(&frame[12..]);
        match parse_1e_response(&resp, CMD1E_BIT_READ, 16).unwrap() {
            OneEResponse::Bits(out) => assert_eq!(out, bits),
            other => panic!("应为 Bits,得到 {other:?}"),
        }
    }
}
