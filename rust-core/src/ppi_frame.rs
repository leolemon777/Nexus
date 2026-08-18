//! 西门子 PPI 协议帧层(经典 S7-200 / SMART RS485;本模块亦承载 PPIOverTcp 透传)。
//!
//! golden 字节(grok 交叉调研,与官方手册一致):
//! - SD2 长帧:`68 LE LEr 68 DA SA FC <S7 PDU> FCS 16`
//!   - LE=LEr= DA..PDU 的字节数(2+1+pdu.len());FC=6C(读)/7C(写)
//!   - FCS = DA..PDU 的**算术和**低 8 位(非 XOR!)
//!   - 读 VB100×3(站2 主0):`68 1B 1B 68 02 00 6C <32 01 … 84 00 03 20> 8D 16`
//! - 单字节确认 SC = `E5`(仅表示收到,无数据)
//! - 二次确认短帧 SA:`10 DA SA 5C FCS 16`(FCS=DA+SA+5C);站2 主0 → `10 02 00 5C 5E 16`
//! - 双拍时序:请求 → E5 → SA 短帧 → SD2 数据长帧(少第③步会一直停在 E5)
//! - 内嵌 S7 PDU 就是标准 S7comm Job(pdu_ref=0),V 区 = DB1 + Area 0x84
//! - 传输默认 9.6k 8E1(187.5k 需 USB-PPI 电缆)

use crate::error::CoreError;

pub const FC_READ: u8 = 0x6C;
pub const FC_WRITE: u8 = 0x7C;
pub const SC_E5: u8 = 0xE5;

fn ppi_err(msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code: "S7_PPI_INVALID", message: msg.into(), details: None }
}

/// SD2 长帧计算术和校验(DA..PDU)。
pub fn fcs_sum(bytes: &[u8]) -> u8 {
    bytes.iter().fold(0u8, |a, b| a.wrapping_add(*b))
}

/// 构造 SD2 长帧(读/写共用;内嵌 S7 PDU)。
pub fn build_sd2(da: u8, sa: u8, fc: u8, s7_pdu: &[u8]) -> Vec<u8> {
    let body_len = 2 + 1 + s7_pdu.len(); // DA SA FC + PDU
    let mut f = Vec::with_capacity(4 + body_len + 2);
    f.extend_from_slice(&[0x68, body_len as u8, body_len as u8, 0x68]);
    f.push(da);
    f.push(sa);
    f.push(fc);
    f.extend_from_slice(s7_pdu);
    let fcs = fcs_sum(&f[4..f.len()]);
    f.push(fcs);
    f.push(0x16);
    f
}

/// 二次确认短帧:`10 DA SA 5C FCS 16`。
pub fn build_sa_confirm(da: u8, sa: u8) -> Vec<u8> {
    let fcs = fcs_sum(&[da, sa, 0x5C]);
    vec![0x10, da, sa, 0x5C, fcs, 0x16]
}

