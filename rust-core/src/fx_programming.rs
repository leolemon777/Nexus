//! 三菱 FX 编程口协议(编程口 RS-422 / SC09 / USB-SC09,固定 9600/7/E/1,ASCII)。
//!
//! 规范来源:《三菱全协议设计文档.md》§3.3(手册 JY992D82001)。
//!
//! 命令帧(PC → PLC):
//! `[STX 02H][CMD 1]["0"读 / "1"写 / "7"强制ON / "8"强制OFF][首地址 4 ASCII hex][字节数 2][写入数据][ETX 03H][SUM 2]`
//! 强制帧无字节数字段(仅 STX + CMD + 地址(4) + ETX + SUM)。
//! 读响应:`[STX][数据][ETX][SUM]`;写/强制响应:`[ACK 06H]` 成功 / `[NAK 15H][错误码]` 失败。
//!
//! 和校验:CMD ~ ETX 全部字节累加取低 8 位 → 2 个 ASCII hex 字符
//! (文档 §3.3.5(1) 实例:CMD"0"+"10F6"+"04"+ETX = 0x174 → "74")。
//!
//! 地址编码(§3.3.4,两张表,严格区分):
//! - 读/写(CMD 0/1):地址 = 组基地址 + 编号×2(X/Y 编号为八进制书写,按十进制值代入)
//! - 强制 ON/OFF(CMD 7/8):地址 = 编号÷8(整数)+ 强制基址;且地址 4 字符**低位在前**发送
//!
//! 已知文档勘误(实现取值):
//! - 特殊 D:文档示例「E00H+8000×2=4E00H」算术不成立(0xE00+0x3E80=0x4C80);
//!   按通行规则实现为 `0x0E00 + (编号-8000)×2`(D8000 → 0x0E00)。
//! - C 当前值 32 位:文档示例「C00H+200×2=1000H」与 D0 区冲突;
//!   按通行规则实现为 `0x0C00 + (编号-200)×4`(C200 → 0x0C00)。
//! - 强制 ON M100 示例的地址字节(「C008」)只能由字符串拼接("800"+"C")导出,
//!   与表二的数值公式(0x800+0xC=0x80C)及「低位字符在前」规则矛盾(按规则应为 "C080");
//!   实现遵循规则文字(0x080C → "C080"),示例 SUM 字节亦有笔误(按算法应为 "15")。

use crate::error::CoreError;

/// 控制字符
pub const STX: u8 = 0x02;
pub const ETX: u8 = 0x03;
pub const ACK: u8 = 0x06;
pub const NAK: u8 = 0x15;

/// CMD 一览(§3.3.3)
pub const CMD_DEVICE_READ: u8 = b'0'; // DEVICE READ
pub const CMD_DEVICE_WRITE: u8 = b'1'; // DEVICE WRITE
pub const CMD_FORCE_ON: u8 = b'7'; // FORCE ON
pub const CMD_FORCE_OFF: u8 = b'8'; // FORCE OFF

/// 读 PLC 型号的特殊地址(§3.3.5(2):0E02H,2 字节,响应 "C256" = FX1S)
pub const MODEL_TYPE_ADDRESS: u16 = 0x0E02;
/// 读 PLC 运行状态的特殊地址(§3.3.5(3):01E0H,"09"=运行 / "0A"=暂停)
pub const RUN_STATUS_ADDRESS: u16 = 0x01E0;

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

fn parse_two_hex(high: u8, low: u8) -> Option<u8> {
    Some(hex_digit_value(high)? << 4 | hex_digit_value(low)?)
}

/// 和校验:给定字节(CMD ~ ETX 范围)累加取低 8 位。
pub fn fx_prog_checksum(bytes: &[u8]) -> u8 {
    bytes.iter().fold(0u8, |acc, byte| acc.wrapping_add(*byte))
}

