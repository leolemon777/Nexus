//! 西门子 Fetch/Write 协议(S5 兼容,老 SCADA/网关通道)。
//!
//! 16 字节 S5 头(官方手册 C79000-G8976-C182,经 grok 交叉核对):
//! `53 35 10 | 01 03 OPC | 03 08 | ORG | DBN | 地址2B BE | 长度2B BE | FF 02`
//! - OPC:Fetch 请求 0x05 / 响应 0x06+数据;Write 请求 0x03(+数据) / 响应 0x04
//! - ORG:01 DB / 02 M / 03 I / 04 Q / 06 C / 07 T;响应字节 8 = 错误号(00=成功)
//! - 裸 TCP(NetPro 配置的用户端口,常见 2000),**不要默认 102**(会撞 S7comm)

use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{Arc, Mutex};

use crate::error::CoreError;

pub const OPC_FETCH_REQ: u8 = 0x05;
pub const OPC_FETCH_RESP: u8 = 0x06;
pub const OPC_WRITE_REQ: u8 = 0x03;
pub const OPC_WRITE_RESP: u8 = 0x04;

fn fw_err(msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code: "S7_FW_INVALID", message: msg.into(), details: None }
}

/// ORG 区码 ↔ 名称
pub fn fw_area_code(name: &str) -> Option<u8> {
    match name.to_ascii_uppercase().as_str() {
        "DB" => Some(0x01),
        "M" => Some(0x02),
        "I" => Some(0x03),
        "Q" => Some(0x04),
        "C" => Some(0x06),
        "T" => Some(0x07),
        _ => None,
    }
}

/// 构造 Fetch(读)请求头。
pub fn build_fetch(org: u8, db: u8, address: u16, length: u16) -> Vec<u8> {
    let mut f = Vec::with_capacity(16);
    f.extend_from_slice(&[0x53, 0x35, 0x10, 0x01, 0x03, OPC_FETCH_REQ, 0x03, 0x08]);
    f.push(org);
    f.push(db);
    f.extend_from_slice(&address.to_be_bytes());
    f.extend_from_slice(&length.to_be_bytes());
    f.extend_from_slice(&[0xFF, 0x02]);
    f
}

/// 构造 Write 请求(头 + 裸数据)。
pub fn build_write(org: u8, db: u8, address: u16, data: &[u8]) -> Vec<u8> {
    let mut f = build_fetch(org, db, address, data.len() as u16);
    f[5] = OPC_WRITE_REQ;
    f.extend_from_slice(data);
    f
}

/// 解析响应:返回 (opc, 错误号(响应字节8), 数据)。
pub fn parse_response(buf: &[u8]) -> Result<(u8, u8, Vec<u8>), CoreError> {
    if buf.len() < 16 || &buf[..3] != b"S5\x10" {
        return Err(fw_err("不是 Fetch/Write 响应(S5 头不符)"));
    }
    let opc = buf[5];
    let err = buf[8];
    let data = buf[16..].to_vec();
    Ok((opc, err, data))
}

/// 按 length 字段读取完整帧(头 16B 定长,响应长度 = 16 + 请求数据量,由调用方按需读)。
pub fn read_fw_response<R: Read>(reader: &mut R, expect_data: usize) -> Result<Vec<u8>, CoreError> {
    let mut head = [0u8; 16];
    reader.read_exact(&mut head).map_err(|e| fw_err(format!("读 Fetch/Write 头失败:{e}")))?;
    let mut frame = head.to_vec();
    if expect_data > 0 {
        let mut rest = vec![0u8; expect_data];
        reader.read_exact(&mut rest).map_err(|e| fw_err(format!("读数据失败:{e}")))?;
        frame.extend_from_slice(&rest);
    }
    Ok(frame)
}

// ============ 虚拟 Fetch/Write 服务端(供 E2E/演示) ============

