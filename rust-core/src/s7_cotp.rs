//! 西门子 S7comm 传输层:TPKT(RFC 1006) + COTP(ISO 8073)。
//!
//! 规范来源:《西门子全协议设计文档.md》§3.1/§3.2 + deep-dive §2
//! (snap7 源码 + ISO 8073 + python-snap7 3.0 connection.py 交叉印证)。
//!
//! CR(22 字节模板,snap7 实际发出帧):
//! `03 00 00 16 11 E0 00 00 01 00 00 C0 01 0A C1 02 <local> C2 02 <remote>`
//! - SrcRef=0x0100(RFC0983 要求 0,但 S7 设备要求非 0)
//! - Class/Option=0x00(RFC 说 0x40,S7 要求 0x00)
//! - TPDU-SIZE 指数编码:值 v → 2^v 字节(0x0A=1024)
//! DT(数据阶段):`02 F0 80` + S7 PDU

use std::io::{Read, Write};

use crate::error::CoreError;

/// TPKT 版本
pub const TPKT_VERSION: u8 = 0x03;
/// COTP TPDU code:连接请求
pub const COTP_CR: u8 = 0xE0;
/// COTP TPDU code:连接确认
pub const COTP_CC: u8 = 0xD0;
/// COTP TPDU code:断开请求
pub const COTP_DR: u8 = 0x80;
/// COTP TPDU code:数据传输
pub const COTP_DT: u8 = 0xF0;

/// COTP 参数 code(ISO 8073)
pub const PARAM_TPDU_SIZE: u8 = 0xC0;
pub const PARAM_CALLING_TSAP: u8 = 0xC1;
pub const PARAM_CALLED_TSAP: u8 = 0xC2;

/// snap7 默认:SrcRef 非零(RFC0983 说应为 0,S7 设备要求非 0)
pub const DEFAULT_SRC_REF: u16 = 0x0100;

/// TPDU-SIZE 指数(2^v 字节)。snap7 默认 0x0A=1024。
pub fn tpdu_size_code(bytes: u32) -> Option<u8> {
    match bytes {
        128 => Some(0x07),
        256 => Some(0x08),
        512 => Some(0x09),
        1024 => Some(0x0A),
        2048 => Some(0x0B),
        4096 => Some(0x0C),
        8192 => Some(0x0D),
        _ => None,
    }
}

/// 指数 → 字节。
pub fn tpdu_size_from_code(code: u8) -> Option<u32> {
    if (7..=13).contains(&code) {
        Some(1u32 << code)
    } else {
        None
    }
}

/// TSAP 连接类型(§6.2,2026-08 修正映射)。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConnectionType {
    /// 0x01,编程器(snap7 默认,权限最高)
    Pg,
    /// 0x02,操作面板/HMI
    Op,
    /// 0x03,数据交换(S7 Basic)
    Basic,
}

impl ConnectionType {
    pub fn code(self) -> u8 {
        match self {
            ConnectionType::Pg => 0x01,
            ConnectionType::Op => 0x02,
            ConnectionType::Basic => 0x03,
        }
    }
}

/// snap7 公式(deep-dive §2.2 源码级实证):
/// `RemoteTSAP = (ConnectionType<<8) + Rack*0x20 + Slot`
pub fn remote_tsap(conn_type: ConnectionType, rack: u8, slot: u8) -> u16 {
    ((conn_type.code() as u16) << 8) | ((rack as u16) * 0x20) | (slot as u16)
}

/// 构建完整 COTP 连接请求帧(TPKT + CR)。
///
/// 参数顺序对齐 snap7 C 版(C0/C1/C2);python-snap7 用 C1/C2/C0,TLV 语义等价。
pub fn build_cr(local_tsap: u16, remote_tsap: u16, tpdu_size_bytes: u32) -> Vec<u8> {
    let code = tpdu_size_code(tpdu_size_bytes).unwrap_or(0x0A);
    // COTP 头 7B + 参数 3+4+4 = 18 → LI = 17(0x11)
    let mut buf = Vec::with_capacity(22);
    buf.extend_from_slice(&[TPKT_VERSION, 0x00, 0x00, 0x16]); // TPKT(长度回填)
    buf.push(0x11); // Length Indicator
    buf.push(COTP_CR); // TPDU code
    buf.extend_from_slice(&0x0000u16.to_be_bytes()); // DST-REF
    buf.extend_from_slice(&DEFAULT_SRC_REF.to_be_bytes()); // SRC-REF
    buf.push(0x00); // Class 0(S7 要求 0x00)
    buf.extend_from_slice(&[PARAM_TPDU_SIZE, 0x01, code]);
    buf.extend_from_slice(&[PARAM_CALLING_TSAP, 0x02]);
    buf.extend_from_slice(&local_tsap.to_be_bytes());
    buf.extend_from_slice(&[PARAM_CALLED_TSAP, 0x02]);
    buf.extend_from_slice(&remote_tsap.to_be_bytes());
    debug_assert_eq!(buf.len(), 22);
    buf
}

