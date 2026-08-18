//! 西门子 USS(Universal Serial Interface)变频器协议。
//!
//! 完全公开规范(SINAMICS 功能手册 / MicroMaster 操作手册)。
//! 帧结构:`STX(0x02) + LGE + ADR + PKE(2B) + IND(2B) + PZD(2N B) + BCC`
//! - STX: 起始 0x02(应答帧 0x02 或 0x03 短帧)
//! - LGE: 从 ADR 到 BCC 前的字节数(含自身之后到 BCC 前)
//! - ADR: 从站地址(0-30) + 控制位(bit7=1 表示镜像/应答)
//! - PKE: 参数标识(高字节=AK 参数号类型,低字节=参数号 PNU)
//! - IND: 子参数索引(如 P2200[1] 的 [1])
//! - PZD: 过程数据(控制字 STW + 主给定 HSW / 状态字 ZSW + 实际值 HIW)
//! - BCC: XOR 校验(STX 到 PZD 末尾)
//!
//! 串口:RS-485 主从,默认 9600-115200 8N1(非 8E1!)。
//!
//! 常用 PZD 长度: 2(标准) / 4(扩展) / 8(完整)。

use crate::error::CoreError;

pub const STX: u8 = 0x02;
pub const MIN_FRAME_LEN: usize = 4 + 4 + 2; // STX + LGE + ADR + PKE(2) + IND(2) + PZD(0) + BCC

fn uss_err(msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code: "USS_INVALID", message: msg.into(), details: None }
}

/// BCC:从 STX 到最后一个 PZD 字节的 XOR。
pub fn bcc(data: &[u8]) -> u8 {
    data.iter().fold(0u8, |a, b| a ^ b)
}

/// 构建 USS 请求帧(主机 → 变频器)。
///
/// `station`: 从站地址 0-30
/// `pke`: 参数标识 2 字节(如读 P700: 0x0B_BC;写 P1000=1: 0x2E_28)
/// `ind`: 子参数索引 2 字节(0x0000 = 无子索引)
/// `pzd`: 过程数据(控制字+给定,每字 2B 大端)
pub fn build_uss_request(station: u8, pke: [u8; 2], ind: [u8; 2], pzd: &[u8]) -> Vec<u8> {
    let lge = (1 + 2 + 2 + pzd.len()) as u8; // ADR + PKE + IND + PZD
    let mut f = Vec::with_capacity(3 + lge as usize + 1);
    f.push(STX);
    f.push(lge);
    f.push(station & 0x7F); // bit7=0 表示请求
    f.extend_from_slice(&pke);
    f.extend_from_slice(&ind);
    f.extend_from_slice(pzd);
    let checksum = bcc(&f);
    f.push(checksum);
    f
}

/// 解析 USS 应答帧(变频器 → 主机)。
///
/// 返回 (station, pke, ind, pzd)。
pub fn parse_uss_response(frame: &[u8]) -> Result<(u8, [u8; 2], [u8; 2], Vec<u8>), CoreError> {
    if frame.len() < 8 {
        return Err(uss_err(format!("帧过短({}B,需 ≥8)", frame.len())));
    }
    if frame[0] != STX && frame[0] != 0x03 {
        return Err(uss_err(format!("起始字节 0x{:02X}(期望 0x02/0x03)", frame[0])));
    }
    let lge = frame[1] as usize;
    let expected = 2 + lge + 1; // STX + LGE + (ADR..PZD=lge 字节) + BCC
    if frame.len() != expected {
        return Err(uss_err(format!("帧长不匹配:LGE={lge} 声明总长 {expected},实际 {}", frame.len())));
    }
    let checksum = frame[frame.len() - 1];
    let calc = bcc(&frame[..frame.len() - 1]);
    if checksum != calc {
        return Err(uss_err(format!("BCC 校验失败:帧内 0x{checksum:02X},计算 0x{calc:02X}")));
    }
    let station = frame[2] & 0x7F;
    let pke = [frame[3], frame[4]];
    let ind = [frame[5], frame[6]];
    let pzd = frame[7..frame.len() - 1].to_vec();
    Ok((station, pke, ind, pzd))
}

// ============ 常用参数 PKE 编码 ============

/// 读参数 Pxxxx 的 PKE( AK=1 Read Request )。
pub fn pke_read(param: u16) -> [u8; 2] {
    [(0x01 << 4) | ((param >> 8) as u8 & 0x0F), (param & 0xFF) as u8]
}

