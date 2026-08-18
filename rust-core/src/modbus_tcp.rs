//! Modbus TCP MBAP 帧(Modbus Application Protocol Header)。
//!
//! MBAP 帧结构(7 字节头 + PDU,无 CRC):
//! ```text
//! ┌────────────┬────────────┬────────┬─────────┬───────┐
//! │ Transaction│ Protocol   │ Length │ Unit ID │  PDU  │
//! │  ID (2B)   │  ID (2B=0) │ (2B)   │  (1B)   │ (N B) │
//! └────────────┴────────────┴────────┴─────────┴───────┘
//! ```
//! `Length` = 后续字节数(Unit ID 1B + PDU N B)。

use std::sync::atomic::{AtomicU16, Ordering};

use crate::modbus_rtu::RtuError;

pub const MBAP_HEADER_LEN: usize = 7;
pub const MODBUS_PROTOCOL_ID: u16 = 0;

/// MBAP 头。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MbapHeader {
    pub transaction_id: u16,
    pub protocol_id: u16,
    pub length: u16,
    pub unit_id: u8,
}

/// 构建 MBAP 帧(头 + PDU)。
pub fn build_mbap_frame(transaction_id: u16, unit_id: u8, pdu: &[u8]) -> Vec<u8> {
    let pdu_len = u16::try_from(pdu.len()).unwrap_or(u16::MAX);
    let length = 1 + pdu_len; // unit_id(1) + pdu
    let mut frame = Vec::with_capacity(MBAP_HEADER_LEN + pdu.len());
    frame.extend_from_slice(&transaction_id.to_be_bytes());
    frame.extend_from_slice(&MODBUS_PROTOCOL_ID.to_be_bytes());
    frame.extend_from_slice(&length.to_be_bytes());
    frame.push(unit_id);
    frame.extend_from_slice(pdu);
    frame
}

/// 解析 MBAP 帧,返回(头, PDU)。
pub fn parse_mbap_frame(bytes: &[u8]) -> Result<(MbapHeader, Vec<u8>), RtuError> {
    if bytes.len() < MBAP_HEADER_LEN + 1 {
        return Err(RtuError::MbapFrameTooShort { len: bytes.len() });
    }
    let transaction_id = u16::from_be_bytes([bytes[0], bytes[1]]);
    let protocol_id = u16::from_be_bytes([bytes[2], bytes[3]]);
    let length = u16::from_be_bytes([bytes[4], bytes[5]]);
    let unit_id = bytes[6];

    if protocol_id != MODBUS_PROTOCOL_ID {
        return Err(RtuError::MbapProtocolMismatch {
            received: protocol_id,
        });
    }

    let declared_payload_len = usize::from(length); // unit_id + pdu
    let actual_payload_len = bytes.len() - 6; // after the 6-byte header prefix (excl. unit_id byte)

    if declared_payload_len != actual_payload_len {
        return Err(RtuError::MbapLengthMismatch {
            expected: declared_payload_len,
            received: actual_payload_len,
        });
    }

    let pdu = bytes[MBAP_HEADER_LEN..].to_vec();
    Ok((
        MbapHeader {
            transaction_id,
            protocol_id,
            length,
            unit_id,
        },
        pdu,
    ))
}

/// 原子事务 ID 生成器(线程安全,自动递增,回绕到 0)。
pub struct TransactionIdGenerator {
    counter: AtomicU16,
}

impl TransactionIdGenerator {
    pub const fn new() -> Self {
        Self {
            counter: AtomicU16::new(0),
        }
    }

    pub fn next(&self) -> u16 {
        // fetch_add 回绕:u16 溢出自动回绕到 0
        self.counter.fetch_add(1, Ordering::Relaxed)
    }
}

impl Default for TransactionIdGenerator {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn build_and_parse_mbap_round_trips() {
        let pdu = vec![0x03, 0x00, 0x00, 0x00, 0x0A];
        let frame = build_mbap_frame(0x1234, 1, &pdu);
        assert_eq!(frame.len(), MBAP_HEADER_LEN + 5);
        let (header, parsed_pdu) = parse_mbap_frame(&frame).unwrap();
        assert_eq!(header.transaction_id, 0x1234);
        assert_eq!(header.protocol_id, 0);
        assert_eq!(header.length, 6); // unit_id(1) + pdu(5)
        assert_eq!(header.unit_id, 1);
        assert_eq!(parsed_pdu, pdu);
    }

    #[test]
    fn mbap_header_bytes_are_big_endian() {
        let frame = build_mbap_frame(0xABCD, 5, &[0x06]);
        // TID=AB CD, PID=00 00, Length=00 02, Unit=05, PDU=06
        assert_eq!(frame, vec![0xAB, 0xCD, 0x00, 0x00, 0x00, 0x02, 0x05, 0x06]);
    }

    #[test]
    fn too_short_frame_is_rejected() {
        assert!(matches!(
            parse_mbap_frame(&[0, 0, 0, 0, 0, 0]),
            Err(RtuError::MbapFrameTooShort { .. })
        ));
    }

    #[test]
    fn protocol_mismatch_is_rejected() {
        // protocol_id = 1(非 0)
        let frame = [0x00, 0x01, 0x00, 0x01, 0x00, 0x02, 0x01, 0x03];
        assert!(matches!(
            parse_mbap_frame(&frame),
            Err(RtuError::MbapProtocolMismatch { received: 1 })
        ));
    }

    #[test]
    fn length_mismatch_is_rejected() {
        // 声明 length=10,但实际只有 2 字节 payload
        let frame = [0x00, 0x01, 0x00, 0x00, 0x00, 0x0A, 0x01, 0x03];
        assert!(matches!(
            parse_mbap_frame(&frame),
            Err(RtuError::MbapLengthMismatch { .. })
        ));
    }

    #[test]
    fn transaction_id_generator_increments_and_wraps() {
        let generator = TransactionIdGenerator::new();
        assert_eq!(generator.next(), 0);
        assert_eq!(generator.next(), 1);
        assert_eq!(generator.next(), 2);
    }
}
