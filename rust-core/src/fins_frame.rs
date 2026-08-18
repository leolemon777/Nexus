//! 欧姆龙 FINS 帧层:应用帧(ICF..SID + 服务)构造与解析、FINS/TCP 封装与握手。
//!
//! FINS/TCP(端口 9600):
//! - 帧头:魔数 "FINS"(4B) + 帧长(4B BE,含长度字段自身)
//! - 握手:client `FINS,0x0C,cmd=0x00000000,err=0x00000000,node(2B)`
//!         server `FINS,0x10,cmd=0x00000001,err=0x00000000,server_node(2B),client_node(2B)`
//! - 数据:cmd=0x00000002 + 应用帧(ICF RSV GCT(=2) DNA DA1 DA2 SNA SA1 SA2 SID + 服务)
//! FINS/UDP(端口 9600):裸应用帧,无 TCP 头与握手。
//!
//! 服务:0101 读 / 0102 写(参数:word-bit 标志 + 区代码 + 地址 3B + 点数 2B)。

use std::io::{Read, Write};

use crate::error::CoreError;
use crate::fins_address::FinsAddress;

pub const FINS_TCP_PORT: u16 = 9600;
/// ICF:响应要求=1(bit7) + 0x00
pub const ICF_RESPONSE_REQUIRED: u8 = 0x80;

fn err(code: &'static str, msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code, message: msg.into(), details: None }
}

/// FINS 端点节点号(直连以太网惯例:节点号 = IP 末段)。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FinsNodes {
    /// 目标(PLC)网络号(本地网络 0)
    pub dna: u8,
    /// 目标(PLC)节点号(通常 = PLC IP 末段;0 用于本地/仿真)
    pub da1: u8,
    /// 本源节点号
    pub sna: u8,
    pub sa1: u8,
}

impl Default for FinsNodes {
    fn default() -> Self {
        Self { dna: 0, da1: 0, sna: 0, sa1: 0 }
    }
}

// ============ 应用帧 ============

/// 构造 FINS 应用帧(9B 头 + 服务)。
fn build_app_frame(nodes: &FinsNodes, sid: u8, service: &[u8]) -> Vec<u8> {
    let mut f = Vec::with_capacity(10 + service.len());
    f.push(ICF_RESPONSE_REQUIRED);
    f.push(0x00); // RSV
    f.push(0x02); // GCT
    f.push(nodes.dna);
    f.push(nodes.da1);
    f.push(0x00); // DA2(单元 0)
    f.push(nodes.sna);
    f.push(nodes.sa1);
    f.push(0x00); // SA2
    f.push(sid);
    f.extend_from_slice(service);
    f
}

/// 读服务(0101)参数:word-bit + 区代码 + 地址 3B + 点数 2B。
pub fn build_read_service(addr: &FinsAddress, count: u16) -> Vec<u8> {
    let mut s = Vec::with_capacity(8);
    s.extend_from_slice(&[0x01, 0x01]);
    s.push(addr.word_bit_flag());
    s.push(addr.area_code);
    s.extend_from_slice(&addr.encode());
    s.extend_from_slice(&count.to_be_bytes());
    s
}

/// 写服务(0102)参数:读头 + 数据。
pub fn build_write_service(addr: &FinsAddress, count: u16, data: &[u8]) -> Vec<u8> {
    let mut s = build_read_service(addr, count);
    s[0] = 0x01;
    s[1] = 0x02;
    s.extend_from_slice(data);
    s
}

/// 完整读请求应用帧。
pub fn build_read_frame(nodes: &FinsNodes, sid: u8, addr: &FinsAddress, count: u16) -> Vec<u8> {
    build_app_frame(nodes, sid, &build_read_service(addr, count))
}

/// 完整写请求应用帧。
pub fn build_write_frame(nodes: &FinsNodes, sid: u8, addr: &FinsAddress, count: u16, data: &[u8]) -> Vec<u8> {
    build_app_frame(nodes, sid, &build_write_service(addr, count, data))
}

