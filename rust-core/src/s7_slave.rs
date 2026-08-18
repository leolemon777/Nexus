//! 西门子 S7comm 虚拟服务端(自测 + 演示用,模拟 S7 CPU 的 TCP 102 行为)。
//!
//! 实现范围(S1):COTP CR→CC、Setup Communication 协商、Read Var / Write Var。
//! 响应字节遵循「响应侧 TransportSize 编码」与奇数填充规则,
//! 对拍基准:python-snap7 3.0 客户端(纯 Python)与 snap7 serverdemo 行为。
//!
//! 内存模型:M/I/Q 各 64KB;DB 按号懒创建 64KB;Timer/Counter 各 2048 个 16 位。

use std::collections::HashMap;
use std::io::Write;
use std::net::TcpStream;
use std::sync::{Arc, Mutex};

use crate::error::CoreError;
use crate::s7_address::{area, S7Kind};
use crate::s7_cotp::{
    frame_to_pdu, read_tpkt_frame, unwrap_tpkt, wrap_dt, write_frame, COTP_CC, DEFAULT_SRC_REF,
};
use crate::s7_pdu::{
    parse_ack, parse_setup_response, ROSCTR_ACK_DATA, ROSCTR_USERDATA, FUN_READ, FUN_SETUP,
    FUN_WRITE, MAX_ITEMS, S7Item,
};

/// 每区容量(字节)。
const AREA_SIZE: usize = 64 * 1024;
/// Timer/Counter 数量。
const TC_COUNT: usize = 2048;
/// 虚拟 CPU 的 PDU 上限(模拟 S7-1500)。
pub const SLAVE_PDU_LIMIT: u16 = 480;

/// 虚拟 S7 内存。
#[derive(Default)]
pub struct S7SlaveMemory {
    pub m: Vec<u8>,
    pub i: Vec<u8>,
    pub q: Vec<u8>,
    /// DB 号 → 64KB(懒创建)
    pub dbs: HashMap<u16, Vec<u8>>,
    pub timers: Vec<u8>,
    pub counters: Vec<u8>,
    /// S7-200 家族:SM/AI/AQ(64KB 线性)
    pub sm: Vec<u8>,
    pub ai: Vec<u8>,
    pub aq: Vec<u8>,
}

impl S7SlaveMemory {
    pub fn new() -> Self {
        Self {
            m: vec![0; AREA_SIZE],
            i: vec![0; AREA_SIZE],
            q: vec![0; AREA_SIZE],
            dbs: HashMap::new(),
            timers: vec![0; TC_COUNT * 2],
            counters: vec![0; TC_COUNT * 2],
            sm: vec![0; AREA_SIZE],
            ai: vec![0; AREA_SIZE],
            aq: vec![0; AREA_SIZE],
        }
    }

    fn area_slice(&mut self, area_code: u8, db: u16) -> Option<&mut Vec<u8>> {
        match area_code {
            area::MARKERS => Some(&mut self.m),
            area::INPUTS => Some(&mut self.i),
            area::OUTPUTS => Some(&mut self.q),
            area::DB => Some(self.dbs.entry(db).or_insert_with(|| vec![0; AREA_SIZE])),
            crate::s7_address::area::SYS_FLAGS_SM => Some(&mut self.sm),
            crate::s7_address::area::ANALOG_INPUT_AI => Some(&mut self.ai),
            crate::s7_address::area::ANALOG_OUTPUT_AQ => Some(&mut self.aq),
            _ => None,
        }
    }

    /// 读 n 字节。越界返回 None(→ Item RC 0x05)。
    pub fn read_area_bytes(&mut self, area_code: u8, db: u16, start: u32, n: usize) -> Option<Vec<u8>> {
        let buf = self.area_slice(area_code, db)?;
        let start = start as usize;
        if start + n > buf.len() {
            return None;
        }
        Some(buf[start..start + n].to_vec())
    }

    pub fn write_area_bytes(&mut self, area_code: u8, db: u16, start: u32, data: &[u8]) -> Option<()> {
        let buf = self.area_slice(area_code, db)?;
        let start = start as usize;
        if start + data.len() > buf.len() {
            return None;
        }
        buf[start..start + data.len()].copy_from_slice(data);
        Some(())
    }

    /// Timer/Counter:读 count 个 16 位值(Address=编号)。
    pub fn read_tc(&self, area_code: u8, index: u32, count: u16) -> Option<Vec<u8>> {
        let buf = if area_code == area::TIMER { &self.timers } else { &self.counters };
        let start = index as usize * 2;
        let len = count as usize * 2;
        if start + len > buf.len() {
            return None;
        }
        Some(buf[start..start + len].to_vec())
    }

