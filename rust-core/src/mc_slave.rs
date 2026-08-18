//! 三菱 MC 协议虚拟从站——内存模型 + 请求处理。
//!
//! 对齐 modbus_slave.rs 模式:一块虚拟内存按软元件区映射,
//! 解析进来的 MC 3E/4E 请求帧并生成响应帧。供自测与 UI 模拟使用。
//!
//! 内存模型:每个 device code 一块独立 Vec(u16)(位元件按位打包进 u16)。
//! 基础容量按文档 §6.2 的保守上限。

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use crate::error::CoreError;
use crate::mc_address::device_spec;
use crate::mc_frame::{build_response_frame, parse_request_frame, FrameType};
use crate::mc_pdu::{CMD_READ_BATCH, CMD_WRITE_BATCH, SUBCMD_BIT};

/// 虚拟 MC 从站内存。
pub struct McSlaveMemory {
    /// device code → 内存块(统一 u16 存储:字元件每项 1 字,位元件每项 1 位)
    blocks: HashMap<u8, Vec<u16>>,
}

impl McSlaveMemory {
    pub fn new() -> Self {
        let mut blocks = HashMap::new();
        // 为常用软元件分配保守容量(§6.2 FX5U/Q 典型值)
        for (prefix, cap) in [
            ("X", 1024), ("Y", 1024), ("M", 8192), ("L", 8192),
            ("B", 8192), ("S", 4096), ("SM", 8192),
            ("D", 12288), ("W", 8192), ("SD", 8192), ("R", 32768),
            ("TS", 1024), ("TC", 1024), ("TN", 1024),
            ("CS", 1024), ("CC", 1024), ("CN", 1024),
        ] {
            if let Some(spec) = device_spec(prefix) {
                blocks.insert(spec.code, vec![0u16; cap]);
            }
        }
        Self { blocks }
    }

    /// 写字元件值。
    pub fn set_words(&mut self, device_code: u8, start: u32, values: &[u16]) -> Result<(), CoreError> {
        let block = self.block_mut(device_code)?;
        let end = start as usize + values.len();
        if end > block.len() {
            return Err(out_of_range(device_code, start, block.len()));
        }
        block[start as usize..end].copy_from_slice(values);
        Ok(())
    }

    /// 读字元件值。
    pub fn get_words(&mut self, device_code: u8, start: u32, count: u16) -> Result<Vec<u16>, CoreError> {
        let block = self.block_mut(device_code)?;
        let end = start as usize + count as usize;
        if end > block.len() {
            return Err(out_of_range(device_code, start, block.len()));
        }
        Ok(block[start as usize..end].to_vec())
    }

    /// 写位元件(值 0/1)。
    pub fn set_bits(&mut self, device_code: u8, start: u32, values: &[u16]) -> Result<(), CoreError> {
        let block = self.block_mut(device_code)?;
        let end = start as usize + values.len();
        if end > block.len() {
            return Err(out_of_range(device_code, start, block.len()));
        }
        for (i, v) in values.iter().enumerate() {
            block[start as usize + i] = *v & 1;
        }
        Ok(())
    }

    /// 读位元件(值 0/1)。
    pub fn get_bits(&mut self, device_code: u8, start: u32, count: u16) -> Result<Vec<u16>, CoreError> {
        self.get_words(device_code, start, count)
            .map(|v| v.into_iter().map(|x| x & 1).collect())
    }

    fn block_mut(&mut self, device_code: u8) -> Result<&mut Vec<u16>, CoreError> {
        self.blocks.get_mut(&device_code).ok_or_else(|| CoreError::Modbus {
            code: "MC_DEVICE_UNSUPPORTED",
            message: format!("虚拟从站未实现软元件代码 {device_code:#04x}"),
            details: None,
        })
    }
}

impl Default for McSlaveMemory {
    fn default() -> Self {
        Self::new()
    }
}

/// 预置演示数据(对齐 modbus_slave::seed_demo 习惯)。
pub fn seed_demo(mem: &mut McSlaveMemory) {
    let d = device_spec("D").unwrap().code;
    let _ = mem.set_words(d, 100, &[0x1234, 0xABCD, 1, 2, 3]);
    let _ = mem.set_words(d, 200, &[0xBEEF]);
    let m = device_spec("M").unwrap().code;
    // M0~M11 交替 ON/OFF
    let alt: Vec<u16> = (0..12).map(|i| (i % 2 == 0) as u16).collect();
    let _ = mem.set_bits(m, 0, &alt);
}