/// 响应应用帧解析结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FinsResponse {
    pub end_code: u16,
    /// 结束码后的数据
    pub data: Vec<u8>,
}

/// 解析响应应用帧:校验 ICF/GCT/地址回显,取服务结束码与数据。
pub fn parse_response_frame(frame: &[u8]) -> Result<FinsResponse, CoreError> {
    if frame.len() < 12 {
        return Err(err("FINS_RESPONSE_INVALID", format!("响应帧过短({}B,需 ≥12)", frame.len())));
    }
    let gct = frame[2];
    // 布局:ICF..SA2(9B) + SID(1) + 服务码(2) + 结束码(2) + 数据
    let end = u16::from_be_bytes([frame[12], frame[13]]);
    let data = frame[14..].to_vec();
    let _ = gct;
    Ok(FinsResponse { end_code: end, data })
}

/// FINS 结束码 → 人话(常见码)。
pub fn end_code_message(code: u16) -> &'static str {
    match code {
        0x0000 => "成功",
        0x0001 => "服务未执行(头部末尾异常)",
        0x0002 => "不接受服务(路由表/节点配置异常)",
        0x0003 => "不接受服务(控制器 busy)",
        0x0201 => "地址越界/区代码错误",
        0x0202 => "访问权限错误(写保护)",
        0x0203 => "越界(地址+点数超限)",
        0x0204 => "区代码/格式错误",
        _ => "未知结束码(查 W342 附录)",
    }
}

// ============ FINS/TCP 封装 ============

/// FINS/TCP 握手请求。
pub fn build_tcp_handshake(client_node: u16) -> Vec<u8> {
    let mut f = Vec::with_capacity(20);
    f.extend_from_slice(b"FINS");
    f.extend_from_slice(&12u32.to_be_bytes()); // 长度(不含 magic+len 8B)
    f.extend_from_slice(&0x00000000u32.to_be_bytes()); // command: connect
    f.extend_from_slice(&0x00000000u32.to_be_bytes()); // error
    f.extend_from_slice(&client_node.to_be_bytes());
    f.extend_from_slice(&[0x00, 0x00]); // 保留 2B(使 payload=12,与 length 字段一致)
    f
}

/// TCP 帧封装(cmd=0x00000002 SEND)。
pub fn wrap_tcp(app_frame: &[u8]) -> Vec<u8> {
    let mut f = Vec::with_capacity(8 + app_frame.len());
    f.extend_from_slice(b"FINS");
    f.extend_from_slice(&((8 + app_frame.len()) as u32).to_be_bytes());
    f.extend_from_slice(&0x00000002u32.to_be_bytes());
    f.extend_from_slice(&0x00000000u32.to_be_bytes()); // error(4B)
    f.extend_from_slice(app_frame);
    f
}

/// 从流读一个完整 FINS/TCP 帧(按 magic+length 定界),返回净应用帧。
pub fn read_tcp_frame<R: Read>(reader: &mut R) -> Result<Vec<u8>, CoreError> {
    let mut head = [0u8; 8];
    reader.read_exact(&mut head).map_err(|e| err("FINS_READ_FAILED", format!("读 FINS/TCP 头失败:{e}")))?;
    if &head[..4] != b"FINS" {
        return Err(err("FINS_TCP_INVALID", "不是 FINS/TCP 帧(魔数不符)"));
    }
    // length 字段 = 自身之后的净字节数(cmd+err+data);按它读满
    let len = u32::from_be_bytes([head[4], head[5], head[6], head[7]]) as usize;
    if len < 8 || len > 8192 + 16 {
        return Err(err("FINS_TCP_INVALID", format!("帧长不合法:{len}")));
    }
    let mut rest = vec![0u8; len];
    reader.read_exact(&mut rest).map_err(|e| err("FINS_READ_FAILED", format!("读 FINS/TCP 体失败:{e}")))?;
    Ok(rest)
}