    pub fn write_tc(&mut self, area_code: u8, index: u32, data: &[u8]) -> Option<()> {
        let buf = if area_code == area::TIMER { &mut self.timers } else { &mut self.counters };
        let start = index as usize * 2;
        if start + data.len() > buf.len() {
            return None;
        }
        buf[start..start + data.len()].copy_from_slice(data);
        Some(())
    }

    /// 外设区(0x80):读映像区、写丢弃(模拟只读外设?实际 PQ 可写——按区域字母映射,
    /// 这里简化:外设读返回 I/Q 映像,写同 Q)。Area=0x80 无法区分 PI/PQ → 读 I 写 Q。
    pub fn read_peripheral(&mut self, start: u32, n: usize) -> Option<Vec<u8>> {
        let start = start as usize;
        if start + n > self.i.len() {
            return None;
        }
        Some(self.i[start..start + n].to_vec())
    }
}

/// 演示数据(seed):DB1.DBD0=0x12345678、DB1.DBD4=0x0A0B0C0D、DB1.DBW8=0xBEEF、
/// MW0=0x1234、MD4=0x0000BEEF? 简化:MW0=0x1234、M10=0x55(位交替)、IW0=0x1111、QW0=0x2222、
/// T0 = 0x2510(S5TIME)、C0 = 0x0005。
pub fn seed_demo(mem: &mut S7SlaveMemory) {
    mem.dbs.insert(1, vec![0; AREA_SIZE]);
    let db1 = mem.dbs.get_mut(&1).unwrap();
    db1[0..4].copy_from_slice(&[0x12, 0x34, 0x56, 0x78]);
    db1[4..8].copy_from_slice(&[0x0A, 0x0B, 0x0C, 0x0D]);
    db1[8..10].copy_from_slice(&[0xBE, 0xEF]);
    mem.m[0..2].copy_from_slice(&[0x12, 0x34]);
    mem.m[10] = 0x55; // M10.0-10.6 交替
    mem.i[0..2].copy_from_slice(&[0x11, 0x11]);
    mem.q[0..2].copy_from_slice(&[0x22, 0x22]);
    mem.timers[0..2].copy_from_slice(&[0x25, 0x10]);
    mem.counters[0..2].copy_from_slice(&[0x00, 0x05]);
    // 200 家族:SMB1=0x55(位交替)、AIW0=0x1234、AQW0=0x5678
    mem.sm[1] = 0x55;
    mem.ai[0..2].copy_from_slice(&[0x12, 0x34]);
    mem.aq[0..2].copy_from_slice(&[0x56, 0x78]);
}

fn s7_err(code: &'static str, msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code, message: msg.into(), details: None }
}

/// 解析任意 S7ANY 请求项(12 字节)为 S7Item —— 从站侧需要接受
/// python-snap7/snap7/标准客户端发来的任意合法 TransportSize。
fn decode_any_item(bytes: &[u8; 12]) -> Result<S7Item, CoreError> {
    if bytes[0] != 0x12 || bytes[1] != 0x0A || bytes[2] != 0x10 {
        return Err(s7_err("S7_SLAVE_ITEM", "不支持的地址项(非 S7ANY 0x12/0x0A/0x10)"));
    }
    let ts = bytes[3];
    let count = u16::from_be_bytes([bytes[4], bytes[5]]);
    let db = u16::from_be_bytes([bytes[6], bytes[7]]);
    let area_code = bytes[8];
    let linear = u32::from_be_bytes([0, bytes[9], bytes[10], bytes[11]]);

    let kind = match ts {
        0x01 => S7Kind::Bit,
        0x02 => S7Kind::Byte,
        0x03 => S7Kind::Byte, // CHAR
        0x04 => S7Kind::Word,
        0x05 => S7Kind::Word, // INT
        0x06 => S7Kind::Dword,
        0x07 => S7Kind::Dword, // DINT
        0x08 => S7Kind::Dword, // REAL
        0x09 => S7Kind::Byte,  // DATE/OCTET?请求侧少见
        0x1C => S7Kind::Counter,
        0x1D => S7Kind::Timer,
        _ => return Err(s7_err("S7_SLAVE_ITEM", format!("不支持请求侧 TS=0x{ts:02X}"))),
    };
    let byte = if matches!(kind, S7Kind::Timer | S7Kind::Counter) { linear } else { linear >> 3 };
    let bit = (linear & 7) as u8;
    Ok(S7Item {
        addr: crate::s7_address::S7Address { area: area_code, db, byte, bit, kind },
        count,
    })
}

