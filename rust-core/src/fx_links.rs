//! 三菱 FX Computer Link 专用协议(计算机链接,RS-232/485)。
//!
//! 规范来源:《三菱全协议设计文档.md》§3.2(手册 JY992D82001 第 5 章)。
//!
//! 命令帧(PC → PLC):
//! `[ENQ 05H][站号 2 ASCII hex][PC号 "FF"][命令码 2][延时 1 "0"~"F"][数据...][ETX 03H][和校验 2][CR LF]`
//! 读响应(PLC → PC):`[STX 02H][站号 2][PC号 "FF"][数据][ETX 03H][和校验 2][CR LF]`
//! 写响应(PLC → PC):`[ACK 06H]` 或 `[NAK 15H][站号 2][错误码]`
//!
//! 和校验:站号首字符 ~ ETX 的全部 ASCII 字节累加取低 8 位 → 2 个 ASCII hex 字符
//! (文档 §3.2.4 示例:0x30+0x30+0x31+0x30+0x31+0x30+0x31+0x03 = 0x156 → 0x56 → "56")。
//!
//! 点数字段首版按文档约定:位命令 2 字符、字命令 4 字符;首地址为十六进制(如 D200 → "D00C8")。

use crate::error::CoreError;

/// 控制字符(§3.2.2)
pub const ENQ: u8 = 0x05;
pub const ACK: u8 = 0x06;
pub const NAK: u8 = 0x15;
pub const STX: u8 = 0x02;
pub const ETX: u8 = 0x03;
pub const CR: u8 = 0x0D;
pub const LF: u8 = 0x0A;

/// 命令码表(§3.2.3)
pub const CMD_BIT_READ: &str = "BR"; // 位成批读(点单位,1 点 = 1 字符 "0"/"1")
pub const CMD_WORD_READ: &str = "WR"; // 字成批读(16 点单位,每字 4 字符 ASCII hex)
pub const CMD_BIT_WRITE: &str = "BW"; // 位成批写
pub const CMD_WORD_WRITE: &str = "WW"; // 字成批写
pub const CMD_BIT_RANDOM_WRITE: &str = "BT"; // 位多点写(随机,逐点 SET/RESET)
pub const CMD_WORD_RANDOM_WRITE: &str = "WT"; // 字多点写(随机)
pub const CMD_REMOTE_RUN: &str = "RR"; // 远程 RUN
pub const CMD_REMOTE_STOP: &str = "RS"; // 远程 STOP
pub const CMD_READ_TYPE: &str = "PC"; // 读 PLC 型号
pub const CMD_LOOP_TEST: &str = "TT"; // 回送测试(通信诊断)

/// 单帧点数上限(§3.2.4):位 ≤ 255 点 / 字 ≤ 64 字
pub const MAX_BIT_POINTS: usize = 255;
pub const MAX_WORD_POINTS: usize = 64;

/// 站号上限(D8121,0~15,§3.2.1)
pub const MAX_STATION: u8 = 0x0F;

fn err(code: &'static str, message: String) -> CoreError {
    CoreError::Modbus {
        code,
        message,
        details: None,
    }
}

fn hex_char(value: u8) -> u8 {
    match value {
        0..=9 => b'0' + value,
        _ => b'A' + value - 10,
    }
}

fn hex_digit_value(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'A'..=b'F' => Some(byte - b'A' + 10),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        _ => None,
    }
}

/// 两个 ASCII hex 字符 → u8
fn parse_two_hex(high: u8, low: u8) -> Option<u8> {
    Some(hex_digit_value(high)? << 4 | hex_digit_value(low)?)
}

/// 和校验:给定字节累加取低 8 位。
///
/// 调用方应传入「站号首字符 ~ ETX」范围(请求)或「数据首字符 ~ ETX」范围(响应)。
pub fn fx_links_checksum(bytes: &[u8]) -> u8 {
    bytes.iter().fold(0u8, |acc, byte| acc.wrapping_add(*byte))
}

/// 命令码是否属于 §3.2.3 命令表(大小写不敏感)。
pub fn fx_links_is_known_command(cmd: &str) -> bool {
    matches!(
        cmd.to_ascii_uppercase().as_str(),
        "BR" | "WR" | "BW" | "WW" | "BT" | "WT" | "RR" | "RS" | "PC" | "TT"
    )
}

