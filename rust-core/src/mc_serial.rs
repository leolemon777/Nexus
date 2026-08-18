//! 三菱 MC 协议**串口 C24**帧编码层(QJ71C24/RJ71C24/AJ71C24,§3.1)。
//!
//! 核心认知(§3.1):应用层(指令/子命令/软元件)与以太网 3E 帧 **100% 复用**——
//! [`crate::mc_pdu::build_read_batch_pdu`] 等函数产出的就是「3E 应用数据区」,
//! 本模块只在外面再包一层串口封装:
//!
//! - **3C 帧(格式1,ASCII+和校验,§3.1.2)**:
//!   `站号(2 ASCII hex) | 报文体(3E 应用区逐字节 {:02X} 的 ASCII) | ETX(03H) | 和校验(2 ASCII hex) | CR LF`
//! - **3C 帧(格式3,二进制+和校验)**:
//!   `站号(1B) | 报文体(3E 应用区原样二进制) | ETX(03H) | 和校验(2B LE 累加和)`
//! - **4C 格式4(二进制无校验)** = 3C 去掉校验:`站号(1B) | 报文体 | CR LF`
//!
//! 和校验范围(§3.1.2):**站号首字符 ~ ETX(含)**;格式1 取低 8 位输出 2 个 ASCII hex,
//! 格式3 输出 16 位累加和的小端 2 字节。
//!
//! 响应解析:剥掉站号/校验/ETX/CRLF 封装,还原 3E 应用区响应体(结束代码+数据),
//! 之后交给 [`crate::mc_pdu::parse_read_batch_response`] 等按 3E 方式解析(见本模块测试
//! `app_layer_reuses_3e_pdu`)。
//!
//! ⚠️ RS-485 半双工时序(RTS 方向控制、turnaround 延时、严格一问一答)属传输层,
//! 由串口驱动负责,不在本编码器范围内(§3.1.3)。

use crate::error::CoreError;

/// ETX 控制字符(03H)
pub const ETX: u8 = 0x03;
/// CR(0DH)
pub const CR: u8 = 0x0D;
/// LF(0AH)
pub const LF: u8 = 0x0A;

/// C24 站号上限(§3.1.3:00~31,模块参数)
pub const MAX_STATION: u8 = 31;

/// 串口帧数据格式(§3.1 帧类型×数据格式矩阵的工程子集:3C 常用格式 1/3,4C 用格式 4)。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum McSerialFormat {
    /// 格式1:报文体 ASCII + 和校验(2 ASCII hex)+ CR/LF
    Format1Ascii,
    /// 格式3:报文体二进制 + 和校验(2 字节 LE 累加和)
    Format3Binary,
    /// 格式4:报文体二进制、无校验(站号+报文体+CR/LF)
    Format4BinaryNoChecksum,
}

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

/// 和校验(格式1):给定字节累加取低 8 位。
///
/// 调用方应传入「站号首字符 ~ ETX(含)」范围(§3.1.2)。
pub fn mc_serial_checksum_ascii(bytes: &[u8]) -> u8 {
    bytes.iter().fold(0u8, |acc, byte| acc.wrapping_add(*byte))
}

/// 和校验(格式3):16 位累加和(小端 2 字节附加在帧尾)。
fn checksum_u16(bytes: &[u8]) -> u16 {
    bytes.iter().fold(0u16, |acc, byte| acc.wrapping_add(u16::from(*byte)))
}

