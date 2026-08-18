//! 欧姆龙 FINS 虚拟 PLC(自测 + 演示):FINS/TCP 握手 + 0101/0102 读写。
//!
//! 内存:CIO/W/H/A 各 32768 字(线性字空间),DM 32768 字,TIM/CNT 8192 字。
//! 位访问按「字偏移×16+位」线性换算;位读响应每位 1 字节(0/1),位写数据同理。

use std::collections::HashMap;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream, UdpSocket};
use std::sync::{Arc, Mutex};

use crate::error::CoreError;
use crate::fins_address::area;
use crate::fins_frame::{
    build_tcp_handshake, parse_response_frame, read_tcp_frame, wrap_tcp, write_frame, FinsNodes,
};

const WORDS: usize = 32768;
const TC_WORDS: usize = 8192;

#[derive(Default)]
pub struct FinsMemory {
    pub cio: Vec<u16>,
    pub w: Vec<u16>,
    pub h: Vec<u16>,
    pub a: Vec<u16>,
    pub dm: Vec<u16>,
    pub tim_cnt: Vec<u16>,
}

impl FinsMemory {
    pub fn new() -> Self {
        Self {
            cio: vec![0; WORDS],
            w: vec![0; WORDS],
            h: vec![0; WORDS],
            a: vec![0; WORDS],
            dm: vec![0; WORDS],
            tim_cnt: vec![0; TC_WORDS],
        }
    }

    fn bank_mut(&mut self, area_code: u8) -> Option<&mut Vec<u16>> {
        match area_code {
            area::CIO_WORD | area::CIO_BIT => Some(&mut self.cio),
            area::W_WORD | area::W_BIT => Some(&mut self.w),
            area::H_WORD | area::H_BIT => Some(&mut self.h),
            area::A_WORD | area::A_BIT => Some(&mut self.a),
            area::DM_WORD | area::DM_BIT => Some(&mut self.dm),
            area::TIMER_CNT_WORD => Some(&mut self.tim_cnt),
            _ => None,
        }
    }
}

/// 演示数据:D100=0x1234, D101=0xABCD, CIO0=0xBEEF, W10=0x55AA, H0=0x00FF, T0=100, C0=7
pub fn seed_demo(mem: &mut FinsMemory) {
    mem.dm[100] = 0x1234;
    mem.dm[101] = 0xABCD;
    mem.dm[200] = 0xBEEF;
    mem.cio[0] = 0xBEEF;
    mem.w[10] = 0x55AA;
    mem.h[0] = 0x00FF;
    mem.tim_cnt[0] = 100;
    mem.tim_cnt[5000] = 7; // C0(计数区从 5000 起?为简单:T/C 共用同一 bank,C 编号偏移 5000 由 UI 说明)
}

/// 处理一条 FINS 应用帧(TCP/UDP 共用)。
pub fn handle_fins_request(app: &[u8], mem: &Arc<Mutex<FinsMemory>>) -> Vec<u8> {
    if app.len() < 10 {
        return error_response(app, 0x0001);
    }
    let service = &app[10..];
    if service.len() < 2 {
        return error_response(app, 0x0004);
    }
    let end_code_data: (u16, Vec<u8>) = match (&service[0..2], service.get(2)) {
        ([0x01, 0x01], Some(&word_bit)) => handle_read(word_bit, &service[3..], mem),
        ([0x01, 0x02], Some(&word_bit)) => handle_write(word_bit, &service[3..], mem),
        _ => (0x0003, Vec::new()), // 不支持的服务
    };
    // 响应帧:回显头(ICF 置响应位)+ SID + 服务码 + 结束码 + 数据
    let (end_code, data) = end_code_data;
    let mut resp = Vec::with_capacity(13 + data.len());
    resp.extend_from_slice(&app[..9]);
    resp[0] |= 0x40; // ICF:响应帧位(0x40)
    resp.push(app[9]); // SID 回显
    resp.extend_from_slice(&service[..2].to_vec());
    resp.extend_from_slice(&end_code.to_be_bytes());
    resp.extend_from_slice(&data);
    resp
}

