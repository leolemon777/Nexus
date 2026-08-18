//! Modbus ASCII 帧编解码 + LRC 校验。
//!
//! ASCII 帧格式:
//! ```text
//! ':'  +  Hex(unit_id)  +  Hex(pdu)  +  Hex(lrc)  +  CR  +  LF
//! ```
//! 每个 raw 字节编码为 2 个 hex 字符(大写),所以帧是纯 ASCII 文本。
//! 无 CRC(用 LRC 替代),起始符 `':'`(0x3A),结束符 `CR LF`(0x0D 0x0A)。

use crate::modbus_rtu::RtuError;

/// 计算 LRC(Longitudinal Redundancy Check)。
///
/// LRC = -(sum of all bytes) mod 256,即所有字节的和的补码。
pub fn compute_lrc(bytes: &[u8]) -> u8 {
    let sum: u32 = bytes.iter().map(|&b| u32::from(b)).sum();
    (!(sum as u8)).wrapping_add(1)
}

/// 验证 LRC。
pub fn verify_lrc(bytes: &[u8], expected: u8) -> bool {
    compute_lrc(bytes) == expected
}

/// 构建 ASCII 帧:输入 unit_id + pdu,返回完整 ASCII 字节(含 `:` 和 `CRLF`)。
pub fn build_ascii_frame(unit_id: u8, pdu: &[u8]) -> Vec<u8> {
    let mut content = Vec::with_capacity(1 + pdu.len());
    content.push(unit_id);
    content.extend_from_slice(pdu);
    let lrc = compute_lrc(&content);
    content.push(lrc);

    let mut frame = Vec::with_capacity(1 + content.len() * 2 + 2);
    frame.push(b':');
    for &byte in &content {
        frame.extend_from_slice(&byte_to_hex_upper(byte));
    }
    frame.push(b'\r');
    frame.push(b'\n');
    frame
}

/// 解析 ASCII 帧:输入完整 ASCII 字节,返回 (unit_id, pdu)。
pub fn parse_ascii_frame(bytes: &[u8]) -> Result<(u8, Vec<u8>), RtuError> {
    if bytes.len() < 3 {
        return Err(RtuError::AsciiFrameTooShort { len: bytes.len() });
    }
    if bytes[0] != b':' {
        return Err(RtuError::AsciiStartByteMissing);
    }
    // 去掉起始 ':' 和结束 CR LF
    let end = bytes.len();
    let mut content_end = end;
    if bytes[end - 1] == b'\n' {
        content_end -= 1;
    } else {
        return Err(RtuError::AsciiEndBytesMissing);
    }
    if bytes[content_end - 1] == b'\r' {
        content_end -= 1;
    }
    let hex_str = &bytes[1..content_end];
    if hex_str.len() % 2 != 0 || hex_str.len() < 4 {
        // 至少 unit_id(2 hex) + lrc(2 hex) = 4
        return Err(RtuError::AsciiFrameTooShort { len: bytes.len() });
    }
    let raw = hex_to_bytes(hex_str)?;
    // raw = unit_id + pdu + lrc
    let lrc_index = raw.len() - 1;
    let received_lrc = raw[lrc_index];
    let content = &raw[..lrc_index];
    let expected_lrc = compute_lrc(content);
    if received_lrc != expected_lrc {
        return Err(RtuError::LrcMismatch {
            expected: expected_lrc,
            received: received_lrc,
        });
    }
    let unit_id = content[0];
    let pdu = content[1..].to_vec();
    Ok((unit_id, pdu))
}

/// 单字节转大写 hex(2 字符)。
fn byte_to_hex_upper(byte: u8) -> [u8; 2] {
    const HEX_DIGITS: &[u8; 16] = b"0123456789ABCDEF";
    [
        HEX_DIGITS[(byte >> 4) as usize],
        HEX_DIGITS[(byte & 0x0F) as usize],
    ]
}