/// 构建完整请求帧。
///
/// `data` 为延时之后、ETX 之前的全部 ASCII 载荷
/// (软元件代码+首地址 5 字符、元件数、写入数据等,按命令自行拼接)。
///
/// 布局(§3.2.4):`ENQ 站号(2) "FF" 命令(2) 延时(1) data ETX SUM(2) CR LF`,
/// SUM = 站号首字符 ~ ETX 累加。
pub fn build_fx_links_request(
    station: u8,
    cmd: &str,
    delay: u8,
    data: &str,
) -> Result<Vec<u8>, CoreError> {
    if station > MAX_STATION {
        return Err(err(
            "FX_LINKS_STATION_INVALID",
            format!("站号 {station} 超出范围 0~15(D8121)"),
        ));
    }
    let cmd = cmd.to_ascii_uppercase();
    if !fx_links_is_known_command(&cmd) {
        return Err(err(
            "FX_LINKS_CMD_UNKNOWN",
            format!("未知命令码「{cmd}」(支持 BR/WR/BW/WW/BT/WT/RR/RS/PC/TT)"),
        ));
    }
    if delay > 0x0F {
        return Err(err(
            "FX_LINKS_DELAY_INVALID",
            format!("延时 {delay:#04X} 超出范围 0~F"),
        ));
    }
    if data.bytes().any(|byte| !(0x20..=0x7E).contains(&byte)) {
        return Err(err(
            "FX_LINKS_DATA_INVALID",
            "数据字段必须是可打印 ASCII 字符".into(),
        ));
    }

    let mut frame = Vec::with_capacity(10 + data.len() + 5);
    frame.push(ENQ);
    frame.push(hex_char(station >> 4));
    frame.push(hex_char(station & 0x0F));
    frame.extend_from_slice(b"FF");
    frame.extend_from_slice(cmd.as_bytes());
    frame.push(hex_char(delay));
    frame.extend_from_slice(data.as_bytes());
    frame.push(ETX);
    // 和校验:站号首字符(下标 1)~ ETX
    let sum = fx_links_checksum(&frame[1..]);
    frame.push(hex_char(sum >> 4));
    frame.push(hex_char(sum & 0x0F));
    frame.extend_from_slice(&[CR, LF]);
    Ok(frame)
}

/// 软元件字段:1 位字母 + 4 位十六进制地址(如 D200 → "D00C8")。
pub fn fx_links_device_field(device: &str, head: u16) -> Result<String, CoreError> {
    let device = device.to_ascii_uppercase();
    match device.as_str() {
        "X" | "Y" | "M" | "S" | "T" | "C" | "D" => Ok(format!("{device}{head:04X}")),
        other => Err(err(
            "FX_LINKS_DEVICE_UNKNOWN",
            format!("未知软元件「{other}」(支持 X/Y/M/S/T/C/D)"),
        )),
    }
}

/// 是否位元件(X/Y/M/S/T/C);D 为字元件。
fn is_bit_device(device: &str) -> Result<bool, CoreError> {
    let device = device.to_ascii_uppercase();
    match device.as_str() {
        "X" | "Y" | "M" | "S" | "T" | "C" => Ok(true),
        "D" => Ok(false),
        other => Err(err(
            "FX_LINKS_DEVICE_UNKNOWN",
            format!("未知软元件「{other}」(支持 X/Y/M/S/T/C/D)"),
        )),
    }
}

/// BR/WR 成批读:X/Y/M/S/T/C 走 BR(点数 2 字符),D 走 WR(点数 4 字符)。
///
/// 读 T/C 当前值请用 [build_fx_links_request] 直接拼 WR。
pub fn build_fx_links_read(
    station: u8,
    device: &str,
    head: u16,
    points: u16,
    delay: u8,
) -> Result<Vec<u8>, CoreError> {
    if points == 0 {
        return Err(err("FX_LINKS_POINTS_INVALID", "点数须 ≥ 1".into()));
    }
    let field = fx_links_device_field(device, head)?;
    let (cmd, data) = if is_bit_device(device)? {
        if points as usize > MAX_BIT_POINTS {
            return Err(err(
                "FX_LINKS_POINTS_INVALID",
                format!("位点数 {points} 超过单帧上限 {MAX_BIT_POINTS}"),
            ));
        }
        (CMD_BIT_READ, format!("{field}{points:02X}"))
    } else {
        if points as usize > MAX_WORD_POINTS {
            return Err(err(
                "FX_LINKS_POINTS_INVALID",
                format!("字点数 {points} 超过单帧上限 {MAX_WORD_POINTS}"),
            ));
        }
        (CMD_WORD_READ, format!("{field}{points:04X}"))
    };
    build_fx_links_request(station, cmd, delay, &data)
}