fn error_response(req: &[u8], code: u16) -> Vec<u8> {
    let mut resp = Vec::with_capacity(13);
    let head_len = req.len().min(10);
    resp.extend_from_slice(&req[..head_len]);
    if head_len == 10 {
        resp[0] |= 0x40;
    } else {
        resp.resize(10, 0);
        resp[0] = 0xC0;
    }
    resp.extend_from_slice(&code.to_be_bytes());
    resp
}

/// 解析读/写参数尾:区代码 + 地址 3B + 点数 2B [+ 数据]
fn parse_params(tail: &[u8]) -> Option<(u8, u32, u16)> {
    if tail.len() < 6 {
        return None;
    }
    let area_code = tail[0];
    let address = u32::from_be_bytes([0, tail[1], tail[2], tail[3]]);
    let count = u16::from_be_bytes([tail[4], tail[5]]);
    Some((area_code, address, count))
}

fn handle_read(word_bit: u8, tail: &[u8], mem: &Arc<Mutex<FinsMemory>>) -> (u16, Vec<u8>) {
    let Some((area_code, address, count)) = parse_params(tail) else {
        return (0x0004, Vec::new());
    };
    let mut m = mem.lock().unwrap_or_else(|e| e.into_inner());
    let Some(bank) = m.bank_mut(area_code) else {
        return (0x0201, Vec::new()); // 区代码错误
    };
    let n = count as usize;
    if word_bit == 0x00 {
        // 位读:每位 1 字节
        let word_addr = (address / 16) as usize;
        let bit = (address % 16) as u16;
        if word_addr.saturating_add(n / 16 + 1) >= bank.len() {
            return (0x0203, Vec::new());
        }
        let mut out = Vec::with_capacity(n);
        for i in 0..n {
            let w = bank[word_addr + (bit as usize + i) / 16];
            let b = (bit as usize + i) % 16;
            out.push(((w >> b) & 1) as u8);
        }
        (0x0000, out)
    } else {
        let start = address as usize;
        if start.saturating_add(n) > bank.len() {
            return (0x0203, Vec::new());
        }
        let mut out = Vec::with_capacity(n * 2);
        for i in 0..n {
            out.extend_from_slice(&bank[start + i].to_be_bytes());
        }
        (0x0000, out)
    }
}

fn handle_write(word_bit: u8, tail: &[u8], mem: &Arc<Mutex<FinsMemory>>) -> (u16, Vec<u8>) {
    let Some((area_code, address, count)) = parse_params(tail) else {
        return (0x0004, Vec::new());
    };
    let data = &tail[6..];
    let mut m = mem.lock().unwrap_or_else(|e| e.into_inner());
    let Some(bank) = m.bank_mut(area_code) else {
        return (0x0201, Vec::new());
    };
    let n = count as usize;
    if word_bit == 0x00 {
        if data.len() < n {
            return (0x0004, Vec::new());
        }
        let word_addr = (address / 16) as usize;
        let bit = (address % 16) as usize;
        for i in 0..n {
            let wi = word_addr + (bit + i) / 16;
            let b = (bit + i) % 16;
            if wi >= bank.len() {
                return (0x0203, Vec::new());
            }
            if data[i] != 0 {
                bank[wi] |= 1 << b;
            } else {
                bank[wi] &= !(1 << b);
            }
        }
        (0x0000, Vec::new())
    } else {
        if data.len() < n * 2 {
            return (0x0004, Vec::new());
        }
        let start = address as usize;
        if start.saturating_add(n) > bank.len() {
            return (0x0203, Vec::new());
        }
        for i in 0..n {
            bank[start + i] = u16::from_be_bytes([data[i * 2], data[i * 2 + 1]]);
        }
        (0x0000, Vec::new())
    }
}

// ============ TCP 服务 ============