/// 处理一帧 MC 3E/4E 请求,生成响应帧字节。
///
/// 返回 Err 仅表示**内部错误**(帧损坏等,应断开连接);
/// PLC 业务错误(地址越界等)通过结束代码写进响应帧——与真机行为一致。
pub fn handle_mc_request(frame: &[u8], memory: &Arc<Mutex<McSlaveMemory>>) -> Result<Vec<u8>, CoreError> {
    // 1E 帧识别(A-1E/SLMP-1E,§3.4):命令 00~03 + PC号 FF
    if is_1e_request(frame) {
        return handle_1e_request(frame, memory);
    }
    let req = parse_request_frame(frame)?;

    let (end_code, data) = match req.command {
        CMD_READ_BATCH => handle_read(&req, memory),
        CMD_WRITE_BATCH => handle_write(&req, memory),
        crate::mc_pdu::CMD_READ_RANDOM => handle_read_random(&req, memory),
        crate::mc_pdu::CMD_WRITE_RANDOM_WORD => handle_write_random_word(&req, memory),
        crate::mc_pdu::CMD_READ_BLOCKS => handle_read_blocks(&req, memory),
        crate::mc_pdu::CMD_ECHO_TEST => (0x0000u16, req.data.clone()), // 回送:原样返回
        crate::mc_pdu::CMD_READ_CPU_TYPE => (0x0000u16, b"Nexus-Rust-VM".to_vec()),
        crate::mc_pdu::CMD_READ_CPU_STATUS => (0x0000u16, vec![0x00]),       // RUN
        crate::mc_pdu::CMD_READ_CLOCK => (
            0x0000u16,
            vec![0x26, 0x08, 0x15, 0x14, 0x30, 0x00, 0x05], // 2026-08-15 14:30:00 周五
        ),
        crate::mc_pdu::CMD_REMOTE_RUN | crate::mc_pdu::CMD_REMOTE_STOP
        | crate::mc_pdu::CMD_REMOTE_PAUSE | crate::mc_pdu::CMD_REMOTE_RESET => (0x0000u16, vec![]),
        _ => (0x0007u16, Vec::new()), // 无法识别指令
    };

    Ok(build_response_frame(req.frame_type, req.sequence, end_code, &data))
}

/// 处理 0403 随机读:点数(2B) + [地址(3B)+代码(1B)]×n。
fn handle_read_random(req: &crate::mc_frame::McRequestFrame, memory: &Arc<Mutex<McSlaveMemory>>) -> (u16, Vec<u8>) {
    if req.data.len() < 2 {
        return (0x0004, vec![]);
    }
    let count = u16::from_le_bytes([req.data[0], req.data[1]]) as usize;
    if req.data.len() < 2 + count * 4 {
        return (0x0004, vec![]);
    }
    let is_bit = req.subcommand == 0x0001; // 0403 位=0001(与 0401 相反)
    let mut data = Vec::with_capacity(count * 2);
    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    for i in 0..count {
        let off = 2 + i * 4;
        let head = (req.data[off] as u32) | ((req.data[off + 1] as u32) << 8) | ((req.data[off + 2] as u32) << 16);
        let code = req.data[off + 3];
        let result = if is_bit { mem.get_bits(code, head, 1) } else { mem.get_words(code, head, 1) };
        match result {
            Ok(vals) => {
                if is_bit {
                    data.push(vals[0] as u8);
                } else {
                    data.extend_from_slice(&vals[0].to_le_bytes());
                }
            }
            Err(_) => return (0x00D2, vec![]),
        }
    }
    (0x0000, data)
}

/// 处理 1403 随机写(字单位):点数 + [地址+代码+字数据2B]×n。
fn handle_write_random_word(req: &crate::mc_frame::McRequestFrame, memory: &Arc<Mutex<McSlaveMemory>>) -> (u16, Vec<u8>) {
    if req.data.len() < 2 {
        return (0x0004, vec![]);
    }
    let count = u16::from_le_bytes([req.data[0], req.data[1]]) as usize;
    if req.data.len() < 2 + count * 6 {
        return (0x0004, vec![]);
    }
    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    for i in 0..count {
        let off = 2 + i * 6;
        let head = (req.data[off] as u32) | ((req.data[off + 1] as u32) << 8) | ((req.data[off + 2] as u32) << 16);
        let code = req.data[off + 3];
        let value = u16::from_le_bytes([req.data[off + 4], req.data[off + 5]]);
        if mem.set_words(code, head, &[value]).is_err() {
            return (0x00D2, vec![]);
        }
    }
    (0x0000, vec![])
}