/// 处理一条已剥壳的 S7 PDU(请求),返回响应 PDU。
pub fn handle_s7_request(pdu: &[u8], mem: &Arc<Mutex<S7SlaveMemory>>) -> Vec<u8> {
    let req = match parse_ack(pdu) {
        Ok(r) => r,
        Err(e) => return error_ack(0, &e),
    };
    // Userdata(0x07)请求:SZL/密码(param 头 00 01 12 04 11 44/45)
    if req.rosctr == ROSCTR_USERDATA {
        return handle_userdata(&req);
    }
    match req.param.first().copied() {
        Some(FUN_SETUP) => handle_setup(&req),
        Some(0x29) => handle_cpu_control(&req, false),
        Some(0x28) => handle_cpu_control(&req, true),
        Some(FUN_READ) => handle_read(&req, mem),
        Some(FUN_WRITE) => handle_write(&req, mem),
        // 0x8104 = 功能不支持(deep-dive §4.4 Code7FunNotAvailable)
        Some(other) => ack_header(req.pdu_ref, 2, 0, &0x8104u16.to_be_bytes(), &[other, 0x00]),
        None => error_ack(req.pdu_ref, &s7_err("S7_SLAVE_FUN", "空参数区")),
    }
}

fn ack_header(pdu_ref: u16, param_len: usize, data_len: usize, error: &[u8], param: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(12 + param_len + data_len);
    out.extend_from_slice(&[0x32, ROSCTR_ACK_DATA]);
    out.extend_from_slice(&0x0000u16.to_be_bytes());
    out.extend_from_slice(&pdu_ref.to_be_bytes());
    out.extend_from_slice(&(param_len as u16).to_be_bytes());
    out.extend_from_slice(&(data_len as u16).to_be_bytes());
    out.extend_from_slice(error);
    out.extend_from_slice(param);
    out
}

fn error_ack(pdu_ref: u16, e: &CoreError) -> Vec<u8> {
    let msg = match e {
        CoreError::Modbus { message, .. } => message.clone(),
        other => other.to_string(),
    };
    let param = vec![0x00u8];
    let mut out = ack_header(pdu_ref, 1, msg.len(), &0x8500u16.to_be_bytes(), &param);
    out.extend_from_slice(msg.as_bytes());
    out
}

/// CPU 控制(Stop 0x29 / Start 0x28):回 Ack_Data + para。
/// 虚拟 CPU 状态机:默认 RUN;控制结果 0x00=成功(不追踪真实状态切换,S1 从站够用)。
fn handle_cpu_control(req: &crate::s7_pdu::S7Ack, _is_start: bool) -> Vec<u8> {
    // golden(deep-dive §6.2 Stop 响应):param_len=1(仅回显 Fun),data_len=0
    let fun = *req.param.first().unwrap_or(&0);
    ack_header(req.pdu_ref, 1, 0, &[0x00, 0x00], &[fun])
}

/// Userdata:SZL(0x44)/密码(0x45)。
fn handle_userdata(req: &crate::s7_pdu::S7Ack) -> Vec<u8> {
    // param: 00 01 12 04 11 <Tg> <序号> <保留>
    let tg = req.param.get(5).copied().unwrap_or(0);
    match tg {
        0x44 => handle_szl(req),
        0x45 => {
            // 密码:虚拟 CPU 无保护 → 直接接受(数据区 Ret=FF)
            let param = [0x00u8, 0x01, 0x12, 0x08, 0x12, 0x85, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00];
            let data = [0xFFu8, 0x09, 0x00, 0x00];
            let mut out = ack_header(req.pdu_ref, param.len(), data.len(), &[0x00, 0x00], &param);
            out.extend_from_slice(&data);
            out
        }
        _ => ack_header(req.pdu_ref, 2, 0, &0x8104u16.to_be_bytes(), &[tg, 0x00]),
    }
}

/// SZL 0x0424(CPU 模式)→ RUN;其它 SZL → 不支持(0x8104)。
fn handle_szl(req: &crate::s7_pdu::S7Ack) -> Vec<u8> {
    // 请求 data: FF 09 00 04 <ID 2B> <Index 2B>
    let szl_id = if req.data.len() >= 6 {
        u16::from_be_bytes([req.data[4], req.data[5]])
    } else {
        0
    };
    if szl_id != 0x0424 {
        let param = [0x00u8, 0x01, 0x12, 0x08, 0x12, 0x84, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00];
        return ack_header(req.pdu_ref, param.len(), 0, &0x8104u16.to_be_bytes(), &param);
    }
    // golden 结构(§6.4 TIA 抓包):param 12B + data = FF 09 00 1C + (04 24 00 00 00 14 00 01 + 20B 记录)
    let param = [0x00u8, 0x01, 0x12, 0x08, 0x12, 0x84, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00];
    let mut record = vec![0x51, 0x44, 0xFF, 0x08]; // 记录头 + 状态字节 0x08 = RUN
    record.extend_from_slice(&[0u8; 16]); // 补足 20B 记录
    let mut data = vec![0xFFu8, 0x09, 0x00, 0x1C];
    data.extend_from_slice(&[0x04, 0x24, 0x00, 0x00, 0x00, 0x14, 0x00, 0x01]);
    data.extend_from_slice(&record);
    let mut out = ack_header(req.pdu_ref, param.len(), data.len(), &[0x00, 0x00], &param);
    out.extend_from_slice(&data);
    out
}