// TCP 主循环:accept + 每连接处理循环(read_tcp_frame → handle → wrap 回写)
pub fn fins_tcp_accept_loop(listener: TcpListener, memory: Arc<Mutex<FinsMemory>>, running: Arc<Mutex<bool>>) {
    let _ = listener.set_nonblocking(true);
    while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        match listener.accept() {
            Ok((stream, _)) => {
                let mem = Arc::clone(&memory);
                let rf = Arc::clone(&running);
                std::thread::spawn(move || fins_serve_tcp(stream, mem, rf));
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
            Err(_) => break,
        }
    }
}

fn fins_serve_tcp(mut stream: TcpStream, memory: Arc<Mutex<FinsMemory>>, running: Arc<Mutex<bool>>) {
    let _ = stream.set_nonblocking(false);
    let _ = stream.set_read_timeout(Some(std::time::Duration::from_millis(200)));
    // 握手
    let _ = stream.set_read_timeout(Some(std::time::Duration::from_secs(2)));
    let first = match read_tcp_frame(&mut stream) {
        Ok(f) => f,
        Err(_) => return,
    };
    let client_node = if first.len() >= 10 { u16::from_be_bytes([first[8], first[9]]) } else { 0 };
    let mut hs = Vec::with_capacity(20);
    hs.extend_from_slice(b"FINS");
    // length = cmd(4)+err(4)+server_node(2)+client_node(2) = 12
    hs.extend_from_slice(&12u32.to_be_bytes());
    hs.extend_from_slice(&0x00000001u32.to_be_bytes());
    hs.extend_from_slice(&0x00000000u32.to_be_bytes());
    hs.extend_from_slice(&1u16.to_be_bytes());
    hs.extend_from_slice(&client_node.to_be_bytes());
    if write_frame(&mut stream, &hs).is_err() {
        return;
    }
    let _ = stream.set_read_timeout(Some(std::time::Duration::from_millis(200)));
    while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        // 帧可能跨 read —— 用缓冲流逐帧读
        match read_tcp_frame(&mut &stream) {
            Ok(payload) => {
                // payload = cmd(4B)+err(4B)+app —— SEND cmd
                if payload.len() < 8 {
                    break;
                }
                let app = &payload[8..];
                let resp = handle_fins_request(app, &memory);
                if write_frame(&mut stream, &wrap_tcp(&resp)).is_err() {
                    break;
                }
            }
            Err(CoreError::Modbus { code, .. }) if code == "FINS_READ_FAILED" => continue,
            Err(_) => break,
        }
    }
}

/// UDP 服务:裸应用帧。
pub fn fins_udp_loop(socket: UdpSocket, memory: Arc<Mutex<FinsMemory>>, running: Arc<Mutex<bool>>) {
    let _ = socket.set_read_timeout(Some(std::time::Duration::from_millis(200)));
    let mut buf = [0u8; 2048];
    while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        match socket.recv_from(&mut buf) {
            Ok((n, peer)) => {
                let resp = handle_fins_request(&buf[..n], &memory);
                let _ = socket.send_to(&resp, peer);
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock
                || e.kind() == std::io::ErrorKind::TimedOut => continue,
            Err(_) => break,
        }
    }
}

/// 直读内存(测试/JSONL 从站 set/get)。
pub fn memory_read(mem: &FinsMemory, area_code: u8, word_addr: usize, count: usize) -> Option<Vec<u16>> {
    let bank = match area_code {
        area::CIO_WORD => &mem.cio,
        area::W_WORD => &mem.w,
        area::H_WORD => &mem.h,
        area::A_WORD => &mem.a,
        area::DM_WORD => &mem.dm,
        area::TIMER_CNT_WORD => &mem.tim_cnt,
        _ => return None,
    };
    if word_addr + count > bank.len() {
        return None;
    }
    Some(bank[word_addr..word_addr + count].to_vec())
}