/// 处理 0406 多块成批读:块数(2B) + [点数(2B)+地址(3B)+代码(1B)]×块。
fn handle_read_blocks(req: &crate::mc_frame::McRequestFrame, memory: &Arc<Mutex<McSlaveMemory>>) -> (u16, Vec<u8>) {
    if req.data.len() < 2 {
        return (0x0004, vec![]);
    }
    let block_count = u16::from_le_bytes([req.data[0], req.data[1]]) as usize;
    let mut off = 2;
    let is_bit = req.subcommand == 0x0001; // 0406 位=0001
    let mut data = Vec::new();
    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    for _ in 0..block_count {
        if off + 6 > req.data.len() {
            return (0x0004, vec![]);
        }
        let points = u16::from_le_bytes([req.data[off], req.data[off + 1]]);
        let head = (req.data[off + 2] as u32) | ((req.data[off + 3] as u32) << 8) | ((req.data[off + 4] as u32) << 16);
        let code = req.data[off + 5];
        off += 6;
        let result = if is_bit { mem.get_bits(code, head, points) } else { mem.get_words(code, head, points) };
        match result {
            Ok(vals) => {
                if is_bit {
                    for v in vals {
                        data.push(v as u8);
                    }
                } else {
                    for v in vals {
                        data.extend_from_slice(&v.to_le_bytes());
                    }
                }
            }
            Err(_) => return (0x00D2, vec![]),
        }
    }
    (0x0000, data)
}

/// 处理 0401 成批读。
fn handle_read(req: &crate::mc_frame::McRequestFrame, memory: &Arc<Mutex<McSlaveMemory>>) -> (u16, Vec<u8>) {
    // 数据区:头设备号 3B + 软元件代码 1B + 点数 2B
    if req.data.len() < 6 {
        return (0x0004, vec![]);
    }
    let head = (req.data[0] as u32) | ((req.data[1] as u32) << 8) | ((req.data[2] as u32) << 16);
    let device_code = req.data[3];
    let points = u16::from_le_bytes([req.data[4], req.data[5]]);

    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    let is_bit = req.subcommand == SUBCMD_BIT;
    let result = if is_bit {
        mem.get_bits(device_code, head, points)
    } else {
        mem.get_words(device_code, head, points)
    };
    match result {
        Ok(values) => {
            let mut data = Vec::with_capacity(values.len() * 2);
            if is_bit {
                for v in values {
                    data.push(v as u8);
                }
            } else {
                for v in values {
                    data.extend_from_slice(&v.to_le_bytes());
                }
            }
            (0x0000, data)
        }
        Err(_) => (0x00D2, vec![]), // 头软元件编号非法
    }
}

/// 处理 1401 成批写。
fn handle_write(req: &crate::mc_frame::McRequestFrame, memory: &Arc<Mutex<McSlaveMemory>>) -> (u16, Vec<u8>) {
    // 数据区:头设备号 3B + 软元件代码 1B + 点数 2B + 写数据
    if req.data.len() < 6 {
        return (0x0004, vec![]);
    }
    let head = (req.data[0] as u32) | ((req.data[1] as u32) << 8) | ((req.data[2] as u32) << 16);
    let device_code = req.data[3];
    let points = u16::from_le_bytes([req.data[4], req.data[5]]) as usize;
    let payload = &req.data[6..];

    let is_bit = req.subcommand == SUBCMD_BIT;
    let expected = if is_bit { points } else { points * 2 };
    if payload.len() < expected {
        return (0x0004, vec![]);
    }

    let values: Vec<u16> = if is_bit {
        payload[..points].iter().map(|&b| u16::from(b)).collect()
    } else {
        (0..points)
            .map(|i| u16::from_le_bytes([payload[i * 2], payload[i * 2 + 1]]))
            .collect()
    };

    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    let result = if is_bit {
        mem.set_bits(device_code, head, &values)
    } else {
        mem.set_words(device_code, head, &values)
    };
    match result {
        Ok(()) => (0x0000, vec![]), // 写响应无数据
        Err(_) => (0x00D2, vec![]),
    }
}