/// COTP 连接确认解析结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CcInfo {
    pub dst_ref: u16,
    pub src_ref: u16,
    /// 协商后的 TPDU size(字节;未携带参数时为 None)
    pub tpdu_size: Option<u32>,
}

/// 解析 COTP 连接确认(不含 TPKT 头,即 COTP 部分开始)。
///
/// CC 与 CR 布局相同;variable part 以 TLV 扫描(容忍任意参数顺序/未知参数,
/// python-snap7 3.0 `_parse_cotp_parameters` 同款宽容策略)。
pub fn parse_cc(cotp: &[u8]) -> Result<CcInfo, CoreError> {
    let err = |msg: &str| CoreError::Modbus {
        code: "S7_COTP_INVALID",
        message: msg.to_string(),
        details: None,
    };
    if cotp.len() < 7 {
        return Err(err("COTP CC 帧过短"));
    }
    let li = cotp[0] as usize;
    let pdu_type = cotp[1] & 0xF0;
    if pdu_type != COTP_CC {
        // 0x80=DR:CPU 拒绝连接(rack/slot/TSAP 错误或连接资源耗尽)
        if pdu_type == COTP_DR {
            return Err(CoreError::Modbus {
                code: "S7_COTP_REFUSED",
                message: "CPU 拒绝了 S7 连接(COTP Disconnect Request)。检查 rack/slot:S7-1200/1500 用 0/1,S7-300 用 0/2,S7-400 用 0/3,S7-200 SMART 用 0/0;个别 CPU 只接受特定连接类型(PG/OP/Basic)".to_string(),
                details: Some(serde_json::json!({ "tpdu": format!("0x{pdu_type:02X}") })),
            });
        }
        return Err(err(&format!("期望 COTP CC(0xD0),实际 0x{pdu_type:02X}")));
    }
    let dst_ref = u16::from_be_bytes([cotp[2], cotp[3]]);
    let src_ref = u16::from_be_bytes([cotp[4], cotp[5]]);
    let end = (li + 1).min(cotp.len());

    let mut tpdu_size = None;
    let mut off = 7;
    while off + 2 <= end {
        let pcode = cotp[off];
        let plen = cotp[off + 1] as usize;
        if off + 2 + plen > end {
            break;
        }
        if pcode == PARAM_TPDU_SIZE {
            tpdu_size = if plen == 1 {
                tpdu_size_from_code(cotp[off + 2])
            } else if plen == 2 {
                let raw = u16::from_be_bytes([cotp[off + 2], cotp[off + 3]]);
                (128..=8192).contains(&raw).then_some(raw as u32)
            } else {
                None
            };
        }
        off += 2 + plen;
    }
    Ok(CcInfo { dst_ref, src_ref, tpdu_size })
}

/// COTP DT 固定头(S7 数据阶段每帧 3 字节)。
pub const DT_HEADER: [u8; 3] = [0x02, COTP_DT, 0x80];

/// 包装 S7 PDU 为完整 TPKT+COTP DT 帧。
pub fn wrap_dt(s7_pdu: &[u8]) -> Vec<u8> {
    let total = (4 + 3 + s7_pdu.len()) as u16;
    let mut buf = Vec::with_capacity(total as usize);
    buf.extend_from_slice(&[TPKT_VERSION, 0x00]);
    buf.extend_from_slice(&total.to_be_bytes());
    buf.extend_from_slice(&DT_HEADER);
    buf.extend_from_slice(s7_pdu);
    buf
}