/// BW 位成批写:连续位逐点写 "0"/"1"。
pub fn build_fx_links_write_bits(
    station: u8,
    device: &str,
    head: u16,
    values: &[bool],
    delay: u8,
) -> Result<Vec<u8>, CoreError> {
    if !is_bit_device(device)? {
        return Err(err(
            "FX_LINKS_DEVICE_NOT_BIT",
            "BW 的目标须是位元件(X/Y/M/S/T/C)".into(),
        ));
    }
    if values.is_empty() || values.len() > MAX_BIT_POINTS {
        return Err(err(
            "FX_LINKS_POINTS_INVALID",
            format!("位点数须在 1~{MAX_BIT_POINTS}"),
        ));
    }
    let field = fx_links_device_field(device, head)?;
    let mut data = format!("{field}{:02X}", values.len());
    for value in values {
        data.push(if *value { '1' } else { '0' });
    }
    build_fx_links_request(station, CMD_BIT_WRITE, delay, &data)
}

/// WW 字成批写:每字 4 字符 ASCII hex(目标 D)。
pub fn build_fx_links_write_words(
    station: u8,
    device: &str,
    head: u16,
    values: &[u16],
    delay: u8,
) -> Result<Vec<u8>, CoreError> {
    if is_bit_device(device)? {
        return Err(err(
            "FX_LINKS_DEVICE_NOT_WORD",
            "WW 的目标须是字元件(D)".into(),
        ));
    }
    if values.is_empty() || values.len() > MAX_WORD_POINTS {
        return Err(err(
            "FX_LINKS_POINTS_INVALID",
            format!("字点数须在 1~{MAX_WORD_POINTS}"),
        ));
    }
    let field = fx_links_device_field(device, head)?;
    let mut data = format!("{field}{:04X}", values.len());
    for value in values {
        data.push_str(&format!("{value:04X}"));
    }
    build_fx_links_request(station, CMD_WORD_WRITE, delay, &data)
}

/// BT 位多点写的单个目标(随机 SET/RESET)。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FxLinksBitPoint {
    pub device: String,
    pub head: u16,
    pub on: bool,
}

/// BT 位多点写:点数(2)在前,随后逐点 [软元件字段(5) + "0"/"1"]。
pub fn build_fx_links_write_bits_random(
    station: u8,
    points: &[FxLinksBitPoint],
    delay: u8,
) -> Result<Vec<u8>, CoreError> {
    if points.is_empty() || points.len() > MAX_BIT_POINTS {
        return Err(err(
            "FX_LINKS_POINTS_INVALID",
            format!("位点数须在 1~{MAX_BIT_POINTS}"),
        ));
    }
    let mut data = format!("{:02X}", points.len());
    for point in points {
        data.push_str(&fx_links_device_field(&point.device, point.head)?);
        data.push(if point.on { '1' } else { '0' });
    }
    build_fx_links_request(station, CMD_BIT_RANDOM_WRITE, delay, &data)
}

/// TT 回送测试:PLC 原样返回测试数据(布局按 JY992D82001:字符数(2)+测试字符)。
pub fn build_fx_links_test(
    station: u8,
    test_data: &str,
    delay: u8,
) -> Result<Vec<u8>, CoreError> {
    let count = test_data.len();
    if count == 0 || count > 0xFF {
        return Err(err(
            "FX_LINKS_TEST_DATA_INVALID",
            "测试数据须为 1~255 个 ASCII 字符".into(),
        ));
    }
    if test_data.bytes().any(|byte| !(0x20..=0x7E).contains(&byte)) {
        return Err(err(
            "FX_LINKS_DATA_INVALID",
            "测试数据必须是可打印 ASCII 字符".into(),
        ));
    }
    let data = format!("{count:02X}{test_data}");
    build_fx_links_request(station, CMD_LOOP_TEST, delay, &data)
}