fn out_of_range(device_code: u8, start: u32, len: usize) -> CoreError {
    CoreError::Modbus {
        code: "MC_ADDRESS_OUT_OF_RANGE",
        message: format!("软元件 {device_code:#04x} 地址 {start} 超出虚拟从站范围(0..{len})"),
        details: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::mc_address::parse_mc_address;
    use crate::mc_frame::{build_request_frame, AccessRoute};
    use crate::mc_pdu::{build_read_batch_pdu, build_write_batch_pdu};

    fn setup_mem() -> Arc<Mutex<McSlaveMemory>> {
        let mut mem = McSlaveMemory::new();
        seed_demo(&mut mem);
        Arc::new(Mutex::new(mem))
    }

    /// E2E:读 D100(预置 0x1234)→ 响应数据 34 12
    #[test]
    fn e2e_read_d100_returns_seeded_value() {
        let mem = setup_mem();
        let addr = parse_mc_address("D100").unwrap();
        let req_data = build_read_batch_pdu(&addr, 1).unwrap();
        let frame = build_request_frame(FrameType::Type3E, &AccessRoute::default(), 0x0010, &req_data, 0);

        let resp = handle_mc_request(&frame, &mem).unwrap();
        let parsed = crate::mc_frame::parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.end_code, 0x0000);
        assert_eq!(parsed.data, vec![0x34, 0x12]); // 0x1234 小端
    }

    /// E2E:写 M100 = ON → 读回 ON
    #[test]
    fn e2e_write_and_read_back_m100() {
        let mem = setup_mem();
        let addr = parse_mc_address("M100").unwrap();

        // 写 ON
        let req_data = build_write_batch_pdu(&addr, &[1]).unwrap();
        let frame = build_request_frame(FrameType::Type3E, &AccessRoute::default(), 0x0010, &req_data, 0);
        let resp = handle_mc_request(&frame, &mem).unwrap();
        let parsed = crate::mc_frame::parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.end_code, 0x0000);
        assert!(parsed.data.is_empty());

        // 读回
        let req_data = build_read_batch_pdu(&addr, 1).unwrap();
        let frame = build_request_frame(FrameType::Type3E, &AccessRoute::default(), 0x0010, &req_data, 0);
        let resp = handle_mc_request(&frame, &mem).unwrap();
        let parsed = crate::mc_frame::parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.data, vec![0x01]);
    }

    /// E2E:读 M0~M11 交替位(seed_demo 预置)
    #[test]
    fn e2e_read_alternating_bits() {
        let mem = setup_mem();
        let addr = parse_mc_address("M0").unwrap();
        let req_data = build_read_batch_pdu(&addr, 12).unwrap();
        let frame = build_request_frame(FrameType::Type3E, &AccessRoute::default(), 0x0010, &req_data, 0);
        let resp = handle_mc_request(&frame, &mem).unwrap();
        let parsed = crate::mc_frame::parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.data.len(), 12);
        for i in 0..12 {
            assert_eq!(parsed.data[i], (i % 2 == 0) as u8, "M{i}");
        }
    }

    /// E2E:地址越界 → 结束代码 D2(不 panic,与真机一致)
    #[test]
    fn e2e_out_of_range_returns_end_code() {
        let mem = setup_mem();
        // D16777215 远超虚拟内存 12288
        let addr = parse_mc_address("D16777215").unwrap();
        let req_data = build_read_batch_pdu(&addr, 1).unwrap();
        let frame = build_request_frame(FrameType::Type3E, &AccessRoute::default(), 0x0010, &req_data, 0);
        let resp = handle_mc_request(&frame, &mem).unwrap();
        let parsed = crate::mc_frame::parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.end_code, 0x00D2);
    }

    /// E2E:未实现指令(如 0x0201 扩展访问)→ 结束代码 0007
    #[test]
    fn e2e_unknown_command_returns_0007() {
        let mem = setup_mem();
        // 手工构造指令 0x0201 的请求(未实现)
        let req_data = [0x01, 0x02, 0x00, 0x00];
        let frame = build_request_frame(FrameType::Type3E, &AccessRoute::default(), 0x0010, &req_data, 0);
        let resp = handle_mc_request(&frame, &mem).unwrap();
        let parsed = crate::mc_frame::parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.end_code, 0x0007);
    }

    /// E2E:多字读写回环(写 3 个字再读回)
    #[test]
    fn e2e_write_read_words_roundtrip() {
        let mem = setup_mem();
        let addr = parse_mc_address("D500").unwrap();

        let req_data = build_write_batch_pdu(&addr, &[0xCAFE, 0xBABE, 0x00FF]).unwrap();
        let frame = build_request_frame(FrameType::Type3E, &AccessRoute::default(), 0x0010, &req_data, 0);
        let resp = handle_mc_request(&frame, &mem).unwrap();
        assert_eq!(crate::mc_frame::parse_response_frame(&resp).unwrap().end_code, 0x0000);

        let req_data = build_read_batch_pdu(&addr, 3).unwrap();
        let frame = build_request_frame(FrameType::Type3E, &AccessRoute::default(), 0x0010, &req_data, 0);
        let resp = handle_mc_request(&frame, &mem).unwrap();
        let parsed = crate::mc_frame::parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.data, vec![0xFE, 0xCA, 0xBE, 0xBA, 0xFF, 0x00]);
    }
}