/// 从流中读取一个完整 TPKT 帧(阻塞,按长度字段定界)。
///
/// 返回**完整帧**(含 4 字节 TPKT 头),便于报文面板原样展示。
pub fn read_tpkt_frame<R: Read>(reader: &mut R) -> Result<Vec<u8>, CoreError> {
    let err = |code: &'static str, msg: String| CoreError::Modbus { code, message: msg, details: None };
    let mut head = [0u8; 4];
    reader.read_exact(&mut head).map_err(|e| err("S7_READ_FAILED", format!("读取 TPKT 头失败:{e}")))?;
    if head[0] != TPKT_VERSION {
        return Err(err("S7_TPKT_INVALID", format!("TPKT 版本应为 0x03,实际 0x{:02X}", head[0])));
    }
    let total = u16::from_be_bytes([head[2], head[3]]) as usize;
    if total < 7 || total > 8192 + 12 {
        return Err(err("S7_TPKT_INVALID", format!("TPKT 长度不合法:{total}")));
    }
    let mut frame = head.to_vec();
    frame.resize(total, 0);
    reader
        .read_exact(&mut frame[4..])
        .map_err(|e| err("S7_READ_FAILED", format!("读取 TPKT 净荷失败:{e}")))?;
    Ok(frame)
}

/// 校验完整 TPKT 帧并剥出 COTP 部分。
pub fn unwrap_tpkt(frame: &[u8]) -> Result<&[u8], CoreError> {
    let err = |msg: String| CoreError::Modbus {
        code: "S7_TPKT_INVALID",
        message: msg,
        details: None,
    };
    if frame.len() < 7 {
        return Err(err("TPKT 帧过短".into()));
    }
    if frame[0] != TPKT_VERSION {
        return Err(err(format!("TPKT 版本应为 0x03,实际 0x{:02X}", frame[0])));
    }
    let total = u16::from_be_bytes([frame[2], frame[3]]) as usize;
    if total != frame.len() {
        return Err(err(format!("TPKT 长度字段 {total} 与实际帧长 {} 不符", frame.len())));
    }
    Ok(&frame[4..])
}

/// 校验 COTP DT 并剥出 S7 PDU。
pub fn unwrap_dt(cotp: &[u8]) -> Result<&[u8], CoreError> {
    let err = |msg: String| CoreError::Modbus {
        code: "S7_COTP_INVALID",
        message: msg,
        details: None,
    };
    if cotp.len() < 3 || cotp[0] != 0x02 || (cotp[1] & 0xF0) != COTP_DT {
        return Err(err(format!("期望 COTP DT(02 F0 80),实际 {:02X?}", &cotp[..cotp.len().min(3)])));
    }
    if cotp[2] & 0x80 == 0 {
        return Err(err("COTP DT 缺少 EOT 标记(多片 TSDU 不支持)".to_string()));
    }
    Ok(&cotp[3..])
}

/// 便捷:完整帧 → S7 PDU。
pub fn frame_to_pdu(frame: &[u8]) -> Result<&[u8], CoreError> {
    unwrap_dt(unwrap_tpkt(frame)?)
}