/// 与 S7 虚拟 CPU 共享同一份内存(FW 访问 DB/M/I/Q 映射到 s7_slave 的区)。
#[derive(Default)]
pub struct FwMemory {
    pub m: Vec<u8>,
    pub i: Vec<u8>,
    pub q: Vec<u8>,
    pub dbs: std::collections::HashMap<u16, Vec<u8>>,
}

impl FwMemory {
    pub fn new() -> Self {
        Self {
            m: vec![0; 64 * 1024],
            i: vec![0; 64 * 1024],
            q: vec![0; 64 * 1024],
            dbs: std::collections::HashMap::new(),
        }
    }
    fn bank(&mut self, org: u8, db: u8) -> Option<&mut Vec<u8>> {
        match org {
            0x01 => Some(self.dbs.entry(db as u16).or_insert_with(|| vec![0; 64 * 1024])),
            0x02 => Some(&mut self.m),
            0x03 => Some(&mut self.i),
            0x04 => Some(&mut self.q),
            _ => None,
        }
    }
}

pub fn seed_fw(mem: &mut FwMemory) {
    mem.m[0..2].copy_from_slice(&[0x12, 0x34]);
    let db1 = mem.dbs.entry(1).or_default();
    db1.resize(64 * 1024, 0);
    db1[0..4].copy_from_slice(&[0xAA, 0xBB, 0xCC, 0xDD]);
}

/// 处理一条 FW 请求(读 16B 或 写 16B+数据),返回响应帧。
pub fn handle_fw_request(frame: &[u8], mem: &Arc<Mutex<FwMemory>>) -> Result<Vec<u8>, CoreError> {
    if frame.len() < 16 || &frame[..3] != b"S5\x10" {
        return Err(fw_err("S5 头不符"));
    }
    let opc = frame[5];
    let org = frame[8];
    let db = frame[9];
    let address = u16::from_be_bytes([frame[10], frame[11]]) as usize;
    let length = u16::from_be_bytes([frame[12], frame[13]]) as usize;
    let mut m = mem.lock().unwrap_or_else(|e| e.into_inner());
    match opc {
        OPC_FETCH_REQ => {
            let Some(bank) = m.bank(org, db) else {
                return Ok(fw_error_response(org, db, address as u16, length as u16, 0x03));
            };
            if address + length > bank.len() {
                return Ok(fw_error_response(org, db, address as u16, length as u16, 0x05));
            }
            let mut resp = fw_error_response(org, db, address as u16, length as u16, 0x00);
            resp[5] = OPC_FETCH_RESP;
            resp.extend_from_slice(&bank[address..address + length]);
            Ok(resp)
        }
        OPC_WRITE_REQ => {
            let data = &frame[16..];
            if data.len() < length {
                return Err(fw_err("写数据不足"));
            }
            let Some(bank) = m.bank(org, db) else {
                return Ok(fw_error_response(org, db, address as u16, length as u16, 0x03));
            };
            if address + length > bank.len() {
                return Ok(fw_error_response(org, db, address as u16, length as u16, 0x05));
            }
            bank[address..address + length].copy_from_slice(&data[..length]);
            let mut resp = fw_error_response(org, db, address as u16, length as u16, 0x00);
            resp[5] = OPC_WRITE_RESP;
            Ok(resp)
        }
        _ => Err(fw_err(format!("未知 OPC 0x{opc:02X}"))),
    }
}

fn fw_error_response(org: u8, db: u8, address: u16, length: u16, error: u8) -> Vec<u8> {
    let mut f = build_fetch(org, db, address, length);
    f[8] = error;
    f
}

pub fn fw_accept_loop(listener: TcpListener, mem: Arc<Mutex<FwMemory>>, running: Arc<Mutex<bool>>) {
    let _ = listener.set_nonblocking(true);
    while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        match listener.accept() {
            Ok((stream, _)) => {
                let m = Arc::clone(&mem);
                let r = Arc::clone(&running);
                std::thread::spawn(move || fw_serve(stream, m, r));
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
            Err(_) => break,
        }
    }
}