/// RR 远程 RUN(无附加数据)
pub fn build_fx_links_run(station: u8, delay: u8) -> Result<Vec<u8>, CoreError> {
    build_fx_links_request(station, CMD_REMOTE_RUN, delay, "")
}

/// RS 远程 STOP(无附加数据)
pub fn build_fx_links_stop(station: u8, delay: u8) -> Result<Vec<u8>, CoreError> {
    build_fx_links_request(station, CMD_REMOTE_STOP, delay, "")
}

/// PC 读 PLC 型号(响应为型号代码,经 STX 帧返回)
pub fn build_fx_links_read_type(station: u8, delay: u8) -> Result<Vec<u8>, CoreError> {
    build_fx_links_request(station, CMD_READ_TYPE, delay, "")
}

/// 解析后的 FX Computer Link 响应。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FxLinksResponse {
    /// STX 读响应:`STX [站号 PC号] 数据 ETX SUM`
    /// station/pc 为 None 表示响应未带站号/PC号前缀(§3.2.4 简化布局兼容)。
    ReadData {
        station: Option<u8>,
        pc: Option<u8>,
        /// ASCII 字符数据(位:逐点 "0"/"1";字:每字 4 字符 hex)
        data: Vec<u8>,
    },
    /// ACK:写命令成功
    Ack,
    /// NAK:站号 + 错误码
    Nak { station: Option<u8>, error_code: u8 },
}

/// 解析 PLC 响应帧:区分 STX 数据 / ACK / NAK 错误码,并校验和校验。
pub fn parse_fx_links_response(bytes: &[u8]) -> Result<FxLinksResponse, CoreError> {
    let Some(&first) = bytes.first() else {
        return Err(err(
            "FX_LINKS_RESPONSE_EMPTY",
            "响应帧为空(PLC 无应答?)".into(),
        ));
    };
    match first {
        ACK => Ok(FxLinksResponse::Ack),
        NAK => parse_nak(bytes),
        STX => parse_stx(bytes),
        other => Err(err(
            "FX_LINKS_RESPONSE_PREFIX",
            format!("响应首字节 {other:#04X} 不是 STX/ACK/NAK"),
        )),
    }
}

/// STX 帧:`STX [站号 PC号] 数据 ETX SUM [CR LF]`
fn parse_stx(bytes: &[u8]) -> Result<FxLinksResponse, CoreError> {
    let etx = match bytes[1..].iter().position(|&byte| byte == ETX) {
        Some(index) => index + 1,
        None => {
            return Err(err(
                "FX_LINKS_ETX_MISSING",
                "STX 响应缺少 ETX(03H) 终止符".into(),
            ))
        }
    };
    if etx < 2 {
        return Err(err(
            "FX_LINKS_DATA_EMPTY",
            "STX 与 ETX 之间无任何内容".into(),
        ));
    }
    if bytes.len() < etx + 3 {
        return Err(err(
            "FX_LINKS_SUM_MISSING",
            "ETX 后缺少 2 字符和校验".into(),
        ));
    }
    let received = parse_two_hex(bytes[etx + 1], bytes[etx + 2]).ok_or_else(|| {
        err(
            "FX_LINKS_SUM_MALFORMED",
            "和校验字符不是 ASCII hex".into(),
        )
    })?;
    // 和校验覆盖:站号(或数据)首字符 ~ ETX,即 STX 之后全部载荷
    let expected = fx_links_checksum(&bytes[1..=etx]);
    if received != expected {
        return Err(err(
            "FX_LINKS_CHECKSUM_MISMATCH",
            format!("和校验不符:期望 {expected:02X},收到 {received:02X}"),
        ));
    }
    // 站号+PC号前缀:STX 后紧跟 [站号(2 hex)] "FF" 时拆出
    if etx >= 5 && &bytes[3..5] == b"FF" {
        let station = parse_two_hex(bytes[1], bytes[2]).ok_or_else(|| {
            err("FX_LINKS_STATION_MALFORMED", "站号字符不是 ASCII hex".into())
        })?;
        Ok(FxLinksResponse::ReadData {
            station: Some(station),
            pc: Some(0xFF),
            data: bytes[5..etx].to_vec(),
        })
    } else {
        Ok(FxLinksResponse::ReadData {
            station: None,
            pc: None,
            data: bytes[1..etx].to_vec(),
        })
    }
}