/// 读/写命令(CMD 0/1)组基地址表(§3.3.4 表一)。
fn rw_base(device: &str) -> Option<u16> {
    match device {
        "X" => Some(0x0080),  // X → 80H(X17(=15dec) → 9EH)
        "Y" => Some(0x00A0),  // Y → A0H
        "M" => Some(0x0100),  // M → 100H(M100 → 1C8H)
        "S" => Some(0x0000),  // S → 0H
        "T" => Some(0x00C0),  // T 触点 → C0H
        "C" => Some(0x01C0),  // C 触点 → 1C0H
        "TN" => Some(0x0800), // T 当前值 → 800H
        "CN" => Some(0x0A00), // C 当前值(16 位) → A00H
        "CN32" => Some(0x0C00), // C 当前值(32 位,C200 起) → C00H
        "D" => Some(0x1000),  // D → 1000H(D123 → 10F6H)
        _ => None,
    }
}

/// 强制 ON/OFF(CMD 7/8)基址表(§3.3.4 表二,与读/写**不是同一张表**)。
fn force_base(device: &str) -> Option<u16> {
    match device {
        "X" => Some(0x0100),
        "Y" => Some(0x0200),
        "M" => Some(0x0800), // M100 → 100/8=0CH → 80CH
        "S" => Some(0x0000),
        "T" => Some(0x0300), // [实机验证]
        "C" => Some(0x0400), // [实机验证]
        _ => None,
    }
}

/// 读/写命令地址 = 组基地址 + 编号×2(§3.3.4 表一)。
///
/// `number` 为十进制编号(X/Y 的八进制书写请先用 [fx_prog_parse_number] 转换)。
/// D≥8000 走特殊 D 区,CN32 从 C200 起(步长 4,32 位)。
pub fn fx_prog_rw_address(device: &str, number: u32) -> Result<u16, CoreError> {
    let device = device.to_ascii_uppercase();
    let address = match device.as_str() {
        "D" if number >= 8000 => 0x0E00 + (number - 8000) * 2,
        "CN32" if number < 200 => {
            return Err(err(
                "FX_PROG_NUMBER_INVALID",
                "CN32 的编号从 C200 起(0~199 请用 CN)".into(),
            ))
        }
        "CN32" => 0x0C00 + (number - 200) * 4,
        other => {
            let base = rw_base(other).ok_or_else(|| {
                err(
                    "FX_PROG_DEVICE_UNKNOWN",
                    format!("未知软元件「{other}」(支持 X/Y/M/S/T/C/TN/CN/CN32/D)"),
                )
            })? as u32;
            base + number * 2
        }
    };
    u16::try_from(address).map_err(|_| {
        err(
            "FX_PROG_ADDR_OVERFLOW",
            format!("软元件「{device}」编号 {number} 的地址 {address:#06X} 超出 4 位 hex 表示范围"),
        )
    })
}

/// 强制 ON/OFF 地址 = 编号÷8(整数)+ 强制基址(§3.3.4 表二)。
pub fn fx_prog_force_address(device: &str, number: u32) -> Result<u16, CoreError> {
    let device = device.to_ascii_uppercase();
    let base = force_base(device.as_str()).ok_or_else(|| {
        err(
            "FX_PROG_DEVICE_UNKNOWN",
            format!("强制命令不支持软元件「{device}」(支持 X/Y/M/S/T/C)"),
        )
    })? as u32;
    let address = base + number / 8;
    u16::try_from(address).map_err(|_| {
        err(
            "FX_PROG_ADDR_OVERFLOW",
            format!("强制地址 {address:#06X} 超出 4 位 hex 表示范围"),
        )
    })
}

/// 解析用户输入的软元件编号:X/Y 按八进制(§3.3.4「X/Y 为八进制→按十进制值计算」),其余十进制。
pub fn fx_prog_parse_number(device: &str, number: &str) -> Result<u32, CoreError> {
    let device = device.to_ascii_uppercase();
    let radix = if device == "X" || device == "Y" { 8 } else { 10 };
    u32::from_str_radix(number.trim(), radix).map_err(|_| {
        err(
            "FX_PROG_NUMBER_INVALID",
            format!("「{number}」不是合法的软元件编号({device} 为 {} 进制)", if radix == 8 { "八" } else { "十" }),
        )
    })
}

/// 地址 4 字符,高位字符在前(§3.3.4:读/写命令,如 10F6H → "10F6")
fn addr_chars_high_first(address: u16) -> [u8; 4] {
    let chars = format!("{address:04X}");
    [chars.as_bytes()[0], chars.as_bytes()[1], chars.as_bytes()[2], chars.as_bytes()[3]]
}