fn handle_setup(req: &crate::s7_pdu::S7Ack) -> Vec<u8> {
    let Ok((amq1, amq2, requested)) = parse_setup_response(req) else {
        return error_ack(req.pdu_ref, &s7_err("S7_SLAVE_SETUP", "Setup 请求格式无效"));
    };
    let agreed = requested.min(SLAVE_PDU_LIMIT).max(64);
    let mut out = ack_header(req.pdu_ref, 8, 0, &[0x00, 0x00], &[FUN_SETUP, 0x00]);
    out.extend_from_slice(&amq1.to_be_bytes());
    out.extend_from_slice(&amq2.to_be_bytes());
    out.extend_from_slice(&agreed.to_be_bytes());
    out
}

fn handle_read(req: &crate::s7_pdu::S7Ack, mem: &Arc<Mutex<S7SlaveMemory>>) -> Vec<u8> {
    let item_count = *req.param.get(1).unwrap_or(&0) as usize;
    if item_count == 0 || item_count > MAX_ITEMS {
        return ack_header(req.pdu_ref, 2, 0, &0x8700u16.to_be_bytes(), &[FUN_READ, item_count as u8]);
    }
    // #12: 超过协商 PDU 上限 → 0x8500(与真机一致,而非静默返回)
    let total_data: usize = (2..item_count * 12 + 2).step_by(12)
        .map(|off| {
            let ts = req.param.get(off + 3).copied().unwrap_or(0x02);
            let cnt = u16::from_be_bytes([
                req.param.get(off + 4).copied().unwrap_or(0),
                req.param.get(off + 5).copied().unwrap_or(0),
            ]);
            match ts { 0x01 => ((cnt as usize) + 7) / 8, _ => cnt as usize * 2 }
        })
        .sum();
    let est_response = 12 + 2 + item_count * (4 + 1) + total_data;
    if est_response > SLAVE_PDU_LIMIT as usize {
        return ack_header(req.pdu_ref, 2, 0, &0x8500u16.to_be_bytes(), &[FUN_READ, item_count as u8]);
    }
    let mut data_sec: Vec<u8> = Vec::new();
    let mut memory = mem.lock().unwrap_or_else(|e| e.into_inner());
    for i in 0..item_count {
        let off = 2 + i * 12;
        if off + 12 > req.param.len() {
            return ack_header(req.pdu_ref, 2, 0, &0x8700u16.to_be_bytes(), &[FUN_READ, item_count as u8]);
        }
        let mut raw = [0u8; 12];
        raw.copy_from_slice(&req.param[off..off + 12]);
        let item = match decode_any_item(&raw) {
            Ok(it) => it,
            Err(_) => {
                push_read_item(&mut data_sec, 0xD2, &[], i + 1 < item_count);
                continue;
            }
        };
        let read_result: Option<Vec<u8>> = match item.addr.kind {
            S7Kind::Timer | S7Kind::Counter => {
                memory.read_tc(item.addr.area, item.addr.byte, item.count)
            }
            S7Kind::Bit => {
                // 位串:从起始位逐位读并按位打包(每 8 位 1 字节)
                let mut packed = vec![0u8; (item.count as usize + 7) / 8];
                let mut ok = true;
                for b in 0..item.count as usize {
                    let abs = item.addr.byte as usize * 8 + item.addr.bit as usize + b;
                    match memory.read_area_bytes(item.addr.area, item.addr.db, (abs / 8) as u32, 1) {
                        Some(v) if v[0] >> (abs % 8) & 1 == 1 => packed[b / 8] |= 1 << (b % 8),
                        Some(_) => {}
                        None => {
                            ok = false;
                            break;
                        }
                    }
                }
                ok.then_some(packed)
            }
            _ => {
                memory.read_area_bytes(item.addr.area, item.addr.db, item.addr.byte, item.data_bytes())
            }
        };
        match read_result {
            Some(data) => {
                // 响应侧 TS 编码(deep-dive §4.1):
                // Bit → 0x03 len=位数;字节流 → 0x04 len=字节*8;T/C → 0x09 len=字节
                let (ts, len) = match item.addr.kind {
                    S7Kind::Bit => (0x03u8, item.count),
                    S7Kind::Timer | S7Kind::Counter => (0x09u8, data.len() as u16),
                    _ => (0x04u8, (data.len() as u16) * 8),
                };
                data_sec.push(0xFF);
                data_sec.push(ts);
                data_sec.extend_from_slice(&len.to_be_bytes());
                let pad = item_count > i + 1 && data.len() % 2 != 0;
                data_sec.extend_from_slice(&data);
                if pad {
                    data_sec.push(0x00);
                }
            }
            None => {
                push_read_item(&mut data_sec, 0x05, &[], i + 1 < item_count);
            }
        }
    }
    // Item 级错误通过 RC 传递,头级 Error Code 保持 0(与真机一致)
    let mut out = ack_header(req.pdu_ref, 2, data_sec.len(), &[0x00, 0x00], &[FUN_READ, item_count as u8]);
    out.extend_from_slice(&data_sec);
    out
}