/// NAK 帧:文档 §3.2.4 布局为 `NAK 站号(2) 错误码(2)`;兼容带 PC号 "FF"
/// 与 1 位错误码的变体(错误码取值不会是 FF,可安全区分)。
fn parse_nak(bytes: &[u8]) -> Result<FxLinksResponse, CoreError> {
    let hexes: Vec<u8> = bytes[1..]
        .iter()
        .copied()
        .take_while(|byte| hex_digit_value(*byte).is_some())
        .map(|byte| byte.to_ascii_uppercase())
        .collect();
    if hexes.is_empty() {
        return Err(err(
            "FX_LINKS_NAK_MALFORMED",
            "NAK 帧缺少站号/错误码".into(),
        ));
    }
    let (station, rest): (Option<u8>, &[u8]) = if hexes.len() >= 2 {
        let station = parse_two_hex(hexes[0], hexes[1]).ok_or_else(|| {
            err("FX_LINKS_STATION_MALFORMED", "站号字符不是 ASCII hex".into())
        })?;
        (Some(station), &hexes[2..])
    } else {
        (None, &hexes[..])
    };
    let rest = if rest.len() >= 3 && &rest[..2] == b"FF" {
        &rest[2..]
    } else {
        rest
    };
    let error_code = match rest {
        [single] => hex_digit_value(*single).unwrap_or(0),
        [high, low] => parse_two_hex(*high, *low).ok_or_else(|| {
            err("FX_LINKS_NAK_MALFORMED", "错误码字符不是 ASCII hex".into())
        })?,
        _ => {
            return Err(err(
                "FX_LINKS_NAK_MALFORMED",
                "NAK 帧站号/错误码字段长度不符".into(),
            ))
        }
    };
    Ok(FxLinksResponse::Nak { station, error_code })
}

