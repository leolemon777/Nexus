//! 虚拟 S7-200 PPI 从站(TCP 透传形态:接收 SD2 → E5 → 短帧确认 → SD2 数据帧)。
//! 内存复用 s7_slave 的 S7SlaveMemory(V 区=DB1);seed 同 s7_slave::seed_demo。

use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{Arc, Mutex};

use crate::error::CoreError;
use crate::ppi_frame::{build_sd2, parse_sd2, FC_READ, FC_WRITE};
use crate::s7_pdu::{parse_ack, S7Ack};

fn ppi_err(msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code: "S7_PPI_INVALID", message: msg.into(), details: None }
}

/// 处理一条 SD2 请求(已剥壳的 S7 PDU)→ 响应 S7 PDU(ack 形态,DA/SA 互换由调用方做)。
fn handle_ppi_s7(pdu: &[u8], mem: &Arc<Mutex<crate::s7_slave::S7SlaveMemory>>) -> Result<Vec<u8>, CoreError> {
    let resp = crate::s7_slave::handle_s7_request(pdu, mem);
    let _ = parse_ack(&resp)?; // 形态自检(顺带早失败)
    Ok(resp)
}

pub fn ppi_accept_loop(listener: TcpListener, mem: Arc<Mutex<crate::s7_slave::S7SlaveMemory>>, running: Arc<Mutex<bool>>) {
    let _ = listener.set_nonblocking(true);
    while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        match listener.accept() {
            Ok((stream, _)) => {
                let m = Arc::clone(&mem);
                let r = Arc::clone(&running);
                std::thread::spawn(move || ppi_serve(stream, m, r));
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
            Err(_) => break,
        }
    }
}

/// 双拍状态机:缓冲字节 → 完整 SD2 请求 → 回 E5 → 等 10 5C 短帧 → 回 SD2 响应。
fn ppi_serve(mut stream: TcpStream, mem: Arc<Mutex<crate::s7_slave::S7SlaveMemory>>, running: Arc<Mutex<bool>>) {
    let _ = stream.set_nonblocking(false);
    let _ = stream.set_read_timeout(Some(std::time::Duration::from_millis(200)));
    let mut pending: Vec<u8> = Vec::new();
    let mut chunk = [0u8; 512];
    'outer: while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        match stream.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                pending.extend_from_slice(&chunk[..n]);
                if pending.len() > 64 * 1024 {
                    pending.clear(); // 缓冲上限:防恶意客户端撑爆内存
                }
            },
            Err(ref e)
                if e.kind() == std::io::ErrorKind::WouldBlock
                    || e.kind() == std::io::ErrorKind::TimedOut => continue,
            Err(_) => break,
        }
        // 循环取完整请求帧
        loop {
            let Some(frame_end) = find_sd2_end(&pending) else { continue 'outer };
            let frame: Vec<u8> = pending.drain(..frame_end).collect();
            let (da, sa, fc, s7_pdu) = match parse_sd2(&frame) {
                Ok(v) => v,
                Err(_) => continue 'outer, // 坏帧丢弃
            };
            if fc != FC_READ && fc != FC_WRITE {
                continue 'outer;
            }
            // ① 回 SC=E5
            if stream.write_all(&[0xE5]).is_err() {
                break 'outer;
            }
            // ② 等待短帧确认 10 DA SA 5C … 16
            loop {
                if !*running.lock().unwrap_or_else(|e| e.into_inner()) {
                    break 'outer;
                }
                if pending.len() >= 6 && pending[0] == 0x10 {
                    pending.drain(..6);
                    break;
                }
                if pending.len() >= 1 && pending[0] == 0xE5 {
                    pending.drain(..1);
                    continue;
                }
                match stream.read(&mut chunk) {
                    Ok(0) => break 'outer,
                    Ok(n) => {
                pending.extend_from_slice(&chunk[..n]);
                if pending.len() > 64 * 1024 {
                    pending.clear(); // 缓冲上限:防恶意客户端撑爆内存
                }
            },
                    Err(ref e)
                        if e.kind() == std::io::ErrorKind::WouldBlock
                            || e.kind() == std::io::ErrorKind::TimedOut => {}
                    Err(_) => break 'outer,
                }
            }
            // ③ 回 SD2 响应(DA/SA 互换,FC=0x08)
            match handle_ppi_s7(&s7_pdu, &mem) {
                Ok(resp_pdu) => {
                    let resp = build_sd2(sa, da, 0x08, &resp_pdu);
                    if stream.write_all(&resp).is_err() {
                        break 'outer;
                    }
                }
                Err(_) => continue 'outer,
            }
        }
    }
}

/// 找 SD2 帧结束(返回整帧长度含 16):头 68 LE LEr 68 … FCS 16。
fn find_sd2_end(buf: &[u8]) -> Option<usize> {
    if buf.len() < 4 || buf[0] != 0x68 {
        return None;
    }
    let le = buf[1] as usize;
    if buf.len() < 4 + le + 2 {
        return None;
    }
    Some(4 + le + 2)
}

// 供 session 走 TCP 双拍的客户端工具也在 ppi_frame;此处仅供测试的单拍语义校验。
#[allow(dead_code)]
fn _unused(_: &S7Ack) {}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ppi_frame::{build_sa_confirm, build_sd2};
    use crate::s7_pdu::{build_read_request, parse_read_response, S7Item};

    /// TCP 双拍回环:请求 → E5 → 短帧 → 数据帧(在同一内存上模拟)
    #[test]
    fn dual_poll_roundtrip_semantics() {
        let mut m = crate::s7_slave::S7SlaveMemory::new();
        crate::s7_slave::seed_demo(&mut m);
        let mem = Arc::new(Mutex::new(m));
        // 读 DB1(V)0..4 = 12 34 56 78
        let items = [S7Item::new("VB0", 4).unwrap()];
        let req_pdu = build_read_request(0, &items).unwrap();
        let req = build_sd2(2, 0, FC_READ, &req_pdu);
        // ① 服务端解析
        let (da, sa, _fc, inner) = parse_sd2(&req).unwrap();
        assert_eq!((da, sa), (2, 0));
        // ② 响应构造(DA/SA 互换)
        let resp_pdu = handle_ppi_s7(&inner, &mem).unwrap();
        let resp = build_sd2(sa, da, 0x08, &resp_pdu);
        // ③ 客户端解析
        let (_, _, _, resp_inner) = parse_sd2(&resp).unwrap();
        let ack = parse_ack(&resp_inner).unwrap();
        let items = parse_read_response(&ack).unwrap();
        assert_eq!(items[0].data, vec![0x12, 0x34, 0x56, 0x78]);
        // 短帧确认 golden
        assert_eq!(build_sa_confirm(2, 0)[..], [0x10, 0x02, 0x00, 0x5C, 0x5E, 0x16]);
    }
}