fn push_read_item(data_sec: &mut Vec<u8>, rc: u8, data: &[u8], pad: bool) {
    data_sec.push(rc);
    data_sec.push(0x04);
    data_sec.extend_from_slice(&((data.len() as u16) * 8).to_be_bytes());
    data_sec.extend_from_slice(data);
    if pad && data.len() % 2 != 0 {
        data_sec.push(0x00);
    }
}

fn handle_write(req: &crate::s7_pdu::S7Ack, mem: &Arc<Mutex<S7SlaveMemory>>) -> Vec<u8> {
    let item_count = *req.param.get(1).unwrap_or(&0) as usize;
    if item_count == 0 || item_count > MAX_ITEMS {
        return ack_header(req.pdu_ref, 2, 0, &0x8700u16.to_be_bytes(), &[FUN_WRITE, item_count as u8]);
    }
    // 先解码全部地址项
    let mut items = Vec::with_capacity(item_count);
    for i in 0..item_count {
        let off = 2 + i * 12;
        if off + 12 > req.param.len() {
            return ack_header(req.pdu_ref, 2, 0, &0x8700u16.to_be_bytes(), &[FUN_WRITE, item_count as u8]);
        }
        let mut raw = [0u8; 12];
        raw.copy_from_slice(&req.param[off..off + 12]);
        match decode_any_item(&raw) {
            Ok(it) => items.push(it),
            Err(_) => {
                return ack_header(req.pdu_ref, 2, 1, &0xD209u16.to_be_bytes(), &[FUN_WRITE, item_count as u8])
            }
        }
    }
    // 再按数据区逐项写入
    let mut rcs = Vec::with_capacity(item_count);
    let mut off = 0usize;
    let mut memory = mem.lock().unwrap_or_else(|e| e.into_inner());
    for (i, item) in items.iter().enumerate() {
        if off + 4 > req.data.len() {
            rcs.push(0x07); // 数据类型不一致(长度不足)
            break;
        }
        let ts = req.data[off + 1];
        let length = u16::from_be_bytes([req.data[off + 2], req.data[off + 3]]) as u32;
        off += 4;
        let byte_len = match ts {
            0x03 | 0x04 | 0x05 => (length + 7) / 8,
            _ => length,
        } as usize;
        if off + byte_len > req.data.len() {
            rcs.push(0x07);
            break;
        }
        let data = &req.data[off..off + byte_len];
        off += byte_len;
        if i + 1 < item_count && byte_len % 2 != 0 {
            off += 1; // 奇数填充
        }

        let written = match item.addr.kind {
            S7Kind::Timer | S7Kind::Counter => memory.write_tc(item.addr.area, item.addr.byte, data),
            S7Kind::Bit => {
                // S7 位写语义:只写指定位(读-改-写);data 每字节 = 1 个位的值。
                // 逐位处理(位串通常很小),避免缓存字节的借用复杂度。
                let mut ok = true;
                for b in 0..item.count as usize {
                    let abs = item.addr.byte as usize * 8 + item.addr.bit as usize + b;
                    match memory.read_area_bytes(item.addr.area, item.addr.db, (abs / 8) as u32, 1) {
                        Some(mut v) => {
                            let mask = 1u8 << (abs % 8);
                            if data.get(b).copied().unwrap_or(0) != 0 {
                                v[0] |= mask;
                            } else {
                                v[0] &= !mask;
                            }
                            if memory
                                .write_area_bytes(item.addr.area, item.addr.db, (abs / 8) as u32, &v)
                                .is_none()
                            {
                                ok = false;
                                break;
                            }
                        }
                        None => {
                            ok = false;
                            break;
                        }
                    }
                }
                ok.then_some(())
            }
            _ => memory.write_area_bytes(item.addr.area, item.addr.db, item.addr.byte, data),
        };
        rcs.push(if written.is_some() { 0xFF } else { 0x05 });
    }
    let mut out = ack_header(req.pdu_ref, 2, rcs.len(), &[0x00, 0x00], &[FUN_WRITE, item_count as u8]);
    out.extend_from_slice(&rcs);
    out
}