/// 写帧辅助。
pub fn write_frame<W: Write>(writer: &mut W, frame: &[u8]) -> Result<(), CoreError> {
    writer.write_all(frame).map_err(|e| err("FINS_WRITE_FAILED", format!("发送 FINS 帧失败:{e}")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::fins_address::{area, parse_fins_address, FinsKind};

    #[test]
    fn read_frame_layout() {
        let addr = parse_fins_address("D100").unwrap();
        let f = build_read_frame(&FinsNodes::default(), 0x01, &addr, 2);
        // 头 10 + 服务 9(码2+flag1+区1+地址3+点数2)
        assert_eq!(f.len(), 19);
        assert_eq!(&f[..9], &[0x80, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        assert_eq!(f[9], 0x01); // SID
        assert_eq!(&f[10..12], &[0x01, 0x01]); // 读服务
        assert_eq!(f[12], 0x01); // word
        assert_eq!(f[13], area::DM_WORD);
        assert_eq!(&f[14..17], &[0x00, 0x00, 0x64]); // 100
        assert_eq!(&f[17..19.min(f.len())], &[0x00, 0x02]);
    }

    #[test]
    fn bit_read_uses_bit_flag_and_linear_addr() {
        let addr = parse_fins_address("CIO10.00").unwrap();
        let s = build_read_service(&addr, 1);
        assert_eq!(s[0..2], [0x01, 0x01]);
        assert_eq!(s[2], 0x00); // bit
        assert_eq!(s[3], area::CIO_BIT);
        assert_eq!(&s[4..7], &[0x00, 0x00, 0xA0]); // 160
    }

    #[test]
    fn write_frame_appends_data() {
        let addr = parse_fins_address("W0").unwrap();
        let f = build_write_frame(&FinsNodes::default(), 0x02, &addr, 2, &[0x12, 0x34, 0x56, 0x78]);
        assert_eq!(&f[10..12], &[0x01, 0x02]);
        assert_eq!(f[13], area::W_WORD);
        assert_eq!(&f[f.len() - 4..], &[0x12, 0x34, 0x56, 0x78]);
    }

    #[test]
    fn tcp_wrap_and_read_roundtrip() {
        let addr = parse_fins_address("D0").unwrap();
        let app = build_read_frame(&FinsNodes::default(), 0x01, &addr, 1);
        let tcp = wrap_tcp(&app);
        assert_eq!(&tcp[..4], b"FINS");
        assert_eq!(&tcp[8..12], &[0x00, 0x00, 0x00, 0x02]);
        let mut cur = std::io::Cursor::new(tcp.clone());
        // read_tcp_frame 返回完整 payload(cmd4+err4+app),调用方剥 8 字节取应用帧
        let payload = read_tcp_frame(&mut cur).unwrap();
        assert_eq!(payload[..8], [0, 0, 0, 2, 0, 0, 0, 0]);
        assert_eq!(payload[8..], app[..]);
    }

    #[test]
    fn handshake_layout() {
        let h = build_tcp_handshake(0x000C);
        assert_eq!(h.len(), 20); // 请求:magic4+len4+cmd4+err4+node2+保留2
        assert_eq!(&h[..4], b"FINS");
        assert_eq!(&h[4..8], &[0, 0, 0, 12]);
        assert_eq!(&h[8..16], &[0; 8]);
        assert_eq!(&h[16..18], &[0x00, 0x0C]);
    }

    #[test]
    fn response_parse() {
        // 头 10 + 0101 + end 0000 + 数据
        let mut f = vec![0xC0, 0x00, 0x02, 0, 0, 0, 0, 0, 0, 0x01];
        f.extend_from_slice(&[0x01, 0x01]);
        f.extend_from_slice(&0x0000u16.to_be_bytes());
        f.extend_from_slice(&[0xBE, 0xEF]);
        let r = parse_response_frame(&f).unwrap();
        assert_eq!(r.end_code, 0);
        assert_eq!(r.data, vec![0xBE, 0xEF]);
    }

    #[test]
    fn bit_read_data_layout_note() {
        // 位读:响应数据每位 1 字节(0/1)——从站与解析端约定,见 fins_slave
        let _ = FinsKind::Bit;
    }
}