/// 写参数 Pxxxx = value 的 PKE( AK=2 Write Request,16 位值放 IND )。
pub fn pke_write_16(param: u16, value: u16) -> ([u8; 2], [u8; 2]) {
    let pke = [(0x02 << 4) | ((param >> 8) as u8 & 0x0F), (param & 0xFF) as u8];
    let ind = value.to_be_bytes();
    (pke, ind)
}

/// AK(Aktion/Response)码 → 人话。
pub fn ak_message(ak: u8) -> &'static str {
    match ak {
        0x00 => "无任务",
        0x01 => "读请求",
        0x02 => "写请求(16位)",
        0x03 => "写请求(32位,分两半)",
        0x04 => "任务拒绝(参数值非法)",
        0x05 => "写请求(32位,整体)",
        0x07 => "任务拒绝(无权限)",
        0x08 => "写请求(双字)",
        0x0B => "读参数值(完整列表)",
        0x0C => "读参数值(部分列表)",
        0x0D => "读参数值(默认值)",
        0x0F => "读参数值(文本)",
        0x10 => "应答:参数值(16位)",
        0x11 => "应答:参数值(32位,低半)",
        0x12 => "应答:参数值(32位,高半)",
        0x13 => "应答:写不执行(下标超限)",
        0x14 => "应答:写不执行(值超范围)",
        0x15 => "应答:参数值(32位,整体)",
        0x17 => "应答:写不执行(无权限)",
        0x18 => "应答:参数值(双字)",
        _ => "未知 AK",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 帧结构自洽测试(公开手册帧格式)
    #[test]
    fn build_and_parse_roundtrip() {
        // 读 P0700(命令源),站 1,2 字 PZD(控制字 0x047E + 给定 0x0000)
        let req = build_uss_request(1, pke_read(700), [0, 0], &[0x04, 0x7E, 0x00, 0x00]);
        assert_eq!(req[0], STX);
        assert_eq!(req[1], 1 + 2 + 2 + 4); // LGE=9
        assert_eq!(req[2], 1); // 站 1
        // 解析应答:站 1,PKE=0x1B_BC(AK=1 应答=0x10? 用 0x10 表示 16 位参数值)
        let resp = build_uss_request(1, [0x1B, 0xBC], [0, 0], &[0x00, 0x06, 0x00, 0x00]);
        let (st, pke, ind, pzd) = parse_uss_response(&resp).unwrap();
        assert_eq!(st, 1);
        assert_eq!(pke[1], 0xBC); // PNU=700 低字节
        assert_eq!(pzd, vec![0x00, 0x06, 0x00, 0x00]);
    }

    #[test]
    fn bcc_xor_property() {
        // XOR 校验:任意单字节翻转 → BCC 必变
        let data = [0x02, 0x09, 0x01, 0x0B, 0xBC, 0x00, 0x00, 0x04, 0x7E];
        let original = bcc(&data);
        for i in 0..data.len() {
            let mut corrupted = data;
            corrupted[i] ^= 0x01;
            assert_ne!(bcc(&corrupted), original, "翻转 byte {i} 应改变 BCC");
        }
    }

    #[test]
    fn pke_encoding() {
        // 读 P700: AK=1, PNU=700=0x02BC → PKE = 0x12_BC? 不对——
        // PNU 是 12 位,PKE 高 4 位 = AK。
        // P700 = 0x2BC → PKE = (AK<<12)|PNU = 0x12BC → 字节 [0x12, 0xBC]
        let pk = pke_read(700);
        assert_eq!(pk[0] >> 4, 0x01); // AK=1(读请求)
        assert_eq!(((pk[0] as u16 & 0x0F) << 8) | pk[1] as u16, 700);
    }

    #[test]
    fn lge_and_length_validation() {
        let req = build_uss_request(0, pke_read(1000), [0, 0], &[0, 0, 0, 0]);
        // STX + LGE + ADR + PKE(2) + IND(2) + PZD(4) + BCC = 12B(LGE=9)
        assert_eq!(req.len(), 12);
        // 篡改 LGE → 解析报错
        let mut bad = req.clone();
        bad[1] = 0xFF;
        assert!(parse_uss_response(&bad).is_err());
    }
}