/// 单连接处理循环:CR→CC 握手后进入数据阶段。
pub fn handle_s7_client(
    mut stream: TcpStream,
    memory: Arc<Mutex<S7SlaveMemory>>,
    running: Arc<Mutex<bool>>,
) {
    // Windows 上 accept 出的 stream 继承 listener 的非阻塞模式——必须显式切回阻塞,
    // 否则客户端数据未到时 read 立即返回 WSAEWOULDBLOCK(10035) 被误判为连接失败。
    let _ = stream.set_nonblocking(false);
    // === 握手:读 CR,回 CC ===
    let first = match read_tpkt_frame(&mut stream) {
        Ok(f) => f,
        Err(_) => return,
    };
    let cotp = match unwrap_tpkt(&first) {
        Ok(c) => c,
        Err(_) => return,
    };
    if cotp.len() < 7 || (cotp[1] & 0xF0) != COTP_CC && (cotp[1] & 0xF0) != 0xE0 {
        let _ = stream.write_all(&[0x03, 0x00, 0x0B, 0x02, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        return;
    }
    // CC:回显 CR 参数(TPDU size/TSAP),SrcRef=DEFAULT_SRC_REF
    let li = cotp[0] as usize;
    if cotp.len() < 7 || li < 6 {
        // LI < 6 意味着无参数区(畸形/扫描器探针)——直接返回,不 panic
        let _ = stream.write_all(&[0x03, 0x00, 0x0B, 0x02, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        return;
    }
    let params = &cotp[7..(li + 1).min(cotp.len())];
    let mut cc_cotp = Vec::with_capacity(7 + params.len());
    cc_cotp.push((7 + params.len()) as u8);
    cc_cotp.push(COTP_CC);
    cc_cotp.extend_from_slice(&cotp[4..6]); // DST-REF = CR 的 SRC-REF(ISO 8073 对调规则)
    cc_cotp.extend_from_slice(&DEFAULT_SRC_REF.to_be_bytes());
    cc_cotp.push(0x00);
    cc_cotp.extend_from_slice(params);
    let mut cc_frame = Vec::with_capacity(4 + cc_cotp.len());
    cc_frame.extend_from_slice(&[0x03, 0x00]);
    cc_frame.extend_from_slice(&((4 + cc_cotp.len()) as u16).to_be_bytes());
    cc_frame.extend_from_slice(&cc_cotp);
    if write_frame(&mut stream, &cc_frame).is_err() {
        return;
    }

    // === 数据阶段 ===
    while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        let _ = stream.set_read_timeout(Some(std::time::Duration::from_millis(200)));
        let frame = match read_tpkt_frame(&mut stream) {
            Ok(f) => f,
            Err(CoreError::Modbus { code, .. }) if code == "S7_READ_FAILED" => continue,
            Err(_) => break,
        };
        let pdu = match frame_to_pdu(&frame) {
            Ok(p) => p,
            Err(_) => break,
        };
        let resp = handle_s7_request(pdu, &memory);
        if write_frame(&mut stream, &wrap_dt(&resp)).is_err() {
            break;
        }
    }
}


#[cfg(test)]
mod tests {
    use super::*;
    use crate::s7_cotp::{build_cr, parse_cc, ConnectionType};
    use crate::s7_pdu::build_setup_request;

    #[test]
    fn setup_negotiation_min_with_limit() {
        let mem = Arc::new(Mutex::new(S7SlaveMemory::new()));
        let req = build_setup_request(0x0005, 1, 1, 960);
        let resp = handle_s7_request(&req, &mem);
        let ack = parse_ack(&resp).unwrap();
        let (_, _, pdu) = parse_setup_response(&ack).unwrap();
        assert_eq!(pdu, SLAVE_PDU_LIMIT); // min(960, 480) = 480
    }

    #[test]
    fn read_db_seed_roundtrip() {
        let mut mem = S7SlaveMemory::new();
        seed_demo(&mut mem);
        assert_eq!(mem.read_area_bytes(area::DB, 1, 0, 4).unwrap(), vec![0x12, 0x34, 0x56, 0x78]);
        assert_eq!(mem.read_area_bytes(area::MARKERS, 0, 0, 2).unwrap(), vec![0x12, 0x34]);
        assert_eq!(mem.read_tc(area::TIMER, 0, 1).unwrap(), vec![0x25, 0x10]);
        assert_eq!(mem.read_tc(area::COUNTER, 0, 1).unwrap(), vec![0x00, 0x05]);
        // 越界
        assert!(mem.read_area_bytes(area::DB, 1, (AREA_SIZE - 2) as u32, 4).is_none());
    }

    #[test]
    fn full_read_flow_bit_length_encoding() {
        let mut m = S7SlaveMemory::new();
        seed_demo(&mut m);
        let mem = Arc::new(Mutex::new(m));
        // python-snap7 风格:read_area(DB1, 0, 4 字节, TS=BYTE)
        let items = [S7Item::new("DB1.DBB0", 4).unwrap()];
        let req = crate::s7_pdu::build_read_request(0x0002, &items).unwrap();
        let resp_pdu = handle_s7_request(&req, &mem);
        // 剥壳后应能被 parse_read_response 解回
        let ack = parse_ack(&resp_pdu).unwrap();
        assert_eq!(ack.error, 0);
        let parsed = crate::s7_pdu::parse_read_response(&ack).unwrap();
        assert_eq!(parsed[0].data, vec![0x12, 0x34, 0x56, 0x78]);
    }

    #[test]
    fn full_write_flow_and_readback() {
        let m = S7SlaveMemory::new();
        let mem = Arc::new(Mutex::new(m));
        let items = [S7Item::new("MW20", 1).unwrap()]; // count=元素数:1 个字=2 字节
        let data = [vec![0xABu8, 0xCD]];
        let req = crate::s7_pdu::build_write_request(0x0001, &items, &data).unwrap();
        let resp = handle_s7_request(&req, &mem);
        let ack = parse_ack(&resp).unwrap();
        assert_eq!(crate::s7_pdu::parse_write_response(&ack).unwrap(), vec![0xFF]);
        assert_eq!(mem.lock().unwrap_or_else(|e| e.into_inner()).read_area_bytes(area::MARKERS, 0, 20, 2).unwrap(), vec![0xAB, 0xCD]);
    }

    #[test]
    fn word_transport_size_accepted_from_python_snap7_style() {
        // python-snap7/S7.Net 可能用 TS=WORD(0x04)+元素数 发请求——从站须接受
        let mem = Arc::new(Mutex::new({
            let mut m = S7SlaveMemory::new();
            seed_demo(&mut m);
            m
        }));
        let mut req = crate::s7_pdu::build_read_request(1, &[S7Item::new("DB1.DBB0", 4).unwrap()]).unwrap();
        req[12 + 3] = 0x04; // TS BYTE→WORD(item 自 offset 12 起)
        req[12 + 4..12 + 6].copy_from_slice(&2u16.to_be_bytes()); // count=2 word 元素
        let resp = handle_s7_request(&req, &mem);
        let ack = parse_ack(&resp).unwrap();
        let parsed = crate::s7_pdu::parse_read_response(&ack).unwrap();
        assert_eq!(parsed[0].data, vec![0x12, 0x34, 0x56, 0x78]);
    }

    #[test]
    fn bit_write_read_modify_write_semantics() {
        let m = S7SlaveMemory::new();
        let mem = Arc::new(Mutex::new(m));
        // 先写 MB10 = 0x00,再写 M10.3 = 1 → MB10 应为 0x08
        let items0 = [S7Item::new("MB10", 1).unwrap()];
        let resp = handle_s7_request(
            &crate::s7_pdu::build_write_request(1, &items0, &[vec![0x00]]).unwrap(),
            &mem,
        );
        assert!(parse_write_response_checked(&resp));

        let bit_item = [S7Item::new("M10.3", 1).unwrap()];
        let resp = handle_s7_request(
            &crate::s7_pdu::build_write_request(2, &bit_item, &[vec![0x01]]).unwrap(),
            &mem,
        );
        assert!(parse_write_response_checked(&resp));
        assert_eq!(mem.lock().unwrap_or_else(|e| e.into_inner()).m[10], 0x08);
    }

    fn parse_write_response_checked(resp: &[u8]) -> bool {
        let ack = parse_ack(resp).unwrap();
        *crate::s7_pdu::parse_write_response(&ack).unwrap().first().unwrap() == 0xFF
    }

    #[test]
    fn smart_v_area_accessible_as_db1() {
        let mem = Arc::new(Mutex::new({
            let mut m = S7SlaveMemory::new();
            seed_demo(&mut m);
            m
        }));
        // SMART VW100 = DB1.DBW100:写入后读回
        let items = [S7Item::new("VW100", 1).unwrap()]; // 1 个字
        let req = crate::s7_pdu::build_write_request(1, &items, &[vec![0xCA, 0xFE]]).unwrap();
        handle_s7_request(&req, &mem);
        let read = handle_s7_request(&crate::s7_pdu::build_read_request(2, &items).unwrap(), &mem);
        let ack = parse_ack(&read).unwrap();
        let parsed = crate::s7_pdu::parse_read_response(&ack).unwrap();
        assert_eq!(parsed[0].data, vec![0xCA, 0xFE]);
    }

    #[test]
    fn timer_read_via_any_item() {
        let mem = Arc::new(Mutex::new({
            let mut m = S7SlaveMemory::new();
            seed_demo(&mut m);
            m
        }));
        let items = [S7Item::new("T0", 1).unwrap()];
        let req = crate::s7_pdu::build_read_request(1, &items).unwrap();
        let resp = handle_s7_request(&req, &mem);
        let ack = parse_ack(&resp).unwrap();
        let parsed = crate::s7_pdu::parse_read_response(&ack).unwrap();
        assert_eq!(parsed[0].data, vec![0x25, 0x10]);
    }

    #[test]
    fn decode_any_item_maps_all_request_transport_sizes() {
        // 12 0A 10 06 00 01 00 02 84 00 00 00 → DWORD, DB2, offset 0
        let raw = [0x12, 0x0A, 0x10, 0x06, 0x00, 0x01, 0x00, 0x02, 0x84, 0x00, 0x00, 0x00];
        let item = decode_any_item(&raw).unwrap();
        assert_eq!(item.addr.db, 2);
        assert_eq!(item.addr.kind, S7Kind::Dword);
        // Timer:12 0A 10 1D 00 01 00 00 1D 00 00 05
        let t = [0x12, 0x0A, 0x10, 0x1D, 0x00, 0x01, 0x00, 0x00, 0x1D, 0x00, 0x00, 0x05];
        assert_eq!(decode_any_item(&t).unwrap().addr.byte, 5);
    }

    #[test]
    fn stop_and_szl_and_password_flow() {
        let mem = Arc::new(Mutex::new(S7SlaveMemory::new()));
        // Stop
        let stop_req = crate::s7_pdu::build_stop_job(2);
        let resp = handle_s7_request(&stop_req, &mem);
        let ack = parse_ack(&resp).unwrap();
        assert_eq!(ack.error, 0);
        assert_eq!(ack.param[0], 0x29);
        // SZL 0x0424 → RUN
        let szl_req = crate::s7_pdu::build_szl_request(3, 0x0424, 0);
        let resp = handle_s7_request(&szl_req, &mem);
        let ack = parse_ack(&resp).unwrap();
        let payload = crate::s7_pdu::parse_szl_response(&ack).unwrap();
        assert_eq!(crate::s7_pdu::szl_0424_mode(&payload), "RUN");
        // 密码 → 接受
        let pwd_req = crate::s7_pdu::build_password_job(4, "abc");
        let resp = handle_s7_request(&pwd_req, &mem);
        let ack = parse_ack(&resp).unwrap();
        assert_eq!(ack.error, 0);
        // 非 0424 的 SZL → 0x8104
        let other = crate::s7_pdu::build_szl_request(5, 0x0232, 0);
        let resp = handle_s7_request(&other, &mem);
        let ack = parse_ack(&resp).unwrap();
        assert_eq!(ack.error, 0x8104);
    }

    #[test]
    fn cc_echoes_cr_params() {
        // 连接级握手在 handle_s7_client,这里验证 CC 组帧逻辑的等价实现
        let cr = build_cr(0x0100, 0x0301, 1024);
        let cotp = unwrap_tpkt(&cr).unwrap();
        let li = cotp[0] as usize;
        let params = &cotp[7..(li + 1).min(cotp.len())];
        let mut cc = Vec::new();
        cc.push((7 + params.len()) as u8);
        cc.push(COTP_CC);
        cc.extend_from_slice(&cotp[4..6]);
        cc.extend_from_slice(&DEFAULT_SRC_REF.to_be_bytes());
        cc.push(0x00);
        cc.extend_from_slice(params);
        let info = parse_cc(&cc).unwrap();
        assert_eq!(info.tpdu_size, Some(1024));
        assert_eq!(info.dst_ref, DEFAULT_SRC_REF);
    }
}