/// NAK 错误码 → 人类可读(码值来源 JY992D82001 第 5 章)。
pub fn fx_links_error_message(error_code: u8) -> String {
    match error_code {
        0x02 => "奇偶校验错误".into(),
        0x03 => "成帧错误(停止位/数据位不符)".into(),
        0x06 => "字符错误(校验和或命令不匹配)".into(),
        0x07 => "字符数超过允许范围".into(),
        other => format!("NAK 错误码 {other:#04X}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 文档 §3.2.4 和校验示例:8 字节累加 = 0x156 → 低 8 位 0x56
    #[test]
    fn checksum_matches_doc_example() {
        let sum = fx_links_checksum(&[0x30, 0x30, 0x31, 0x30, 0x31, 0x30, 0x31, 0x03]);
        assert_eq!(sum, 0x56);
        // 同一算式的字符形式:"56"
        assert_eq!(&[hex_char(sum >> 4), hex_char(sum & 0x0F)], b"56");
    }

    /// 文档 §3.2.4 示例【构造】:站号 00,WR 读 D200(0x00C8)起 10 字。
    /// 手算和校验:00 FF WR 0 D00C8 000A + ETX → 0x3B8 → "B8"。
    #[test]
    fn wr_frame_matches_doc_example() {
        let frame = build_fx_links_read(0, "D", 200, 10, 0).unwrap();
        assert_eq!(
            frame,
            vec![
                0x05, // ENQ
                0x30, 0x30, // "00" 站号
                0x46, 0x46, // "FF" PC号
                0x57, 0x52, // "WR"
                0x30, // "0" 延时
                0x44, 0x30, 0x30, 0x43, 0x38, // "D00C8"
                0x30, 0x30, 0x30, 0x41, // "000A" 字点数(4 字符)
                0x03, // ETX
                0x42, 0x38, // "B8" 和校验
                0x0D, 0x0A, // CR LF
            ]
        );
    }

    /// BR 位读:M100(0x0064)起 8 点 → 点数 2 字符。
    /// 手算:00 FF BR 0 M0064 08 + ETX → 0x332 → "32"。
    #[test]
    fn br_frame_bit_read() {
        let frame = build_fx_links_read(0, "M", 100, 8, 0).unwrap();
        assert_eq!(
            frame,
            vec![
                0x05, 0x30, 0x30, 0x46, 0x46, 0x42, 0x52, 0x30, 0x4D, 0x30, 0x30, 0x36, 0x34,
                0x30, 0x38, 0x03, 0x33, 0x32, 0x0D, 0x0A,
            ]
        );
        // 布局再核对:站号/PC号/命令/延时/软元件字段/点数
        assert_eq!(&frame[5..7], b"BR");
        assert_eq!(&frame[7..8], b"0");
        assert_eq!(&frame[8..13], b"M0064");
        assert_eq!(&frame[13..15], b"08");
    }

    /// BW 位写:M0 写 1,0,1 → 软元件字段+点数(2)+逐点 "101"
    #[test]
    fn bw_frame_bit_write() {
        let frame = build_fx_links_write_bits(0, "M", 0, &[true, false, true], 0).unwrap();
        let expected_payload = b"00FFBW0M000003101\x03";
        let mut expected = vec![0x05];
        expected.extend_from_slice(expected_payload);
        let sum = fx_links_checksum(expected_payload);
        expected.push(hex_char(sum >> 4));
        expected.push(hex_char(sum & 0x0F));
        expected.extend_from_slice(&[0x0D, 0x0A]);
        assert_eq!(frame, expected);
        // 数据区布局
        assert_eq!(&frame[8..13], b"M0000");
        assert_eq!(&frame[13..15], b"03");
        assert_eq!(&frame[15..18], b"101");
    }

    /// WW 字写:D0 = 0x1234 → 软元件字段+点数(4)+4 字符 hex
    #[test]
    fn ww_frame_word_write() {
        let frame = build_fx_links_write_words(0, "D", 0, &[0x1234], 0).unwrap();
        assert_eq!(&frame[5..7], b"WW");
        assert_eq!(&frame[8..13], b"D0000");
        assert_eq!(&frame[13..17], b"0001");
        assert_eq!(&frame[17..21], b"1234");
        assert_eq!(frame[21], 0x03);
        // 和校验自洽:站号首字符 ~ ETX
        let sum = fx_links_checksum(&frame[1..22]);
        assert_eq!(&frame[22..24], &[hex_char(sum >> 4), hex_char(sum & 0x0F)]);
    }

    /// BT 位多点写:M0=ON、Y0=OFF → 点数(2)在前,逐点 软元件+数值
    #[test]
    fn bt_frame_random_bit_write() {
        let frame = build_fx_links_write_bits_random(
            0,
            &[
                FxLinksBitPoint {
                    device: "M".into(),
                    head: 0,
                    on: true,
                },
                FxLinksBitPoint {
                    device: "Y".into(),
                    head: 0,
                    on: false,
                },
            ],
            0,
        )
        .unwrap();
        assert_eq!(&frame[5..7], b"BT");
        assert_eq!(&frame[8..10], b"02");
        assert_eq!(&frame[10..15], b"M0000");
        assert_eq!(&frame[15..16], b"1");
        assert_eq!(&frame[16..21], b"Y0000");
        assert_eq!(&frame[21..22], b"0");
        assert_eq!(frame[22], 0x03);
    }

    /// TT 回送测试:字符数(2)+测试数据
    #[test]
    fn tt_frame_loop_test() {
        let frame = build_fx_links_test(0, "AB", 0).unwrap();
        assert_eq!(&frame[5..7], b"TT");
        assert_eq!(&frame[8..10], b"02");
        assert_eq!(&frame[10..12], b"AB");
        assert_eq!(frame[12], 0x03);
    }

    /// RR 远程 RUN:无附加数据,帧以 延时+ETX+SUM+CRLF 结束
    #[test]
    fn rr_frame_remote_run() {
        let frame = build_fx_links_run(0, 0).unwrap();
        assert_eq!(&frame[5..7], b"RR");
        assert_eq!(frame[7], b'0');
        assert_eq!(frame[8], 0x03);
        assert_eq!(&frame[frame.len() - 2..], &[0x0D, 0x0A]);
        // RS/PC 与 RR 结构一致
        assert_eq!(build_fx_links_stop(0, 0).unwrap()[5..7], *b"RS");
        assert_eq!(build_fx_links_read_type(0, 0).unwrap()[5..7], *b"PC");
    }

    /// STX 读响应解析:STX "00" "FF" "1234" ETX "B9"(手算 0x1B9)CR LF
    #[test]
    fn parse_stx_response_with_station() {
        let resp = [
            0x02, 0x30, 0x30, 0x46, 0x46, 0x31, 0x32, 0x33, 0x34, 0x03, 0x42, 0x39, 0x0D, 0x0A,
        ];
        match parse_fx_links_response(&resp).unwrap() {
            FxLinksResponse::ReadData {
                station,
                pc,
                data,
            } => {
                assert_eq!(station, Some(0));
                assert_eq!(pc, Some(0xFF));
                assert_eq!(data, b"1234");
            }
            other => panic!("应为 ReadData,实际 {other:?}"),
        }
    }

    /// 无站号前缀的 STX 响应(§3.2.4 简化布局):STX "0123" ETX "C9"
    #[test]
    fn parse_stx_response_without_station() {
        let resp = [0x02, 0x30, 0x31, 0x32, 0x33, 0x03, 0x43, 0x39];
        match parse_fx_links_response(&resp).unwrap() {
            FxLinksResponse::ReadData {
                station,
                pc,
                data,
            } => {
                assert_eq!(station, None);
                assert_eq!(pc, None);
                assert_eq!(data, b"0123");
            }
            other => panic!("应为 ReadData,实际 {other:?}"),
        }
    }

    /// ACK(写成功)与 NAK(文档布局:NAK 站号(2) 错误码(2))
    #[test]
    fn parse_ack_and_nak() {
        assert_eq!(parse_fx_links_response(&[0x06]).unwrap(), FxLinksResponse::Ack);
        match parse_fx_links_response(&[0x15, 0x30, 0x30, 0x30, 0x36, 0x0D, 0x0A]).unwrap() {
            FxLinksResponse::Nak {
                station,
                error_code,
            } => {
                assert_eq!(station, Some(0));
                assert_eq!(error_code, 0x06);
                assert!(fx_links_error_message(error_code).contains("字符错误"));
            }
            other => panic!("应为 Nak,实际 {other:?}"),
        }
        // 1 位错误码变体
        match parse_fx_links_response(&[0x15, 0x30, 0x30, 0x36]).unwrap() {
            FxLinksResponse::Nak {
                station,
                error_code,
            } => {
                assert_eq!(station, Some(0));
                assert_eq!(error_code, 0x06);
            }
            other => panic!("应为 Nak,实际 {other:?}"),
        }
        // 带 PC号 "FF" 变体
        match parse_fx_links_response(&[0x15, 0x30, 0x30, 0x46, 0x46, 0x36]).unwrap() {
            FxLinksResponse::Nak {
                station,
                error_code,
            } => {
                assert_eq!(station, Some(0));
                assert_eq!(error_code, 0x06);
            }
            other => panic!("应为 Nak,实际 {other:?}"),
        }
    }

    /// 校验和不符 → 报错
    #[test]
    fn rejects_checksum_mismatch() {
        let mut resp = vec![
            0x02, 0x30, 0x30, 0x46, 0x46, 0x31, 0x32, 0x33, 0x34, 0x03, 0x42, 0x39,
        ];
        resp[11] = 0x38; // 破坏校验 "B9" → "B8"
        let error = parse_fx_links_response(&resp).unwrap_err();
        assert_eq!(error.body().code, "FX_LINKS_CHECKSUM_MISMATCH");
    }

    /// 非法输入:空帧/未知前缀/站号越界/未知命令/延时越界/非法数据字符
    #[test]
    fn rejects_invalid_inputs() {
        assert_eq!(
            parse_fx_links_response(&[]).unwrap_err().body().code,
            "FX_LINKS_RESPONSE_EMPTY"
        );
        assert_eq!(
            parse_fx_links_response(&[0x41, 0x42]).unwrap_err().body().code,
            "FX_LINKS_RESPONSE_PREFIX"
        );
        assert_eq!(
            build_fx_links_request(16, "WR", 0, "").unwrap_err().body().code,
            "FX_LINKS_STATION_INVALID"
        );
        assert_eq!(
            build_fx_links_request(0, "ZZ", 0, "").unwrap_err().body().code,
            "FX_LINKS_CMD_UNKNOWN"
        );
        assert_eq!(
            build_fx_links_request(0, "wr", 0x10, "").unwrap_err().body().code,
            "FX_LINKS_DELAY_INVALID"
        );
        assert_eq!(
            build_fx_links_request(0, "WR", 0, "AB\nCD").unwrap_err().body().code,
            "FX_LINKS_DATA_INVALID"
        );
        // 小写命令自动转大写
        assert_eq!(&build_fx_links_request(0, "wr", 0, "").unwrap()[5..7], b"WR");
    }
}
