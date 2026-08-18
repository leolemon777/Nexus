//! Modbus 虚拟从站 —— 模拟从站设备,被动响应主站请求。
//!
//! 对标 .NET Nexus 的 `ModbusTcpSimulator` / `ModbusTcpServer`。
//! 阶段 3 实现:TCP 监听 + FC01-06、15、16 响应生成 + 内存区管理。
//!
//! 架构:`SlaveServer` 持 `TcpListener`,每客户端一线程,共享内存区(`Arc<Mutex<SlaveMemory>>`)。

use std::sync::{Arc, Mutex};
use std::net::TcpListener;
use std::io::Read;

use crate::modbus_tcp;
use crate::modbus_pdu as pdu;

/// 4 个内存区,每个 65536 项。用 Box 避免栈溢出(约 640KB)。
pub struct SlaveMemory {
    pub coils: Box<[bool; 65536]>,
    pub discrete_inputs: Box<[bool; 65536]>,
    pub holding_registers: Box<[u16; 65536]>,
    pub input_registers: Box<[u16; 65536]>,
}

impl SlaveMemory {
    pub fn new() -> Self {
        Self {
            coils: Box::new([false; 65536]),
            discrete_inputs: Box::new([false; 65536]),
            holding_registers: Box::new([0; 65536]),
            input_registers: Box::new([0; 65536]),
        }
    }

    /// 用种子数据初始化(对标 .NET ModbusTcpSimulator)。
    pub fn seed_demo(&mut self) {
        self.holding_registers[0] = 128;
        self.holding_registers[1] = 256;
        self.holding_registers[2] = 365;
        self.input_registers[0] = 1000;
        self.input_registers[1] = 2000;
        self.coils[0] = true;
        self.coils[3] = true;
        self.discrete_inputs[1] = true;
    }

    pub fn clear_area(&mut self, area: &str) {
        match area {
            "coils" | "coil" => self.coils.fill(false),
            "discrete_inputs" | "discrete" => self.discrete_inputs.fill(false),
            "holding" | "holding_registers" => self.holding_registers.fill(0),
            "input" | "input_registers" => self.input_registers.fill(0),
            _ => {}
        }
    }

    pub fn set_holding(&mut self, address: u16, values: &[u16]) {
        for (i, &v) in values.iter().enumerate() {
            let idx = address as usize + i;
            if idx < 65536 {
                self.holding_registers[idx] = v;
            }
        }
    }

    pub fn set_input_register(&mut self, address: u16, values: &[u16]) {
        for (i, &v) in values.iter().enumerate() {
            let idx = address as usize + i;
            if idx < 65536 {
                self.input_registers[idx] = v;
            }
        }
    }

    pub fn set_coil(&mut self, address: u16, values: &[bool]) {
        for (i, &v) in values.iter().enumerate() {
            let idx = address as usize + i;
            if idx < 65536 {
                self.coils[idx] = v;
            }
        }
    }

    pub fn set_discrete_input(&mut self, address: u16, values: &[bool]) {
        for (i, &v) in values.iter().enumerate() {
            let idx = address as usize + i;
            if idx < 65536 {
                self.discrete_inputs[idx] = v;
            }
        }
    }
}

impl Default for SlaveMemory {
    fn default() -> Self {
        let mut mem = Self::new();
        mem.seed_demo();
        mem
    }
}

/// 从站配置。
pub struct SlaveConfig {
    pub port: u16,
    pub allowed_station_ids: Vec<u8>,
    pub memory: Arc<Mutex<SlaveMemory>>,
}

/// 从站服务器(运行后阻塞 accept 循环)。
pub struct SlaveServer {
    config: SlaveConfig,
    listener: TcpListener,
    running: Arc<Mutex<bool>>,
    threads: Vec<std::thread::JoinHandle<()>>,
}

impl SlaveServer {
    /// 创建并绑定监听端口。不启动 accept 循环(用 `run` 启动)。
    pub fn new(config: SlaveConfig) -> Result<Self, std::io::Error> {
        let listener = TcpListener::bind(format!("127.0.0.1:{}", config.port))?;
        Ok(Self {
            config,
            listener,
            running: Arc::new(Mutex::new(false)),
            threads: Vec::new(),
        })
    }