/// 构建 3C/4C 串口请求帧。
///
/// `mc_app_data` 为 `mc_pdu` 产出的 3E 应用数据区(指令+子命令+软元件+点数…),
/// 本函数不做应用层校验——合法性由 `mc_pdu` 侧保证。
pub fn build_mc_serial_3c(
    station: u8,
    format: McSerialFormat,
    mc_app_data: &[u8],
) -> Result<Vec<u8>, CoreError> {
    if station > MAX_STATION {
        return Err(err(
            "MC_SERIAL_STATION_INVALID",
            format!("站号 {station} 超出范围 0~31(C24 模块参数,§3.1.3)"),
        ));
    }
    if mc_app_data.is_empty() {
        return Err(err(
            "MC_SERIAL_EMPTY_BODY",
            "报文体(3E 应用区)为空:先由 mc_pdu 构建指令数据区".into(),
        ));
    }
    match format {
        McSerialFormat::Format1Ascii => {
            // 站号(2 ASCII)+ 报文体(逐字节 {:02X})+ ETX + 和校验(2 ASCII)+ CR LF
            let mut frame = Vec::with_capacity(2 + mc_app_data.len() * 2 + 5);
            frame.push(hex_char(station >> 4));
            frame.push(hex_char(station & 0x0F));
            for byte in mc_app_data {
                frame.push(hex_char(byte >> 4));
                frame.push(hex_char(byte & 0x0F));
            }
            frame.push(ETX);
            // 和校验范围:站号首字符 ~ ETX(含)——恰为 frame 当前全部内容
            let sum = mc_serial_checksum_ascii(&frame);
            frame.push(hex_char(sum >> 4));
            frame.push(hex_char(sum & 0x0F));
            frame.push(CR);
            frame.push(LF);
            Ok(frame)
        }
        McSerialFormat::Format3Binary => {
            // 站号(1B)+ 报文体(二进制原样)+ ETX + 和校验(2B LE)
            let mut frame = Vec::with_capacity(1 + mc_app_data.len() + 3);
            frame.push(station);
            frame.extend_from_slice(mc_app_data);
            frame.push(ETX);
            let sum = checksum_u16(&frame);
            frame.extend_from_slice(&sum.to_le_bytes());
            Ok(frame)
        }
        McSerialFormat::Format4BinaryNoChecksum => {
            // 站号(1B)+ 报文体(二进制原样)+ CR LF(无校验、无 ETX)
            let mut frame = Vec::with_capacity(1 + mc_app_data.len() + 2);
            frame.push(station);
            frame.extend_from_slice(mc_app_data);
            frame.push(CR);
            frame.push(LF);
            Ok(frame)
        }
    }
}

/// 解析 3C/4C 串口响应帧,还原为 `(站号, 3E 应用区响应体)`。
///
/// 应用区响应体 = 结束代码(2B)+ 数据,可继续交给
/// [`crate::mc_pdu::parse_read_batch_response`] / [`crate::mc_frame::end_code_message`] 处理。
///
/// 宽容策略:格式1/格式4 的尾部 CR/LF 可缺省(部分模块/抓包工具会剥离);
/// 格式3 尾部固定 2 字节校验,不做 CRLF 剥离(校验和字节可能恰为 0D 0A)。
pub fn parse_mc_serial_3c_response(
    bytes: &[u8],
    format: McSerialFormat,
) -> Result<(u8, Vec<u8>), CoreError> {
    match format {
        McSerialFormat::Format1Ascii => {
            let frame = strip_crlf(bytes);
            // [站号 2 ASCII][报文体 ASCII][ETX][和校验 2 ASCII]
            if frame.len() < 5 {
                return Err(err(
                    "MC_SERIAL_FRAME_TOO_SHORT",
                    format!("格式1 响应 {} 字节,短于最小 5(站号2+ETX1+和校验2)", frame.len()),
                ));
            }
            let etx_idx = frame.len() - 3;
            if frame[etx_idx] != ETX {
                return Err(err(
                    "MC_SERIAL_ETX_MISSING",
                    format!("倒数第 3 字节 {:#04X} 不是 ETX(03H)", frame[etx_idx]),
                ));
            }
            let station = parse_two_hex(frame[0], frame[1]).ok_or_else(|| {
                err(
                    "MC_SERIAL_BAD_HEX",
                    format!("站号「{}」不是合法 ASCII hex", String::from_utf8_lossy(&frame[0..2])),
                )
            })?;
            let ascii = &frame[2..etx_idx];
            if ascii.len() % 2 != 0 {
                return Err(err(
                    "MC_SERIAL_BAD_HEX",
                    format!("报文体 ASCII 长度 {} 为奇数,无法配对解码", ascii.len()),
                ));
            }
            let mut app = Vec::with_capacity(ascii.len() / 2);
            for pair in ascii.chunks_exact(2) {
                app.push(parse_two_hex(pair[0], pair[1]).ok_or_else(|| {
                    err(
                        "MC_SERIAL_BAD_HEX",
                        format!("报文体含非 ASCII hex 字符「{}」", String::from_utf8_lossy(pair)),
                    )
                })?);
            }
            let expect = parse_two_hex(frame[etx_idx + 1], frame[etx_idx + 2]).ok_or_else(|| {
                err(
                    "MC_SERIAL_BAD_HEX",
                    format!(
                        "和校验「{}」不是合法 ASCII hex",
                        String::from_utf8_lossy(&frame[etx_idx + 1..etx_idx + 3])
                    ),
                )
            })?;
            let actual = mc_serial_checksum_ascii(&frame[..=etx_idx]);
            if expect != actual {
                return Err(err(
                    "MC_SERIAL_CHECKSUM_MISMATCH",
                    format!("和校验不符:收到 {expect:02X},计算 {actual:02X}(范围=站号首字符~ETX)"),
                ));
            }
            Ok((station, app))
        }
        McSerialFormat::Format3Binary => {
            // [站号 1B][报文体二进制][ETX][和校验 2B LE]
            if bytes.len() < 4 {
                return Err(err(
                    "MC_SERIAL_FRAME_TOO_SHORT",
                    format!("格式3 响应 {} 字节,短于最小 4(站号1+ETX1+和校验2)", bytes.len()),
                ));
            }
            let etx_idx = bytes.len() - 3;
            if bytes[etx_idx] != ETX {
                return Err(err(
                    "MC_SERIAL_ETX_MISSING",
                    format!("倒数第 3 字节 {:#04X} 不是 ETX(03H)", bytes[etx_idx]),
                ));
            }
            let expect = u16::from_le_bytes([bytes[etx_idx + 1], bytes[etx_idx + 2]]);
            let actual = checksum_u16(&bytes[..=etx_idx]);
            if expect != actual {
                return Err(err(
                    "MC_SERIAL_CHECKSUM_MISMATCH",
                    format!("和校验不符:收到 {expect:04X},计算 {actual:04X}(范围=站号~ETX 16位累加)"),
                ));
            }
            Ok((bytes[0], bytes[1..etx_idx].to_vec()))
        }
        McSerialFormat::Format4BinaryNoChecksum => {
            // [站号 1B][报文体二进制](尾部 CRLF 宽容剥离)
            let frame = strip_crlf(bytes);
            if frame.len() < 2 {
                return Err(err(
                    "MC_SERIAL_FRAME_TOO_SHORT",
                    format!("格式4 响应 {} 字节,短于最小 2(站号1+报文体≥1)", frame.len()),
                ));
            }
            Ok((frame[0], frame[1..].to_vec()))
        }
    }
}