/// 地址 4 字符,低位字符在前(§3.3.4:强制命令,如 80CH → "C008")
fn addr_chars_low_first(address: u16) -> [u8; 4] {
    let chars = format!("{address:04X}");
    [chars.as_bytes()[3], chars.as_bytes()[2], chars.as_bytes()[1], chars.as_bytes()[0]]
}

/// 追加 ETX + 和校验(CMD ~ ETX 累加)收尾。
fn finish_frame(body: &[u8]) -> Vec<u8> {
    let mut frame = Vec::with_capacity(body.len() + 4);
    frame.push(STX);
    frame.extend_from_slice(body);
    frame.push(ETX);
    let sum = fx_prog_checksum(&frame[1..]);
    frame.push(hex_char(sum >> 4));
    frame.push(hex_char(sum & 0x0F));
    frame
}

/// 按已编码地址构建 DEVICE READ 帧(供型号 0E02H / 运行状态 01E0H 等特殊地址使用)。
///
/// 文档 §3.3.5(2) 示例:读型号 `02 30 30 45 30 32 30 32 03 36 43`。
pub fn build_fx_prog_read_by_address(address: u16, bytes: u16) -> Result<Vec<u8>, CoreError> {
    if bytes == 0 || bytes > 0xFF {
        return Err(err(
            "FX_PROG_BYTES_INVALID",
            "字节数须在 1~255(2 字符 ASCII hex)".into(),
        ));
    }
    let mut body = Vec::with_capacity(8);
    body.push(CMD_DEVICE_READ);
    body.extend_from_slice(&addr_chars_high_first(address));
    body.push(hex_char((bytes >> 4) as u8));
    body.push(hex_char((bytes & 0x0F) as u8));
    Ok(finish_frame(&body))
}

/// DEVICE READ(按字节):软元件 + 编号 + 字节数。
pub fn build_fx_prog_read_bytes(
    device: &str,
    number: u32,
    bytes: u16,
) -> Result<Vec<u8>, CoreError> {
    let address = fx_prog_rw_address(device, number)?;
    build_fx_prog_read_by_address(address, bytes)
}

/// DEVICE READ(按字):`words` 个字 = `words×2` 字节。
///
/// 文档 §3.3.5(1) 示例:读 D123 起 4 字节 →
/// `build_fx_prog_read("D", 123, 2)` == `02 30 31 30 46 36 30 34 03 37 34`。
pub fn build_fx_prog_read(
    device: &str,
    number: u32,
    words: u16,
) -> Result<Vec<u8>, CoreError> {
    if words == 0 || words > 0x7F {
        return Err(err(
            "FX_PROG_WORDS_INVALID",
            "字数须在 1~127(字节数 2 字符 ASCII hex 上限)".into(),
        ));
    }
    build_fx_prog_read_bytes(device, number, words * 2)
}

/// DEVICE WRITE(按字节):原始字节逐字节编码为 2 个 ASCII hex 字符(高半字节在前)。
pub fn build_fx_prog_write_bytes(
    device: &str,
    number: u32,
    data: &[u8],
) -> Result<Vec<u8>, CoreError> {
    let address = fx_prog_rw_address(device, number)?;
    if data.is_empty() || data.len() > 0xFF {
        return Err(err(
            "FX_PROG_BYTES_INVALID",
            "写入数据须为 1~255 字节".into(),
        ));
    }
    let mut body = Vec::with_capacity(8 + data.len() * 2);
    body.push(CMD_DEVICE_WRITE);
    body.extend_from_slice(&addr_chars_high_first(address));
    body.push(hex_char((data.len() >> 4) as u8));
    body.push(hex_char((data.len() & 0x0F) as u8));
    for byte in data {
        body.push(hex_char(byte >> 4));
        body.push(hex_char(byte & 0x0F));
    }
    Ok(finish_frame(&body))
}

/// DEVICE WRITE(按字):每字低字节在前(§3.3.3「写数据 ASCII hex,低字节在前」)。
pub fn build_fx_prog_write(
    device: &str,
    number: u32,
    values: &[u16],
) -> Result<Vec<u8>, CoreError> {
    if values.is_empty() || values.len() > 0x7F {
        return Err(err(
            "FX_PROG_WORDS_INVALID",
            "写入字数须在 1~127".into(),
        ));
    }
    let mut raw = Vec::with_capacity(values.len() * 2);
    for value in values {
        raw.extend_from_slice(&value.to_le_bytes()); // 低字节在前
    }
    build_fx_prog_write_bytes(device, number, &raw)
}