pub fn memory_write(mem: &mut FinsMemory, area_code: u8, word_addr: usize, values: &[u16]) -> Option<()> {
    let bank = match area_code {
        area::CIO_WORD => &mut mem.cio,
        area::W_WORD => &mut mem.w,
        area::H_WORD => &mut mem.h,
        area::A_WORD => &mut mem.a,
        area::DM_WORD => &mut mem.dm,
        area::TIMER_CNT_WORD => &mut mem.tim_cnt,
        _ => return None,
    };
    if word_addr + values.len() > bank.len() {
        return None;
    }
    bank[word_addr..word_addr + values.len()].copy_from_slice(values);
    Some(())
}

/// 默认节点(直连仿真)。
pub fn default_nodes() -> FinsNodes {
    FinsNodes::default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::fins_address::parse_fins_address;
    use crate::fins_frame::{build_read_frame, build_write_frame};

    fn mem_seeded() -> Arc<Mutex<FinsMemory>> {
        let mut m = FinsMemory::new();
        seed_demo(&mut m);
        Arc::new(Mutex::new(m))
    }

    #[test]
    fn read_dm_words_roundtrip() {
        let mem = mem_seeded();
        let addr = parse_fins_address("D100").unwrap();
        let req = build_read_frame(&default_nodes(), 1, &addr, 2);
        let resp = handle_fins_request(&req, &mem);
        let parsed = parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.end_code, 0);
        assert_eq!(parsed.data, vec![0x12, 0x34, 0xAB, 0xCD]);
    }

    #[test]
    fn write_then_read_back() {
        let mem = mem_seeded();
        let addr = parse_fins_address("W20").unwrap();
        let req = build_write_frame(&default_nodes(), 2, &addr, 2, &[0xCA, 0xFE, 0xBA, 0xBE]);
        let resp = handle_fins_request(&req, &mem);
        assert_eq!(parse_response_frame(&resp).unwrap().end_code, 0);
        let rd = handle_fins_request(&build_read_frame(&default_nodes(), 3, &addr, 2), &mem);
        assert_eq!(parse_response_frame(&rd).unwrap().data, vec![0xCA, 0xFE, 0xBA, 0xBE]);
    }

    #[test]
    fn bit_read_write() {
        let mem = mem_seeded();
        // CIO0=0xBEEF → bit0=1,bit1=1,bit2=0...
        let addr = parse_fins_address("CIO0.00").unwrap();
        let rd = handle_fins_request(&build_read_frame(&default_nodes(), 1, &addr, 4), &mem);
        assert_eq!(parse_response_frame(&rd).unwrap().data, vec![1, 1, 1, 1]); // 0xBEEF 低4位
        // 写 CIO0.00=0(清 bit0) → 0xBEEF & ~0x1 = 0xBEEE
        let wr = handle_fins_request(&build_write_frame(&default_nodes(), 2, &addr, 1, &[0]), &mem);
        assert_eq!(parse_response_frame(&wr).unwrap().end_code, 0);
        assert_eq!(mem.lock().unwrap().cio[0], 0xBEEE);
    }

    #[test]
    fn out_of_range_end_code() {
        let mem = mem_seeded();
        let addr = parse_fins_address("D40000").unwrap();
        let rd = handle_fins_request(&build_read_frame(&default_nodes(), 1, &addr, 2), &mem);
        assert_eq!(parse_response_frame(&rd).unwrap().end_code, 0x0203);
    }

    #[test]
    fn tim_cnt_read() {
        let mem = mem_seeded();
        let t0 = parse_fins_address("T0").unwrap();
        let rd = handle_fins_request(&build_read_frame(&default_nodes(), 1, &t0, 1), &mem);
        assert_eq!(parse_response_frame(&rd).unwrap().data, vec![0x00, 100]);
    }

    #[test]
    fn unknown_area_0201() {
        let mem = mem_seeded();
        let addr = parse_fins_address("D0").unwrap();
        let mut req = build_read_frame(&default_nodes(), 1, &addr, 1);
        req[13] = 0x99; // 区代码改坏
        let rd = handle_fins_request(&req, &mem);
        assert_eq!(parse_response_frame(&rd).unwrap().end_code, 0x0201);
    }
}