/// hex 字节序列转 raw 字节。
fn hex_to_bytes(hex: &[u8]) -> Result<Vec<u8>, RtuError> {
    let mut bytes = Vec::with_capacity(hex.len() / 2);
    let mut i = 0;
    while i < hex.len() {
        let high = hex_nibble(hex[i])?;
        let low = hex_nibble(hex[i + 1])?;
        bytes.push((high << 4) | low);
        i += 2;
    }
    Ok(bytes)
}

fn hex_nibble(byte: u8) -> Result<u8, RtuError> {
    match byte {
        b'0'..=b'9' => Ok(byte - b'0'),
        b'A'..=b'F' => Ok(byte - b'A' + 10),
        b'a'..=b'f' => Ok(byte - b'a' + 10),
        _ => Err(RtuError::AsciiHexDecodeFailed {
            char: char::from(byte),
        }),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lrc_canonical_vector() {
        // 经典向量:unit=1, FC=03, addr=0, qty=10 → [01 03 00 00 00 0A]
        // sum = 0x0E, LRC = -0x0E = 0xF2
        assert_eq!(compute_lrc(&[0x01, 0x03, 0x00, 0x00, 0x00, 0x0A]), 0xF2);
    }

    #[test]
    fn lrc_verify_works() {
        let data = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A];
        let lrc = compute_lrc(&data);
        assert!(verify_lrc(&data, lrc));
        assert!(!verify_lrc(&data, lrc.wrapping_add(1)));
    }

    #[test]
    fn build_ascii_frame_matches_canonical_form() {
        // FC03 读 10 个保持寄存器,unit=1
        let pdu = [0x03, 0x00, 0x00, 0x00, 0x0A];
        let frame = build_ascii_frame(1, &pdu);
        let frame_str = std::str::from_utf8(&frame).unwrap();
        // : 01 03 00 00 00 0A F2 CR LF
        assert_eq!(frame_str, ":01030000000AF2\r\n");
    }

    #[test]
    fn parse_ascii_frame_round_trips() {
        let pdu = vec![0x03, 0x00, 0x00, 0x00, 0x0A];
        let frame = build_ascii_frame(1, &pdu);
        let (unit_id, parsed_pdu) = parse_ascii_frame(&frame).unwrap();
        assert_eq!(unit_id, 1);
        assert_eq!(parsed_pdu, pdu);
    }

    #[test]
    fn parse_ascii_frame_lower_case_hex_is_accepted() {
        // 小写 hex 也应该被接受
        let frame = b":01030000000af2\r\n";
        let (unit_id, pdu) = parse_ascii_frame(frame).unwrap();
        assert_eq!(unit_id, 1);
        assert_eq!(pdu, vec![0x03, 0x00, 0x00, 0x00, 0x0A]);
    }

    #[test]
    fn missing_start_byte_is_rejected() {
        let frame = b"01030000000AF2\r\n";
        assert!(matches!(
            parse_ascii_frame(frame),
            Err(RtuError::AsciiStartByteMissing)
        ));
    }

    #[test]
    fn missing_end_bytes_are_rejected() {
        let frame = b":01030000000AF2";
        assert!(matches!(
            parse_ascii_frame(frame),
            Err(RtuError::AsciiEndBytesMissing)
        ));
    }

    #[test]
    fn corrupt_lrc_is_rejected() {
        // 把 LRC 从 F2 改成 F3
        let frame = b":01030000000AF3\r\n";
        assert!(matches!(
            parse_ascii_frame(frame),
            Err(RtuError::LrcMismatch { .. })
        ));
    }

    #[test]
    fn invalid_hex_is_rejected() {
        let frame = b":0X030000000AF2\r\n";
        assert!(matches!(
            parse_ascii_frame(frame),
            Err(RtuError::AsciiHexDecodeFailed { .. })
        ));
    }

    #[test]
    fn fc06_write_single_register_round_trips() {
        let pdu = [0x06, 0x00, 0x05, 0x12, 0x34];
        let frame = build_ascii_frame(1, &pdu);
        let (unit_id, parsed_pdu) = parse_ascii_frame(&frame).unwrap();
        assert_eq!(unit_id, 1);
        assert_eq!(parsed_pdu, pdu);
    }
}