// =============================================================================
// A-1E / SLMP-1E 帧处理(§3.4):识别 + 内存读写 + 响应构建
// =============================================================================

/// 判断是否为 1E 请求帧:首字节 0x00~0x03(命令)且次字节 0xFF(PC号)。
/// 与 3E(50 00)/ASCII("5000")天然区分。
pub fn is_1e_request(frame: &[u8]) -> bool {
    frame.len() >= 2 && frame[0] <= 0x03 && frame[1] == 0xFF
}

/// 处理 1E 请求帧,生成 1E 响应帧(`81 <结束码> [数据]`)。
/// 返回 Err = 帧损坏;业务错误写进结束代码。
pub fn handle_1e_request(frame: &[u8], memory: &Arc<Mutex<McSlaveMemory>>) -> Result<Vec<u8>, CoreError> {
    use crate::mc_1e::*;
    if frame.len() < 10 {
        return Err(CoreError::Modbus {
            code: "MC_1E_FRAME_TOO_SHORT",
            message: format!("1E 请求 {} 字节,短于最小 10", frame.len()),
            details: None,
        });
    }
    let cmd = frame[0];
    let watchdog = u16::from_le_bytes([frame[2], frame[3]]);
    let _ = watchdog;
    let head = u32::from_le_bytes([frame[4], frame[5], frame[6], frame[7]]);
    let code_str = &frame[8..10];
    let points = u16::from_le_bytes([frame[10], frame[11]]);

    // 1E 软元件 ASCII 代号 → 3E 二进制代码(用 mc_address 表反查)
    let prefix = match std::str::from_utf8(code_str) {
        Ok(s) => s.trim_end_matches('*').to_uppercase(),
        Err(_) => return Ok(onee_error(0x50)), // 软元件代号错误
    };
    let spec = match crate::mc_address::device_spec(&prefix) {
        Some(s) => s,
        None => return Ok(onee_error(0x50)),
    };

    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    match cmd {
        CMD1E_BIT_READ => {
            let bits = mem.get_bits(spec.code, head, points);
            match bits {
                Ok(vals) => {
                    // 位打包:每 8 点 1 字节,bit i = 第 i/8 字节第 i%8 位
                    let nbytes = (points as usize + 7) / 8;
                    let mut data = vec![0u8; nbytes];
                    for (i, v) in vals.iter().enumerate() {
                        if *v != 0 { data[i / 8] |= 1 << (i % 8); }
                    }
                    let mut resp = vec![0x81, 0x00];
                    resp.extend_from_slice(&data);
                    Ok(resp)
                }
                Err(_) => Ok(onee_error(0x5B)),
            }
        }
        CMD1E_WORD_READ => {
            let words = mem.get_words(spec.code, head, points);
            match words {
                Ok(vals) => {
                    let mut resp = vec![0x81, 0x00];
                    for v in vals {
                        resp.extend_from_slice(&v.to_le_bytes());
                    }
                    Ok(resp)
                }
                Err(_) => Ok(onee_error(0x5B)),
            }
        }
        CMD1E_BIT_WRITE | CMD1E_WORD_WRITE => {
            let payload = &frame[12..];
            let ok = if cmd == CMD1E_BIT_WRITE {
                let expected = (points as usize + 7) / 8;
                if payload.len() < expected { return Ok(onee_error(0x40)); }
                let vals: Vec<u16> = (0..points as usize)
                    .map(|i| ((payload[i / 8] >> (i % 8)) & 1) as u16)
                    .collect();
                mem.set_bits(spec.code, head, &vals).is_ok()
            } else {
                let expected = points as usize * 2;
                if payload.len() < expected { return Ok(onee_error(0x40)); }
                let vals: Vec<u16> = (0..points as usize)
                    .map(|i| u16::from_le_bytes([payload[i * 2], payload[i * 2 + 1]]))
                    .collect();
                mem.set_words(spec.code, head, &vals).is_ok()
            };
            if ok { Ok(vec![0x81, 0x00]) } else { Ok(onee_error(0x5B)) }
        }
        _ => Ok(onee_error(0x40)), // 命令错误
    }
}

fn onee_error(code: u8) -> Vec<u8> {
    if code == 0x5B { vec![0x81, 0x5B, 0x10, 0x00] } else { vec![0x81, code] }
}