fn fw_serve(mut stream: TcpStream, mem: Arc<Mutex<FwMemory>>, running: Arc<Mutex<bool>>) {
    let _ = stream.set_nonblocking(false);
    let _ = stream.set_read_timeout(Some(std::time::Duration::from_millis(200)));
    // 累积缓冲 + 按长度切帧:read_exact 在超时返回时会消耗已读到的部分字节,
    // 直接 continue 会造成帧错位(半包/粘包也一并防住)
    let mut pending: Vec<u8> = Vec::new();
    let mut chunk = [0u8; 512];
    while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        match stream.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                pending.extend_from_slice(&chunk[..n]);
                while pending.len() >= 16 {
                    let is_write = pending[5] == OPC_WRITE_REQ;
                    let data_len =
                        u16::from_be_bytes([pending[12], pending[13]]) as usize;
                    let total = if is_write { 16 + data_len } else { 16 };
                    if pending.len() < total {
                        break;
                    }
                    let frame: Vec<u8> = pending.drain(..total).collect();
                    match handle_fw_request(&frame, &mem) {
                        Ok(resp) => {
                            if stream.write_all(&resp).is_err() {
                                return;
                            }
                        }
                        Err(_) => return,
                    }
                }
            }
            Err(ref e)
                if e.kind() == std::io::ErrorKind::WouldBlock
                    || e.kind() == std::io::ErrorKind::TimedOut =>
            {
                continue
            }
            Err(_) => break,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fetch_header_golden() {
        // golden(grok 调研,读 DB1 起 2 字节):53 35 10 01 03 05 03 08 01 01 00 00 00 02 FF 02
        assert_eq!(
            build_fetch(0x01, 1, 0, 2),
            vec![0x53, 0x35, 0x10, 0x01, 0x03, 0x05, 0x03, 0x08, 0x01, 0x01, 0x00, 0x00, 0x00, 0x02, 0xFF, 0x02]
        );
    }

    #[test]
    fn write_opc_is_03_not_06() {
        // 关键纠错:写请求 OPC=0x03(0x06 是 Fetch 响应码)
        let w = build_write(0x02, 0, 0x0014, &[0x12, 0x34]);
        assert_eq!(w[5], OPC_WRITE_REQ);
        assert_eq!(&w[16..], &[0x12, 0x34]);
    }

    #[test]
    fn fw_roundtrip_read_write() {
        let mem = Arc::new(Mutex::new({
            let mut m = FwMemory::new();
            seed_fw(&mut m);
            m
        }));
        // 读 DB1 0..4
        let req = build_fetch(0x01, 1, 0, 4);
        let resp = handle_fw_request(&req, &mem).unwrap();
        let (opc, err, data) = parse_response(&resp).unwrap();
        assert_eq!((opc, err), (OPC_FETCH_RESP, 0));
        assert_eq!(data, vec![0xAA, 0xBB, 0xCC, 0xDD]);
        // 写 M50 2 字节 → 读回
        let wr = build_write(0x02, 0, 50, &[0xCA, 0xFE]);
        let resp = handle_fw_request(&wr, &mem).unwrap();
        let (opc, err, _) = parse_response(&resp).unwrap();
        assert_eq!((opc, err), (OPC_WRITE_RESP, 0));
        let rd = handle_fw_request(&build_fetch(0x02, 0, 50, 2), &mem).unwrap();
        assert_eq!(parse_response(&rd).unwrap().2, vec![0xCA, 0xFE]);
    }

    #[test]
    fn out_of_range_error_byte() {
        let mem = Arc::new(Mutex::new(FwMemory::new()));
        let req = build_fetch(0x02, 0, 60000, 40000);
        let resp = handle_fw_request(&req, &mem).unwrap();
        assert_eq!(parse_response(&resp).unwrap().1, 0x05);
    }
}