/// FORCE ON(CMD "7"):地址用强制基址表,4 字符低位在前,无字节数字段。
pub fn build_fx_force_on(device: &str, number: u32) -> Result<Vec<u8>, CoreError> {
    let address = fx_prog_force_address(device, number)?;
    let mut body = Vec::with_capacity(5);
    body.push(CMD_FORCE_ON);
    body.extend_from_slice(&addr_chars_low_first(address));
    Ok(finish_frame(&body))
}

/// FORCE OFF(CMD "8"):同 FORCE ON,命令码不同。
pub fn build_fx_force_off(device: &str, number: u32) -> Result<Vec<u8>, CoreError> {
    let address = fx_prog_force_address(device, number)?;
    let mut body = Vec::with_capacity(5);
    body.push(CMD_FORCE_OFF);
    body.extend_from_slice(&addr_chars_low_first(address));
    Ok(finish_frame(&body))
}

/// 解析后的 FX 编程口响应。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FxProgResponse {
    /// STX + 数据(ASCII hex 字符)+ ETX + SUM
    Data(Vec<u8>),
    /// ACK:写/强制成功
    Ack,
    /// NAK:失败,错误码(1 字节,若有)
    Nak { error_code: Option<u8> },
}

/// 解析 PLC 响应:STX 数据(含和校验验证)/ ACK / NAK 错误码。
pub fn parse_fx_prog_response(bytes: &[u8]) -> Result<FxProgResponse, CoreError> {
    let Some(&first) = bytes.first() else {
        return Err(err("FX_PROG_RESPONSE_EMPTY", "响应帧为空(PLC 无应答?)".into()));
    };
    match first {
        ACK => Ok(FxProgResponse::Ack),
        NAK => {
            // NAK 后跟 1 字节错误码(若有;CR/LF 视为帧尾)
            let error_code = bytes[1..]
                .iter()
                .copied()
                .find(|byte| *byte != 0x0D && *byte != 0x0A);
            Ok(FxProgResponse::Nak { error_code })
        }
        STX => parse_stx_data(bytes),
        other => Err(err(
            "FX_PROG_RESPONSE_PREFIX",
            format!("响应首字节 {other:#04X} 不是 STX/ACK/NAK"),
        )),
    }
}

/// STX 帧:`STX 数据 ETX SUM`
fn parse_stx_data(bytes: &[u8]) -> Result<FxProgResponse, CoreError> {
    let etx = match bytes[1..].iter().position(|&byte| byte == ETX) {
        Some(index) => index + 1,
        None => {
            return Err(err(
                "FX_PROG_ETX_MISSING",
                "STX 响应缺少 ETX(03H) 终止符".into(),
            ))
        }
    };
    if etx < 2 {
        return Err(err("FX_PROG_DATA_EMPTY", "STX 与 ETX 之间无数据".into()));
    }
    if bytes.len() < etx + 3 {
        return Err(err("FX_PROG_SUM_MISSING", "ETX 后缺少 2 字符和校验".into()));
    }
    let received = parse_two_hex(bytes[etx + 1], bytes[etx + 2]).ok_or_else(|| {
        err("FX_PROG_SUM_MALFORMED", "和校验字符不是 ASCII hex".into())
    })?;
    let expected = fx_prog_checksum(&bytes[1..=etx]);
    if received != expected {
        return Err(err(
            "FX_PROG_CHECKSUM_MISMATCH",
            format!("和校验不符:期望 {expected:02X},收到 {received:02X}"),
        ));
    }
    Ok(FxProgResponse::Data(bytes[1..etx].to_vec()))
}