/// 校验并剥开 SD2 响应长帧 → (DA, SA, FC, 内嵌 S7 PDU)。
pub fn parse_sd2(frame: &[u8]) -> Result<(u8, u8, u8, Vec<u8>), CoreError> {
    if frame.len() < 7 || frame[0] != 0x68 {
        return Err(ppi_err("不是 PPI SD2 长帧(起始 0x68 不符)"));
    }
    // 防御:LE=DA..PDU 字节数,最少需 3(DA+SA+FC);LE<3 → body 不足 3 字节,body[1] 越界
    if frame[1] < 3 {
        return Err(ppi_err(format!("LE={} 不合法(需 ≥3:DA+SA+FC)", frame[1])));
    }
    let le = frame[1] as usize;
    if frame.len() < 4 + le + 2 {
        return Err(ppi_err("PPI 帧长不足 LE 声明"));
    }
    let body = &frame[4..4 + le];
    let fcs = frame[4 + le];
    if frame[5 + le] != 0x16 {
        return Err(ppi_err("PPI 帧无结束符 0x16"));
    }
    if fcs_sum(body) != fcs {
        return Err(ppi_err(format!("FCS 校验失败(算术和):期望 0x{fcs:02X} 实得 0x{:02X}", fcs_sum(body))));
    }
    Ok((body[0], body[1], body[2], body[3..].to_vec()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::s7_pdu::{build_read_request, S7Item};

    /// golden:站 2 主 0 读 VB100 3 字节(grok 调研逐字节样例)
    #[test]
    fn read_frame_matches_golden() {
        let items = [S7Item::new("VB100", 3).unwrap()];
        let pdu = build_read_request(0, &items).unwrap();
        // 内嵌 PDU 与 golden 的 32 01 … 84 00 03 20 一致(V 区=DB1, 地址 100<<3=0x320, BYTE×3)
        assert_eq!(&pdu[..14], &[0x32, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x04, 0x01, 0x12, 0x0A]);
        let frame = build_sd2(2, 0, FC_READ, &pdu);
        assert_eq!(
            frame,
            vec![0x68, 0x1B, 0x1B, 0x68, 0x02, 0x00, 0x6C,
                 0x32, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x04, 0x01,
                 0x12, 0x0A, 0x10, 0x02, 0x00, 0x03, 0x00, 0x01, 0x84, 0x00, 0x03, 0x20,
                 0x8D, 0x16]
        );
    }

    /// golden:写 VB100 = 0x12(站 2 主 0)
    #[test]
    fn write_frame_matches_golden() {
        let items = [S7Item::new("VB100", 1).unwrap()];
        let pdu = crate::s7_pdu::build_write_request(0, &items, &[vec![0x12]]).unwrap();
        let frame = build_sd2(2, 0, FC_WRITE, &pdu);
        // golden 头:68 20 20 68 02 00 7C 32 01 …
        assert_eq!(&frame[..7], &[0x68, 0x20, 0x20, 0x68, 0x02, 0x00, 0x7C]);
        assert_eq!(frame.last(), Some(&0x16));
        // 数据项尾部:00(RC) 04(TS) 00 08(8bit) 12(数据) 16(ED)
        let n = frame.len();
        assert_eq!(&frame[n-7..], &[0x00, 0x04, 0x00, 0x08, 0x12, 0xBF, 0x16]); // 数据项+FCS(BF)+ED
    }

    #[test]
    fn sa_confirm_matches_golden() {
        assert_eq!(build_sa_confirm(2, 0), vec![0x10, 0x02, 0x00, 0x5C, 0x5E, 0x16]);
    }

    /// 响应回环:本地构造自洽 SD2 响应帧(数据 99 34 56)→ 剥壳 → S7 Ack → 数据
    /// (grok 样例的 LE/FCS 字节本身不自洽,故改为构造法;数据项字节与样例一致)
    #[test]
    fn parse_response_constructed() {
        let ack_pdu: Vec<u8> = vec![
            0x32, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x07, 0x00, 0x00,
            0x04, 0x01, 0xFF, 0x04, 0x00, 0x18, 0x99, 0x34, 0x56,
        ];
        let frame = build_sd2(0, 2, 0x08, &ack_pdu);
        let (da, sa, fc, pdu) = parse_sd2(&frame).unwrap();
        assert_eq!((da, sa, fc), (0, 2, 0x08));
        let ack = crate::s7_pdu::parse_ack(&pdu).unwrap();
        let items = crate::s7_pdu::parse_read_response(&ack).unwrap();
        assert_eq!(items[0].data, vec![0x99, 0x34, 0x56]);
    }

    #[test]
    fn fcs_rejects_corrupted() {
        let items = [S7Item::new("VB0", 1).unwrap()];
        let pdu = build_read_request(0, &items).unwrap();
        let mut f = build_sd2(2, 0, FC_READ, &pdu);
        let last = f.len() - 2;
        f[last] ^= 0xFF; // 破坏 FCS
        assert!(parse_sd2(&f).is_err());
    }
}