/// 写帧辅助(带失败包装)。
pub fn write_frame<W: Write>(writer: &mut W, frame: &[u8]) -> Result<(), CoreError> {
    writer
        .write_all(frame)
        .map_err(|e| CoreError::Modbus {
            code: "S7_WRITE_FAILED",
            message: format!("发送 S7 帧失败:{e}"),
            details: None,
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// snap7 实际发出的 CR(deep-dive §2.1,PG/rack0/slot2,逐字节 golden)
    const CR_PG_R0S2: [u8; 22] = [
        0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00, 0x01, 0x00, 0x00, 0xC0, 0x01, 0x0A, 0xC1,
        0x02, 0x01, 0x00, 0xC2, 0x02, 0x01, 0x02,
    ];

    #[test]
    fn builds_snap7_cr_byte_exact() {
        let frame = build_cr(0x0100, remote_tsap(ConnectionType::Pg, 0, 2), 1024);
        assert_eq!(frame, CR_PG_R0S2.to_vec());
    }

    #[test]
    fn remote_tsap_formula() {
        // §6.2/§6.3:PG rack0 slot2 → 0x0102
        assert_eq!(remote_tsap(ConnectionType::Pg, 0, 2), 0x0102);
        // Basic rack0 slot1 → 0x0301(S7netplus SMART/1200 分支)
        assert_eq!(remote_tsap(ConnectionType::Basic, 0, 1), 0x0301);
        // rack1 slot3 → 0x0123(rack*0x20=0x20)
        assert_eq!(remote_tsap(ConnectionType::Pg, 1, 3), 0x0123);
        // OP rack0 slot1 → 0x0201
        assert_eq!(remote_tsap(ConnectionType::Op, 0, 1), 0x0201);
    }

    #[test]
    fn tpdu_size_roundtrip() {
        for bytes in [128u32, 256, 512, 1024, 2048, 4096, 8192] {
            let code = tpdu_size_code(bytes).unwrap();
            assert_eq!(tpdu_size_from_code(code), Some(bytes));
        }
        assert_eq!(tpdu_size_code(1000), None);
        assert_eq!(tpdu_size_from_code(0x0A), Some(1024));
    }

    #[test]
    fn parses_cc_with_snap7_layout() {
        // deep-dive §2.3 推导的典型 CC:DST-REF=CR 的 SRC-REF
        let cc_cotp = [
            0x11u8, COTP_CC, 0x01, 0x00, 0x00, 0x07, 0x00, PARAM_TPDU_SIZE, 0x01, 0x0A,
            PARAM_CALLING_TSAP, 0x02, 0x01, 0x02, PARAM_CALLED_TSAP, 0x02, 0x01, 0x00,
        ];
        let info = parse_cc(&cc_cotp).unwrap();
        assert_eq!(info.dst_ref, 0x0100);
        assert_eq!(info.tpdu_size, Some(1024));
    }

    #[test]
    fn parses_cc_with_python_snap7_param_order_and_raw_size() {
        // python-snap7 顺序 C1/C2/C0 + 2 字节原始 TPDU size
        let cc_cotp = [
            0x12u8, COTP_CC, 0x00, 0x01, 0x0D, 0x00, 0x00, PARAM_CALLING_TSAP, 0x02, 0x03, 0x01,
            PARAM_CALLED_TSAP, 0x02, 0x01, 0x02, PARAM_TPDU_SIZE, 0x02, 0x04, 0x00,
        ];
        let info = parse_cc(&cc_cotp).unwrap();
        assert_eq!(info.tpdu_size, Some(1024));
        assert_eq!(info.src_ref, 0x0D00);
    }

    #[test]
    fn cc_tolerates_unknown_params() {
        let cc_cotp = [
            0x0Du8, COTP_CC, 0x01, 0x00, 0x00, 0x01, 0x00, 0xC6, 0x02, 0x00, 0x01, PARAM_TPDU_SIZE,
            0x01, 0x0A,
        ];
        let info = parse_cc(&cc_cotp).unwrap();
        assert_eq!(info.tpdu_size, Some(1024));
    }

    #[test]
    fn dr_means_refused() {
        let dr = [0x02u8, COTP_DR, 0x00, 0x00, 0x00, 0x00, 0x00];
        let e = parse_cc(&dr).unwrap_err();
        match e {
            CoreError::Modbus { code, message, .. } => {
                assert_eq!(code, "S7_COTP_REFUSED");
                assert!(message.contains("rack/slot"));
            }
            _ => panic!("错误类型"),
        }
    }

    #[test]
    fn rejects_non_cc() {
        let bad = [0x02u8, COTP_DT, 0x00, 0x00, 0x00, 0x00, 0x00];
        assert!(parse_cc(&bad).is_err());
    }

    #[test]
    fn wraps_and_unwraps_dt() {
        let pdu = [0x32u8, 0x01, 0x00, 0x00];
        let frame = wrap_dt(&pdu);
        assert_eq!(&frame[..4], &[0x03, 0x00, 0x00, 0x0B]); // 4+3+4=11
        assert_eq!(&frame[4..7], &[0x02, 0xF0, 0x80]);
        assert_eq!(frame_to_pdu(&frame).unwrap(), &pdu[..]);
    }

    #[test]
    fn read_tpkt_frame_handles_partial_reads() {
        use std::io::Cursor;
        let frame1 = wrap_dt(&[0x32, 0x01]);
        let frame2 = wrap_dt(&[0x32, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00]);
        let mut stream = Cursor::new([frame1.clone(), frame2.clone()].concat());
        assert_eq!(read_tpkt_frame(&mut stream).unwrap(), frame1);
        assert_eq!(read_tpkt_frame(&mut stream).unwrap(), frame2);
    }

    #[test]
    fn rejects_wrong_version_and_length() {
        let mut bad_version = wrap_dt(&[0x32]);
        bad_version[0] = 0x02;
        assert!(frame_to_pdu(&bad_version).is_err());

        let mut bad_len = wrap_dt(&[0x32, 0x01, 0x02, 0x03]);
        bad_len[3] = 0xFF; // 长度字段与实际不符
        assert!(unwrap_tpkt(&bad_len).is_err());
    }
}