    /// 启动 accept 循环(阻塞当前线程)。
    pub fn run(mut self) {
        *self.running.lock().unwrap_or_else(|e| e.into_inner()) = true;
        let running = self.running.clone();
        let memory = self.config.memory.clone();
        let allowed = self.config.allowed_station_ids.clone();
        let listener = self.listener;
        listener
            .set_nonblocking(true)
            .expect("listener non-blocking");
        while *running.lock().unwrap_or_else(|e| e.into_inner()) {
            match listener.accept() {
                Ok((stream, _addr)) => {
                    let mem = memory.clone();
                    let allow = allowed.clone();
                    let run_flag = running.clone();
                    let handle = std::thread::spawn(move || {
                        handle_client(stream, mem, allow, run_flag);
                    });
                    self.threads.push(handle);
                }
                Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                    std::thread::sleep(std::time::Duration::from_millis(10));
                }
                Err(_) => break,
            }
        }
    }

    /// 停止服务器。
    pub fn stop(&self) {
        *self.running.lock().unwrap_or_else(|e| e.into_inner()) = false;
    }
}

/// 处理单个客户端连接。
fn handle_client(
    mut stream: std::net::TcpStream,
    memory: Arc<Mutex<SlaveMemory>>,
    allowed: Vec<u8>,
    running: Arc<Mutex<bool>>,
) {
    use std::io::Write;
    let mut buf = [0u8; 1024];
    loop {
        if !*running.lock().unwrap_or_else(|e| e.into_inner()) {
            break;
        }
        match stream.read(&mut buf) {
            Ok(0) => break,
            Ok(n) => {
                if n < modbus_tcp::MBAP_HEADER_LEN + 1 {
                    continue;
                }
                // 解析 MBAP 头
                let (_header, request_pdu) =
                    match modbus_tcp::parse_mbap_frame(&buf[..n]) {
                        Ok(v) => v,
                        Err(_) => continue,
                    };
                let unit_id = buf[6]; // MBAP 的 unit_id
                // 站号过滤
                if !allowed.is_empty() && !allowed.contains(&unit_id) {
                    continue; // 静默丢弃
                }
                // 处理请求 PDU
                let response_pdu = handle_request(&request_pdu, &memory);
                if let Some(resp) = response_pdu {
                    let tid = u16::from_be_bytes([buf[0], buf[1]]);
                    let frame = modbus_tcp::build_mbap_frame(tid, unit_id, &resp);
                    let _ = stream.write_all(&frame);
                }
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                std::thread::sleep(std::time::Duration::from_millis(5));
                continue;
            }
            Err(_) => break,
        }
    }
}

/// 处理一个 PDU 请求,生成响应 PDU(返回 None 表示静默丢弃,如广播)。
pub fn handle_request(pdu_bytes: &[u8], memory: &Arc<Mutex<SlaveMemory>>) -> Option<Vec<u8>> {
    if pdu_bytes.is_empty() {
        return None;
    }
    let fc = pdu_bytes[0];
    let data = &pdu_bytes[1..];
    // 读操作:锁一次,直接读
    match fc {
        0x01 => {
            let mem = memory.lock().unwrap_or_else(|e| e.into_inner());
            handle_read_bits(fc, data, &mem.coils)
        }
        0x02 => {
            let mem = memory.lock().unwrap_or_else(|e| e.into_inner());
            handle_read_bits(fc, data, &mem.discrete_inputs)
        }
        0x03 => {
            let mem = memory.lock().unwrap_or_else(|e| e.into_inner());
            handle_read_registers(fc, data, &mem.holding_registers)
        }
        0x04 => {
            let mem = memory.lock().unwrap_or_else(|e| e.into_inner());
            handle_read_registers(fc, data, &mem.input_registers)
        }
        // 写操作:各 handler 内部自己 lock(不重入)
        0x05 => handle_write_single_coil(data, memory),
        0x06 => handle_write_single_register(data, memory),
        0x0F => handle_write_multiple_coils(data, memory),
        0x10 => handle_write_multiple_registers(data, memory),
        // 高级 FC
        0x16 => handle_mask_write_register(data, memory),
        0x17 => handle_read_write_multiple(data, memory),
        0x08 => handle_diagnostics(data),
        0x2B => handle_read_device_id(data),
        // 诊断类
        0x07 => Some(vec![0x07, 0x00]), // 异常状态 = 0(无异常)
        0x0B => Some(vec![0x0B, 0x00, 0x00, 0x00, 0x00]), // 事件计数 = 0
        0x0C => Some(vec![0x0C, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]), // 事件日志(空)
        0x11 => Some(vec![0x11, 0x03, b'N', b'X', 0xFF]), // 从站 ID = "NX", ON
        _ => Some(exception_response(fc, 0x01)), // 非法功能
    }
}