/// 把读响应的 ASCII hex 数据按字解码(低字节在前):"3412" → 0x1234。
pub fn decode_fx_prog_word_data(chars: &[u8]) -> Result<Vec<u16>, CoreError> {
    if chars.len() % 4 != 0 {
        return Err(err(
            "FX_PROG_DATA_LENGTH_INVALID",
            format!("数据 {} 字符,不是 4 的倍数(每字 4 字符)", chars.len()),
        ));
    }
    let mut words = Vec::with_capacity(chars.len() / 4);
    for chunk in chars.chunks_exact(4) {
        let low = parse_two_hex(chunk[0], chunk[1]).ok_or_else(|| {
            err("FX_PROG_DATA_MALFORMED", "数据字符不是 ASCII hex".into())
        })?;
        let high = parse_two_hex(chunk[2], chunk[3]).ok_or_else(|| {
            err("FX_PROG_DATA_MALFORMED", "数据字符不是 ASCII hex".into())
        })?;
        words.push(u16::from(high) << 8 | u16::from(low));
    }
    Ok(words)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 文档 §3.3.5(1) 完整示例:读 D123 起 4 字节(2 字)。
    /// 计算地址:123×2+0x1000 = 0x10F6;校验:0x30+0x31+0x30+0x46+0x36+0x30+0x34+0x03 = 0x174 → "74"。
    /// 逐字节对比(任务要求的标准向量):
    /// `02 30 31 30 46 36 30 34 03 37 34`
    #[test]
    fn read_d123_matches_doc_vector() {
        let frame = build_fx_prog_read("D", 123, 2).unwrap();
        assert_eq!(
            frame,
            vec![
                0x02, // STX
                0x30, // "0" CMD DEVICE READ
                0x31, 0x30, 0x46, 0x36, // "10F6" 首地址(D123×2+0x1000)
                0x30, 0x34, // "04" 字节数
                0x03, // ETX
                0x37, 0x34, // "74" SUM
            ]
        );
        // 按字节入口与按字入口一致
        assert_eq!(
            build_fx_prog_read_bytes("D", 123, 4).unwrap(),
            build_fx_prog_read("D", 123, 2).unwrap()
        );
    }

    /// 文档 §3.3.5(2):读 PLC 型号,地址 0E02H、2 字节 → `02 30 30 45 30 32 30 32 03 36 43`
    #[test]
    fn read_model_type_matches_doc_vector() {
        let frame = build_fx_prog_read_by_address(MODEL_TYPE_ADDRESS, 2).unwrap();
        assert_eq!(
            frame,
            vec![0x02, 0x30, 0x30, 0x45, 0x30, 0x32, 0x30, 0x32, 0x03, 0x36, 0x43]
        );
    }

    /// 文档 §3.3.5(2) 响应:`02 43 32 35 36 03 45 33` → 数据 "C256" = FX1S
    #[test]
    fn parse_model_type_response() {
        let resp = [0x02, 0x43, 0x32, 0x35, 0x36, 0x03, 0x45, 0x33];
        match parse_fx_prog_response(&resp).unwrap() {
            FxProgResponse::Data(data) => {
                assert_eq!(data, b"C256");
                assert_eq!(String::from_utf8_lossy(&data), "C256"); // FX1S
            }
            other => panic!("应为 Data,实际 {other:?}"),
        }
    }

    /// 文档 §3.3.5(4):强制 ON M100。地址 = 0x800+100/8 = 0x80C;
    /// 按规范规则「地址 4 位、低位字符在前」:0x080C → "080C" → 反转 "C080";
    /// SUM = 0x37+0x43+0x30+0x38+0x30+0x03 = 0x115 → "15"。
    /// (文档示例字节 "C008" 只能由 "800"+"C" 字符串拼接导出,与其自身的数值加法
    /// 公式和「低位字符在前」规则均矛盾,其 SUM 字节同样有笔误;此处按规则文字实现,
    /// 字段标注 [实机验证],接机后若有出入仅需调整 addr_chars_low_first。)
    #[test]
    fn force_on_m100_address_low_char_first() {
        assert_eq!(fx_prog_force_address("M", 100).unwrap(), 0x080C);
        let frame = build_fx_force_on("M", 100).unwrap();
        assert_eq!(
            frame,
            vec![
                0x02, // STX
                0x37, // "7" FORCE ON
                0x43, 0x30, 0x38, 0x30, // "C080"(0x080C 低位字符在前)
                0x03, // ETX
                0x31, 0x35, // "15" SUM
            ]
        );
    }

    /// 强制 OFF M0:地址 0x800 → "0080";SUM = 0x38+0x30+0x30+0x38+0x30+0x03 = 0x103 → "03"
    #[test]
    fn force_off_m0() {
        let frame = build_fx_force_off("M", 0).unwrap();
        assert_eq!(
            frame,
            vec![0x02, 0x38, 0x30, 0x30, 0x38, 0x30, 0x03, 0x30, 0x33]
        );
    }

    /// DEVICE WRITE:D0 = 0x1234 → 地址 "1000"、字节数 "02"、数据 "3412"(低字节在前)
    #[test]
    fn write_d0_word_low_byte_first() {
        let frame = build_fx_prog_write("D", 0, &[0x1234]).unwrap();
        assert_eq!(frame[0], 0x02);
        assert_eq!(&frame[1..2], b"1");
        assert_eq!(&frame[2..6], b"1000");
        assert_eq!(&frame[6..8], b"02");
        assert_eq!(&frame[8..12], b"3412");
        assert_eq!(frame[12], 0x03);
        let sum = fx_prog_checksum(&frame[1..=12]);
        assert_eq!(&frame[13..15], &[hex_char(sum >> 4), hex_char(sum & 0x0F)]);
        // 完整帧(手算 SUM = 0x221 → "21")
        assert_eq!(
            frame,
            vec![0x02, 0x31, 0x31, 0x30, 0x30, 0x30, 0x30, 0x32, 0x33, 0x34, 0x31, 0x32, 0x03, 0x32, 0x31]
        );
    }

    /// §3.3.4 表一(读/写基地址 + 编号×2)逐项核对
    #[test]
    fn rw_address_table_matches_doc() {
        assert_eq!(fx_prog_rw_address("X", 0).unwrap(), 0x0080); // X0 → 80H
        assert_eq!(fx_prog_rw_address("X", 15).unwrap(), 0x009E); // X17(=15dec) → 9EH
        assert_eq!(fx_prog_rw_address("Y", 0).unwrap(), 0x00A0);
        assert_eq!(fx_prog_rw_address("M", 0).unwrap(), 0x0100);
        assert_eq!(fx_prog_rw_address("M", 100).unwrap(), 0x01C8); // M100 → 1C8H
        assert_eq!(fx_prog_rw_address("S", 0).unwrap(), 0x0000);
        assert_eq!(fx_prog_rw_address("T", 0).unwrap(), 0x00C0);
        assert_eq!(fx_prog_rw_address("C", 0).unwrap(), 0x01C0);
        assert_eq!(fx_prog_rw_address("TN", 0).unwrap(), 0x0800); // T 当前值
        assert_eq!(fx_prog_rw_address("CN", 0).unwrap(), 0x0A00); // C 当前值 16 位
        assert_eq!(fx_prog_rw_address("CN32", 200).unwrap(), 0x0C00); // C200(32 位)
        assert_eq!(fx_prog_rw_address("CN32", 201).unwrap(), 0x0C04); // 步长 4
        assert_eq!(fx_prog_rw_address("D", 0).unwrap(), 0x1000);
        assert_eq!(fx_prog_rw_address("D", 123).unwrap(), 0x10F6); // 文档示例
        assert_eq!(fx_prog_rw_address("D", 8000).unwrap(), 0x0E00); // 特殊 D(见文件头勘误)
        assert_eq!(fx_prog_rw_address("D", 8001).unwrap(), 0x0E02);
    }

    /// §3.3.4 表二(强制基址 + 编号÷8)逐项核对
    #[test]
    fn force_address_table_matches_doc() {
        assert_eq!(fx_prog_force_address("X", 0).unwrap(), 0x0100);
        assert_eq!(fx_prog_force_address("Y", 0).unwrap(), 0x0200);
        assert_eq!(fx_prog_force_address("M", 0).unwrap(), 0x0800);
        assert_eq!(fx_prog_force_address("M", 100).unwrap(), 0x080C); // 文档示例
        assert_eq!(fx_prog_force_address("S", 0).unwrap(), 0x0000);
        assert_eq!(fx_prog_force_address("T", 0).unwrap(), 0x0300);
        assert_eq!(fx_prog_force_address("C", 0).unwrap(), 0x0400);
    }

    /// X/Y 编号按八进制解析:"X17" → 15
    #[test]
    fn octal_number_parsing() {
        assert_eq!(fx_prog_parse_number("X", "17").unwrap(), 15);
        assert_eq!(fx_prog_parse_number("Y", "10").unwrap(), 8);
        assert_eq!(fx_prog_parse_number("D", "123").unwrap(), 123);
        assert!(fx_prog_parse_number("X", "18").is_err()); // 8 不是八进制数字
        // X17 的读地址 = 0x80 + 15×2 = 0x9E(§3.3.4 示例)
        assert_eq!(
            fx_prog_rw_address("X", fx_prog_parse_number("X", "17").unwrap()).unwrap(),
            0x009E
        );
    }

    /// 读响应往返:读 D123 → 伪造响应 STX "3412" ETX SUM → 解码字 0x1234
    #[test]
    fn read_response_roundtrip() {
        let request = build_fx_prog_read("D", 123, 2).unwrap();
        assert_eq!(&request[1..2], b"0");
        // 响应数据 "3412"(低字节在前)
        let body = b"3412\x03";
        let sum = fx_prog_checksum(body);
        let mut response = vec![0x02];
        response.extend_from_slice(body);
        response.push(hex_char(sum >> 4));
        response.push(hex_char(sum & 0x0F));
        match parse_fx_prog_response(&response).unwrap() {
            FxProgResponse::Data(data) => {
                assert_eq!(data, b"3412");
                assert_eq!(decode_fx_prog_word_data(&data).unwrap(), vec![0x1234]);
            }
            other => panic!("应为 Data,实际 {other:?}"),
        }
    }

    /// ACK(写成功)与 NAK(失败,错误码 1 字节 / 裸 NAK)
    #[test]
    fn parse_ack_and_nak() {
        assert_eq!(parse_fx_prog_response(&[0x06]).unwrap(), FxProgResponse::Ack);
        assert_eq!(
            parse_fx_prog_response(&[0x15, 0x06]).unwrap(),
            FxProgResponse::Nak {
                error_code: Some(0x06)
            }
        );
        assert_eq!(
            parse_fx_prog_response(&[0x15, 0x0D, 0x0A]).unwrap(),
            FxProgResponse::Nak { error_code: None }
        );
    }

    /// 校验和不符 / 非法前缀 / 空帧 → 报错
    #[test]
    fn rejects_invalid_responses() {
        // 数据 "3412"(SUM 应为 "CD"),把 SUM 破坏为 "CE"
        let mut resp = vec![0x02, 0x33, 0x34, 0x31, 0x32, 0x03, 0x43, 0x44];
        resp[7] = 0x45;
        assert_eq!(
            parse_fx_prog_response(&resp).unwrap_err().body().code,
            "FX_PROG_CHECKSUM_MISMATCH"
        );
        resp[0] = 0x41;
        assert_eq!(
            parse_fx_prog_response(&resp).unwrap_err().body().code,
            "FX_PROG_RESPONSE_PREFIX"
        );
        assert_eq!(
            parse_fx_prog_response(&[]).unwrap_err().body().code,
            "FX_PROG_RESPONSE_EMPTY"
        );
    }

    /// 非法参数:未知软元件 / 字数 0 / 地址溢出 / 强制不支持 D
    #[test]
    fn rejects_invalid_inputs() {
        assert_eq!(
            build_fx_prog_read("Z", 0, 1).unwrap_err().body().code,
            "FX_PROG_DEVICE_UNKNOWN"
        );
        assert_eq!(
            build_fx_prog_read("D", 0, 0).unwrap_err().body().code,
            "FX_PROG_WORDS_INVALID"
        );
        assert_eq!(
            build_fx_prog_write("D", 0, &[]).unwrap_err().body().code,
            "FX_PROG_WORDS_INVALID"
        );
        // D70000 ×2 + 0x1000 超出 0xFFFF
        assert_eq!(
            build_fx_prog_read("D", 70000, 1).unwrap_err().body().code,
            "FX_PROG_ADDR_OVERFLOW"
        );
        assert_eq!(
            build_fx_force_on("D", 0).unwrap_err().body().code,
            "FX_PROG_DEVICE_UNKNOWN"
        );
        assert_eq!(
            build_fx_prog_read("CN32", 100, 1).unwrap_err().body().code,
            "FX_PROG_NUMBER_INVALID"
        );
    }
}