/// 剥离尾部 CR LF(仅在恰好成对出现时)。
fn strip_crlf(bytes: &[u8]) -> &[u8] {
    if bytes.ends_with(&[CR, LF]) {
        &bytes[..bytes.len() - 2]
    } else {
        bytes
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// mc_pdu 读 D100 1 字的 3E 应用区(文档 §2.1.4-(2))
    const APP_READ_D100: [u8; 10] = [0x01, 0x04, 0x01, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00];

    /// 格式1 构造向量(含手算校验和):
    /// "00"(站号)+"01040100640000A80100"(报文体 ASCII)+ETX
    /// 手算和 = 0x60(站号两字符)+ 0x3EA(报文体 20 字符)+ 0x03(ETX) = 0x44D → 低 8 位 0x4D → "4D"
    #[test]
    fn format1_build_matches_hand_computed_checksum() {
        let frame = build_mc_serial_3c(0x00, McSerialFormat::Format1Ascii, &APP_READ_D100).unwrap();
        let mut expected: Vec<u8> = b"0001040100640000A80100".to_vec();
        expected.push(ETX);
        expected.extend_from_slice(b"4D");
        expected.push(CR);
        expected.push(LF);
        assert_eq!(frame, expected);
        // 独立复核校验范围(站号首字符~ETX 含)
        assert_eq!(mc_serial_checksum_ascii(b"0001040100640000A80100\x03"), 0x4D);
    }

    /// 格式3 二进制构造向量(手算 16 位累加和):
    /// 0x00(站号)+ 0x113(报文体 10 字节)+ 0x03(ETX) = 0x116 → LE `16 01`
    #[test]
    fn format3_build_matches_hand_computed_checksum() {
        let frame = build_mc_serial_3c(0x00, McSerialFormat::Format3Binary, &APP_READ_D100).unwrap();
        assert_eq!(
            frame,
            [0x00, 0x01, 0x04, 0x01, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00, 0x03, 0x16, 0x01]
        );
    }

    /// 格式4:站号 + 报文体二进制 + CR LF,无 ETX 无校验
    #[test]
    fn format4_build_layout() {
        let frame = build_mc_serial_3c(0x05, McSerialFormat::Format4BinaryNoChecksum, &[0xAA, 0x55]).unwrap();
        assert_eq!(frame, [0x05, 0xAA, 0x55, CR, LF]);
    }

    /// 响应解析往返:三种格式 build → parse 还原同一应用区
    #[test]
    fn response_roundtrip_all_formats() {
        // 模拟 PLC 响应应用区:结束代码 0000 + D100=0x1234(小端 34 12)
        let resp_app: Vec<u8> = vec![0x00, 0x00, 0x34, 0x12];
        let formats = [
            McSerialFormat::Format1Ascii,
            McSerialFormat::Format3Binary,
            McSerialFormat::Format4BinaryNoChecksum,
        ];
        for format in formats {
            let frame = build_mc_serial_3c(0x0A, format, &resp_app).unwrap();
            let (station, app) = parse_mc_serial_3c_response(&frame, format).unwrap();
            assert_eq!(station, 0x0A, "{format:?} 站号还原");
            assert_eq!(app, resp_app, "{format:?} 应用区往返");
        }
    }

    /// 应用层 100% 复用 3E:mc_pdu 组帧 → 3C 封装 → 响应解封装 → mc_pdu 解析
    #[test]
    fn app_layer_reuses_3e_pdu() {
        let addr = crate::mc_address::parse_mc_address("D100").unwrap();
        let app = crate::mc_pdu::build_read_batch_pdu(&addr, 1).unwrap();
        assert_eq!(app, APP_READ_D100);

        let resp_app: Vec<u8> = vec![0x00, 0x00, 0x34, 0x12]; // 结束代码 + 数据
        let resp_frame = build_mc_serial_3c(0x00, McSerialFormat::Format1Ascii, &resp_app).unwrap();
        let (_station, app_out) = parse_mc_serial_3c_response(&resp_frame, McSerialFormat::Format1Ascii).unwrap();
        let words = crate::mc_pdu::parse_read_batch_response(&app_out[2..], 1, false).unwrap();
        assert_eq!(words, vec![0x1234]);
    }

    /// 格式1 校验错误检出:篡改报文体一个字符
    #[test]
    fn format1_detects_checksum_mismatch() {
        let mut frame = build_mc_serial_3c(0x00, McSerialFormat::Format1Ascii, &APP_READ_D100).unwrap();
        frame[3] ^= 0x01; // 篡改报文体首字节的一个半字符
        let e = parse_mc_serial_3c_response(&frame, McSerialFormat::Format1Ascii).unwrap_err();
        assert_eq!(e.body().code, "MC_SERIAL_CHECKSUM_MISMATCH");
    }

    /// 格式3 校验错误检出:篡改校验字节
    #[test]
    fn format3_detects_checksum_mismatch() {
        let mut frame = build_mc_serial_3c(0x00, McSerialFormat::Format3Binary, &APP_READ_D100).unwrap();
        let last = frame.len() - 1;
        frame[last] ^= 0xFF;
        let e = parse_mc_serial_3c_response(&frame, McSerialFormat::Format3Binary).unwrap_err();
        assert_eq!(e.body().code, "MC_SERIAL_CHECKSUM_MISMATCH");
    }

    /// 站号 00~31 范围校验(§3.1.3)
    #[test]
    fn rejects_station_out_of_range() {
        assert!(build_mc_serial_3c(31, McSerialFormat::Format1Ascii, &[0x01]).is_ok());
        let e = build_mc_serial_3c(32, McSerialFormat::Format1Ascii, &[0x01]).unwrap_err();
        assert_eq!(e.body().code, "MC_SERIAL_STATION_INVALID");
    }

    #[test]
    fn rejects_empty_app_data() {
        let e = build_mc_serial_3c(0x00, McSerialFormat::Format3Binary, &[]).unwrap_err();
        assert_eq!(e.body().code, "MC_SERIAL_EMPTY_BODY");
    }

    #[test]
    fn parse_rejects_too_short_and_missing_etx() {
        let e = parse_mc_serial_3c_response(b"00", McSerialFormat::Format1Ascii).unwrap_err();
        assert_eq!(e.body().code, "MC_SERIAL_FRAME_TOO_SHORT");
        let e = parse_mc_serial_3c_response(&[0x00, 0x01, 0x02, 0x03], McSerialFormat::Format3Binary).unwrap_err();
        assert_eq!(e.body().code, "MC_SERIAL_ETX_MISSING");
    }

    /// 格式1 宽容:尾部 CR/LF 可缺省(抓包工具剥离场景)
    #[test]
    fn format1_tolerates_missing_crlf() {
        let mut frame = build_mc_serial_3c(0x00, McSerialFormat::Format1Ascii, &[0x00, 0x00, 0x34, 0x12]).unwrap();
        frame.truncate(frame.len() - 2); // 去掉 CR LF
        let (station, app) = parse_mc_serial_3c_response(&frame, McSerialFormat::Format1Ascii).unwrap();
        assert_eq!((station, app.as_slice()), (0x00, &[0x00, 0x00, 0x34, 0x12][..]));
    }

    /// 格式1 站号用 ASCII hex 表示:站号 0x0A → "0A"
    #[test]
    fn format1_station_is_two_ascii_hex_chars() {
        let frame = build_mc_serial_3c(0x0A, McSerialFormat::Format1Ascii, &[0xAB]).unwrap();
        assert_eq!(&frame[..2], b"0A");
        assert_eq!(&frame[2..4], b"AB");
    }
}