fn handle_read_bits(fc: u8, data: &[u8], bits: &[bool; 65536]) -> Option<Vec<u8>> {
    if data.len() < 4 {
        return Some(exception_response(fc, 0x04));
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let quantity = u16::from_be_bytes([data[2], data[3]]);
    if quantity == 0 || quantity > 2000 {
        return Some(exception_response(fc, 0x03));
    }
    let end = address as usize + quantity as usize;
    if end > 65536 {
        return Some(exception_response(fc, 0x02));
    }
    let byte_count = u8::try_from(quantity.div_ceil(8)).unwrap();
    let mut response = vec![fc, byte_count];
    for byte_idx in 0..byte_count {
        let mut byte = 0u8;
        for bit_idx in 0..8 {
            let abs_bit = byte_idx as usize * 8 + bit_idx;
            if abs_bit >= quantity as usize {
                break;
            }
            if bits[address as usize + abs_bit] {
                byte |= 1 << bit_idx;
            }
        }
        response.push(byte);
    }
    Some(response)
}

fn handle_read_registers(fc: u8, data: &[u8], regs: &[u16; 65536]) -> Option<Vec<u8>> {
    if data.len() < 4 {
        return Some(exception_response(fc, 0x04));
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let quantity = u16::from_be_bytes([data[2], data[3]]);
    if quantity == 0 || quantity > 125 {
        return Some(exception_response(fc, 0x03));
    }
    let end = address as usize + quantity as usize;
    if end > 65536 {
        return Some(exception_response(fc, 0x02));
    }
    let byte_count = u8::try_from(quantity * 2).unwrap();
    let mut response = vec![fc, byte_count];
    for i in 0..quantity {
        response.extend_from_slice(&regs[address as usize + i as usize].to_be_bytes());
    }
    Some(response)
}

fn handle_write_single_coil(data: &[u8], memory: &Arc<Mutex<SlaveMemory>>) -> Option<Vec<u8>> {
    if data.len() < 4 {
        return Some(exception_response(0x05, 0x04));
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let value = u16::from_be_bytes([data[2], data[3]]);
    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    let on = match value {
        0xFF00 => true,
        0x0000 => false,
        _ => return Some(exception_response(0x05, 0x03)),
    };
    if (address as usize) < 65536 {
        mem.coils[address as usize] = on;
    }
    // 回显请求
    Some(vec![0x05, data[0], data[1], data[2], data[3]])
}

fn handle_write_single_register(data: &[u8], memory: &Arc<Mutex<SlaveMemory>>) -> Option<Vec<u8>> {
    if data.len() < 4 {
        return Some(exception_response(0x06, 0x04));
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let value = u16::from_be_bytes([data[2], data[3]]);
    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    if (address as usize) < 65536 {
        mem.holding_registers[address as usize] = value;
    }
    Some(vec![0x06, data[0], data[1], data[2], data[3]])
}

fn handle_write_multiple_coils(data: &[u8], memory: &Arc<Mutex<SlaveMemory>>) -> Option<Vec<u8>> {
    if data.len() < 5 {
        return Some(exception_response(0x0F, 0x04));
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let quantity = u16::from_be_bytes([data[2], data[3]]);
    let byte_count = data[4] as usize;
    // 数量与数据字节一致性校验(规范 FC15:quantity 1..=0x7B0,byte_count=ceil(q/8))。
    // 旧实现缺此校验:quantity=0xFFFF+byte_count=1 的畸形帧导致索引越界 panic + 锁中毒,
    // 14 字节报文即可远程瘫痪整个虚拟从站。
    if quantity < 1 || quantity > 1968 || byte_count != (quantity as usize + 7) / 8 {
        return Some(exception_response(0x0F, 0x03));
    }
    if data.len() < 5 + byte_count {
        return Some(exception_response(0x0F, 0x04));
    }
    if address as usize + quantity as usize > 65536 {
        return Some(exception_response(0x0F, 0x02));
    }
    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    for i in 0..quantity as usize {
        let on = data[5 + i / 8] & (1 << (i % 8)) != 0;
        mem.coils[address as usize + i] = on;
    }
    // 回显 addr + quantity
    Some(vec![0x0F, data[0], data[1], data[2], data[3]])
}

fn handle_write_multiple_registers(
    data: &[u8],
    memory: &Arc<Mutex<SlaveMemory>>,
) -> Option<Vec<u8>> {
    if data.len() < 5 {
        return Some(exception_response(0x10, 0x04));
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let quantity = u16::from_be_bytes([data[2], data[3]]);
    let byte_count = data[4] as usize;
    if data.len() < 5 + byte_count {
        return Some(exception_response(0x10, 0x04));
    }
    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    for i in 0..quantity {
        let offset = 5 + i as usize * 2;
        if offset + 1 >= data.len() {
            break;
        }
        let value = u16::from_be_bytes([data[offset], data[offset + 1]]);
        let idx = address as usize + i as usize;
        if idx < 65536 {
            mem.holding_registers[idx] = value;
        }
    }
    Some(vec![0x10, data[0], data[1], data[2], data[3]])
}

fn handle_mask_write_register(data: &[u8], memory: &Arc<Mutex<SlaveMemory>>) -> Option<Vec<u8>> {
    if data.len() < 6 {
        return Some(exception_response(0x16, 0x04));
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let and_mask = u16::from_be_bytes([data[2], data[3]]);
    let or_mask = u16::from_be_bytes([data[4], data[5]]);
    let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    let idx = address as usize;
    if idx < 65536 {
        let current = mem.holding_registers[idx];
        // FC22 规范公式:Result = (Current AND And_Mask) OR (Or_Mask AND (NOT And_Mask))
        mem.holding_registers[idx] = (current & and_mask) | (or_mask & !and_mask);
    }
    Some(vec![0x16, data[0], data[1], data[2], data[3], data[4], data[5]])
}

fn handle_read_write_multiple(data: &[u8], memory: &Arc<Mutex<SlaveMemory>>) -> Option<Vec<u8>> {
    if data.len() < 9 {
        return Some(exception_response(0x17, 0x04));
    }
    let read_address = u16::from_be_bytes([data[0], data[1]]);
    let read_quantity = u16::from_be_bytes([data[2], data[3]]);
    let write_address = u16::from_be_bytes([data[4], data[5]]);
    let write_qty = u16::from_be_bytes([data[6], data[7]]);
    let byte_count = data[8] as usize;
    if data.len() < 9 + byte_count || write_qty as usize * 2 != byte_count {
        return Some(exception_response(0x17, 0x04));
    }
    // FC23 数量上限:读≤125,写≤121(PDU=10+N×2≤253)
    if read_quantity < 1 || read_quantity > 125 || write_qty < 1 || write_qty > 121 {
        return Some(exception_response(0x17, 0x03));
    }
    // 地址越界 → 0x02(旧实现把越界读钳制到 65535,返回伪造数据且报成功——静默数据错)
    if read_address as usize + read_quantity as usize > 65536
        || write_address as usize + write_qty as usize > 65536
    {
        return Some(exception_response(0x17, 0x02));
    }
    // 先写
    {
        let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        for i in 0..write_qty as usize {
            let offset = 9 + i * 2;
            let val = u16::from_be_bytes([data[offset], data[offset + 1]]);
            mem.holding_registers[write_address as usize + i] = val;
        }
    }
    // 再读
    let mem = memory.lock().unwrap_or_else(|e| e.into_inner());
    let resp_byte_count = u8::try_from(read_quantity * 2).unwrap();
    let mut response = vec![0x17, resp_byte_count];
    for i in 0..read_quantity as usize {
        response.extend_from_slice(&mem.holding_registers[read_address as usize + i].to_be_bytes());
    }
    Some(response)
}

fn handle_diagnostics(data: &[u8]) -> Option<Vec<u8>> {
    // 回环(子功能 0):原样返回 sub_function + data
    if data.len() >= 3 {
        Some(vec![0x08, data[0], data[1], data[2]])
    } else {
        Some(exception_response(0x08, 0x01))
    }
}

fn handle_read_device_id(data: &[u8]) -> Option<Vec<u8>> {
    // MEI 0x0E, 基本设备标识
    let read_code = data.get(1).copied().unwrap_or(1);
    let object_id = data.get(2).copied().unwrap_or(0);
    // 返回:FC + MEI + readCode + conformity(0x02) + moreFollows(0) + nextObj(0) + objCount
    let vendor = b"Nexus-Rust";
    let product = b"Virtual Slave";
    let version = b"1.0.0";
    let objects: Vec<(u8, &[u8])> = vec![
        (0x00, vendor),
        (0x01, product),
        (0x02, version),
    ];
    let count = objects.len() as u8;
    let mut response = vec![0x2B, 0x0E, read_code, 0x02, 0x00, 0x00, count];
    for (id, val) in &objects {
        response.push(*id);
        response.push(val.len() as u8);
        response.extend_from_slice(val);
    }
    let _ = object_id;
    Some(response)
}

/// 构造异常响应 PDU:[fc | 0x80, exception_code]。
fn exception_response(fc: u8, code: u8) -> Vec<u8> {
    vec![fc | 0x80, code]
}

#[cfg(test)]
mod tests {
    // === 审查回归测试(2026-08-17):畸形帧不得 panic、越界不得伪造数据 ===

    #[test]
    fn fc15_malformed_quantity_returns_exception_not_panic() {
        // 审查 B1:quantity=0xFFFF + byte_count=1 的 14 字节畸形帧——
        // 旧实现索引越界 panic + 锁中毒瘫痪整个从站;应回 0x03
        let mem = std::sync::Arc::new(std::sync::Mutex::new(SlaveMemory::new()));
        let pdu = [0x0Fu8, 0x00, 0x00, 0xFF, 0xFF, 0x01, 0x00];
        let resp = handle_request(&pdu, &mem).expect("应有响应");
        assert_eq!(resp[0], 0x8F, "异常响应 FC|0x80");
        assert_eq!(resp[1], 0x03, "illegal data value");
        // 锁未被毒化:后续访问正常
        drop(mem.lock().unwrap_or_else(|e| e.into_inner()));
    }

    #[test]
    fn fc15_quantity_bytecount_mismatch_rejected() {
        let mem = std::sync::Arc::new(std::sync::Mutex::new(SlaveMemory::new()));
        // quantity=16(需 2 字节)但 byte_count=1
        let pdu = [0x0Fu8, 0x00, 0x00, 0x00, 0x10, 0x01, 0xFF];
        let resp = handle_request(&pdu, &mem).unwrap();
        assert_eq!(resp[1], 0x03);
    }

    #[test]
    fn fc23_read_out_of_range_returns_exception_not_clamped_data() {
        // 审查 B2:read_address=65535 + quantity=125 越界——
        // 旧实现把索引钳制到 65535 返回伪造数据且报成功;应回 0x02
        let mem = std::sync::Arc::new(std::sync::Mutex::new(SlaveMemory::new()));
        let mut pdu = vec![0x17u8];
        pdu.extend_from_slice(&65535u16.to_be_bytes()); // read addr
        pdu.extend_from_slice(&125u16.to_be_bytes());   // read qty
        pdu.extend_from_slice(&0u16.to_be_bytes());     // write addr
        pdu.extend_from_slice(&1u16.to_be_bytes());     // write qty
        pdu.push(2);                                     // byte count
        pdu.extend_from_slice(&0x1234u16.to_be_bytes());
        let resp = handle_request(&pdu, &mem).unwrap();
        assert_eq!(resp[0], 0x97);
        assert_eq!(resp[1], 0x02, "illegal data address");
    }

    #[test]
    fn fc15_normal_write_still_works() {
        let mem = std::sync::Arc::new(std::sync::Mutex::new(SlaveMemory::new()));
        let pdu = [0x0Fu8, 0x00, 0x0A, 0x00, 0x09, 0x02, 0xFF, 0x00];
        let resp = handle_request(&pdu, &mem).unwrap();
        assert_eq!(resp[0], 0x0F, "正常回显非异常");
        let m = mem.lock().unwrap_or_else(|e| e.into_inner());
        assert!(m.coils[10]);
        assert!(!m.coils[18]);
    }

    use super::*;

    fn test_memory() -> Arc<Mutex<SlaveMemory>> {
        let mut mem = SlaveMemory::new();
        mem.holding_registers[0] = 0x1234;
        mem.holding_registers[1] = 0xABCD;
        mem.coils[0] = true;
        mem.coils[1] = false;
        mem.coils[2] = true;
        Arc::new(Mutex::new(mem))
    }

    #[test]
    fn fc03_read_holding_registers_returns_data() {
        let mem = test_memory();
        // FC03, addr=0, qty=2
        let request = vec![0x03, 0x00, 0x00, 0x00, 0x02];
        let response = handle_request(&request, &mem).unwrap();
        assert_eq!(response[0], 0x03);
        assert_eq!(response[1], 4); // byte_count
        assert_eq!(&response[2..], &[0x12, 0x34, 0xAB, 0xCD]);
    }

    #[test]
    fn fc01_read_coils_packs_bits() {
        let mem = test_memory();
        // FC01, addr=0, qty=3
        let request = vec![0x01, 0x00, 0x00, 0x00, 0x03];
        let response = handle_request(&request, &mem).unwrap();
        assert_eq!(response[0], 0x01);
        assert_eq!(response[1], 1); // byte_count=1
        // bits: [0]=true, [1]=false, [2]=true → 0b00000101 = 0x05
        assert_eq!(response[2], 0x05);
    }

    #[test]
    fn fc06_write_single_register_stores_and_echoes() {
        let mem = test_memory();
        // FC06, addr=5, value=0x9999
        let request = vec![0x06, 0x00, 0x05, 0x99, 0x99];
        let response = handle_request(&request, &mem).unwrap();
        assert_eq!(response, vec![0x06, 0x00, 0x05, 0x99, 0x99]);
        // 验证写入了
        assert_eq!(mem.lock().unwrap_or_else(|e| e.into_inner()).holding_registers[5], 0x9999);
    }

    #[test]
    fn fc05_write_single_coil_on_off() {
        let mem = test_memory();
        // ON
        let req_on = vec![0x05, 0x00, 0x0A, 0xFF, 0x00];
        handle_request(&req_on, &mem);
        assert!(mem.lock().unwrap_or_else(|e| e.into_inner()).coils[10]);
        // OFF
        let req_off = vec![0x05, 0x00, 0x0A, 0x00, 0x00];
        handle_request(&req_off, &mem);
        assert!(!mem.lock().unwrap_or_else(|e| e.into_inner()).coils[10]);
    }

    #[test]
    fn fc16_write_multiple_registers_stores_data() {
        let mem = test_memory();
        // FC16, addr=10, qty=2, byte_count=4, data=[0x1111, 0x2222]
        let request = vec![0x10, 0x00, 0x0A, 0x00, 0x02, 0x04, 0x11, 0x11, 0x22, 0x22];
        let response = handle_request(&request, &mem).unwrap();
        assert_eq!(response, vec![0x10, 0x00, 0x0A, 0x00, 0x02]);
        let m = mem.lock().unwrap_or_else(|e| e.into_inner());
        assert_eq!(m.holding_registers[10], 0x1111);
        assert_eq!(m.holding_registers[11], 0x2222);
    }

    #[test]
    fn unknown_fc_returns_exception() {
        let mem = test_memory();
        let request = vec![0x42, 0x00];
        let response = handle_request(&request, &mem).unwrap();
        assert_eq!(response[0], 0xC2); // 0x42 | 0x80
        assert_eq!(response[1], 0x01); // 非法功能
    }

    #[test]
    fn out_of_range_returns_exception_02() {
        let mem = test_memory();
        // FC03, addr=65535, qty=2 → 越界
        let request = vec![0x03, 0xFF, 0xFF, 0x00, 0x02];
        let response = handle_request(&request, &mem).unwrap();
        assert_eq!(response[0], 0x83);
        assert_eq!(response[1], 0x02); // 非法地址
    }

    #[test]
    fn seed_demo_populates_registers() {
        let mut mem = SlaveMemory::new();
        mem.seed_demo();
        assert_eq!(mem.holding_registers[0], 128);
        assert_eq!(mem.holding_registers[1], 256);
        assert!(mem.coils[0]);
    }

    #[test]
    fn clear_area_zeros_registers() {
        let mut mem = SlaveMemory::new();
        mem.seed_demo();
        mem.clear_area("holding");
        assert_eq!(mem.holding_registers[0], 0);
    }

    #[test]
    fn set_holding_batch_writes() {
        let mut mem = SlaveMemory::new();
        mem.set_holding(100, &[10, 20, 30]);
        assert_eq!(mem.holding_registers[100], 10);
        assert_eq!(mem.holding_registers[101], 20);
        assert_eq!(mem.holding_registers[102], 30);
    }
}
