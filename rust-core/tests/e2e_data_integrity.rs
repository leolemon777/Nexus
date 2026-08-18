//! 端到端功能验证:启动 TCP 从站 → 设定已知值 → 主站逐 FC 读回对比。
//! cargo test --test e2e_data_integrity -- --test-threads=1

use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicU16, Ordering};
use std::sync::mpsc::{self, Receiver};
use std::thread;
use std::time::Duration;

use serde_json::{json, Value};

const PROTOCOL_VERSION: u64 = 1;
const RESPONSE_TIMEOUT: Duration = Duration::from_secs(10);
static PORT_GEN: AtomicU16 = AtomicU16::new(15030);
fn next_port() -> u16 { PORT_GEN.fetch_add(1, Ordering::SeqCst) }

struct Sidecar {
    child: Child,
    stdin: Option<ChildStdin>,
    stdout_lines: Receiver<String>,
}

impl Sidecar {
    fn spawn() -> Self {
        let mut child = Command::new(sidecar_binary())
            .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null())
            .spawn().expect("failed to start sidecar");
        let stdin = child.stdin.take().unwrap();
        let stdout = child.stdout.take().unwrap();
        let (tx, rx) = mpsc::channel();
        thread::spawn(move || {
            for line in BufReader::new(stdout).lines().flatten() {
                if tx.send(line).is_err() { break; }
            }
        });
        Self { child, stdin: Some(stdin), stdout_lines: rx }
    }

    fn send(&mut self, id: &str, cmd: &str, payload: Value) -> Value {
        let req = json!({ "protocolVersion": PROTOCOL_VERSION, "requestId": id, "command": cmd, "payload": payload });
        let line = req.to_string();
        let stdin = self.stdin.as_mut().unwrap();
        stdin.write_all(line.as_bytes()).unwrap();
        stdin.write_all(b"\n").unwrap();
        stdin.flush().unwrap();
        loop {
            let raw = self.stdout_lines.recv_timeout(RESPONSE_TIMEOUT).expect("timeout");
            let resp: Value = serde_json::from_str(&raw).unwrap_or_else(|e| panic!("bad JSON: {raw}: {e}"));
            if resp.get("requestId").and_then(|v| v.as_str()) == Some(id) { return resp; }
        }
    }

    fn send_ok(&mut self, id: &str, cmd: &str, payload: Value) -> Value {
        let resp = self.send(id, cmd, payload);
        assert!(resp.get("ok").and_then(|v| v.as_bool()) == Some(true),
            "命令 {} 失败: {}", cmd, resp);
        resp["result"].clone()
    }
}

impl Drop for Sidecar {
    fn drop(&mut self) {
        self.stdin.take();
        if self.child.try_wait().ok().flatten().is_none() {
            let _ = self.child.kill();
            let _ = self.child.wait();
        }
    }
}

fn sidecar_binary() -> PathBuf {
    option_env!("CARGO_BIN_EXE_nexus-rust-core")
        .or_else(|| option_env!("CARGO_BIN_EXE_nexus_rust_core"))
        .map(PathBuf::from).expect("binary not set")
}

fn setup() -> (u16, Sidecar) {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("s", "start_tcp_slave", json!({ "slaveId": "t", "port": port, "allowedStationIds": [] }));
    std::thread::sleep(Duration::from_millis(200));
    // unitId 在连接时设定,TCP 读命令不再需要 unitId
    s.send_ok("c", "open_tcp_connection", json!({
        "connectionId": "c", "host": "127.0.0.1", "port": port, "unitId": 1, "framing": "standard"
    }));
    (port, s)
}

// FC03 读保持寄存器
#[test]
fn e2e_fc03() {
    let (_, mut s) = setup();
    s.send_ok("set", "slave_set_value", json!({ "slaveId": "t", "area": "holding", "address": 100, "values": [0x1234, 0xABCD] }));
    let r = s.send_ok("r", "tcp_read_holding_registers", json!({ "connectionId": "c", "startAddress": 100, "quantity": 2 }));
    assert_eq!(r["registers"][0].as_u64(), Some(0x1234));
    assert_eq!(r["registers"][1].as_u64(), Some(0xABCD));
}

// FC04 读输入寄存器
#[test]
fn e2e_fc04() {
    let (_, mut s) = setup();
    s.send_ok("set", "slave_set_value", json!({ "slaveId": "t", "area": "input", "address": 10, "values": [1, 2, 3] }));
    let r = s.send_ok("r", "tcp_read_input_registers", json!({ "connectionId": "c", "startAddress": 10, "quantity": 3 }));
    let regs = r["registers"].as_array().unwrap();
    assert_eq!(regs.len(), 3);
    assert_eq!(regs[0].as_u64(), Some(1));
    assert_eq!(regs[1].as_u64(), Some(2));
    assert_eq!(regs[2].as_u64(), Some(3));
}

// FC01 读线圈:12 个交替,验证填充位截断
#[test]
fn e2e_fc01_padding() {
    let (_, mut s) = setup();
    let vals: Vec<Value> = (0..12).map(|i| json!(i % 2 == 0)).collect();
    s.send_ok("set", "slave_set_coil", json!({ "slaveId": "t", "area": "coil", "address": 0, "values": vals }));
    let r = s.send_ok("r", "tcp_read_coils", json!({ "connectionId": "c", "startAddress": 0, "quantity": 12 }));
    let coils = r["coils"].as_array().unwrap();
    assert_eq!(coils.len(), 12, "填充位必须截断: 实际 {}", coils.len());
    for i in 0..12 { assert_eq!(coils[i].as_bool(), Some(i % 2 == 0), "线圈 {}", i); }
}

// FC02 读离散输入
#[test]
fn e2e_fc02() {
    let (_, mut s) = setup();
    s.send_ok("set", "slave_set_coil", json!({
        "slaveId": "t", "area": "discrete", "address": 0,
        "values": [json!(true),json!(true),json!(true),json!(true),json!(true),json!(true),json!(true),json!(true)]
    }));
    let r = s.send_ok("r", "tcp_read_discrete_inputs", json!({ "connectionId": "c", "startAddress": 0, "quantity": 8 }));
    let coils = r["coils"].as_array().unwrap();
    assert_eq!(coils.len(), 8);
    assert!(coils.iter().all(|v| v.as_bool() == Some(true)));
}

// FC05 写单线圈 ON/OFF
#[test]
fn e2e_fc05() {
    let (_, mut s) = setup();
    s.send_ok("on", "tcp_write_single_coil", json!({ "connectionId": "c", "address": 50, "value": true }));
    let r = s.send_ok("v1", "tcp_read_coils", json!({ "connectionId": "c", "startAddress": 50, "quantity": 1 }));
    assert_eq!(r["coils"][0].as_bool(), Some(true));
    s.send_ok("off", "tcp_write_single_coil", json!({ "connectionId": "c", "address": 50, "value": false }));
    let r = s.send_ok("v2", "tcp_read_coils", json!({ "connectionId": "c", "startAddress": 50, "quantity": 1 }));
    assert_eq!(r["coils"][0].as_bool(), Some(false));
}

// FC06 写单寄存器 0xBEEF
#[test]
fn e2e_fc06() {
    let (_, mut s) = setup();
    s.send_ok("w", "tcp_write_single_register", json!({ "connectionId": "c", "address": 200, "value": 48879 }));
    let r = s.send_ok("r", "tcp_read_holding_registers", json!({ "connectionId": "c", "startAddress": 200, "quantity": 1 }));
    assert_eq!(r["registers"][0].as_u64(), Some(0xBEEF));
}

// FC16 写多寄存器
#[test]
fn e2e_fc16() {
    let (_, mut s) = setup();
    s.send_ok("w", "tcp_write_multiple_registers", json!({
        "connectionId": "c", "address": 300, "values": [json!(100), json!(200), json!(300), json!(400), json!(500)]
    }));
    let r = s.send_ok("r", "tcp_read_holding_registers", json!({ "connectionId": "c", "startAddress": 300, "quantity": 5 }));
    let regs = r["registers"].as_array().unwrap();
    assert_eq!(regs.len(), 5);
    assert_eq!(regs[0].as_u64(), Some(100));
    assert_eq!(regs[4].as_u64(), Some(500));
}

// FC15 写多线圈
#[test]
fn e2e_fc15() {
    let (_, mut s) = setup();
    s.send_ok("w", "tcp_write_multiple_coils", json!({
        "connectionId": "c", "address": 60, "values": [json!(true),json!(false),json!(true),json!(false),json!(true),json!(false),json!(true),json!(false)]
    }));
    let r = s.send_ok("r", "tcp_read_coils", json!({ "connectionId": "c", "startAddress": 60, "quantity": 8 }));
    let coils = r["coils"].as_array().unwrap();
    assert_eq!(coils.len(), 8);
    for i in 0..8 { assert_eq!(coils[i].as_bool(), Some(i % 2 == 0)); }
}

// FC22 mask write 公式验证(关键:验证修复后的 & !and_mask)
#[test]
fn e2e_fc22_formula() {
    let (_, mut s) = setup();
    // 设 0x000F
    s.send_ok("init", "slave_set_value", json!({ "slaveId": "t", "area": "holding", "address": 400, "values": [0x000F] }));
    // AND=0xFFFF(全保留) + OR=0x00F0
    // 规范: (0x000F & 0xFFFF) | (0x00F0 & ~0xFFFF) = 0x000F
    // 旧代码(错): (0x000F & 0xFFFF) | 0x00F0 = 0x00FF
    s.send_ok("mask", "tcp_mask_write_register", json!({
        "connectionId": "c", "address": 400, "andMask": 0xFFFF, "orMask": 0x00F0
    }));
    let r = s.send_ok("v", "tcp_read_holding_registers", json!({ "connectionId": "c", "startAddress": 400, "quantity": 1 }));
    assert_eq!(r["registers"][0].as_u64(), Some(0x000F),
        "AND=0xFFFF 全保留 + OR=0x00F0 → 应为 0x000F, 旧代码会错误得到 0x00FF");
}

// FC23 先写后读原子 + 上限 121
#[test]
fn e2e_fc23_atomic() {
    let (_, mut s) = setup();
    let r = s.send_ok("rw", "tcp_read_write_multiple", json!({
        "connectionId": "c", "readAddress": 500, "readQuantity": 2,
        "writeAddress": 500, "writeValues": [json!(0xCAFE), json!(0xBABE)]
    }));
    let regs = r["registers"].as_array().unwrap();
    assert_eq!(regs.len(), 2);
    assert_eq!(regs[0].as_u64(), Some(0xCAFE), "先写后读应读到 0xCAFE");
    assert_eq!(regs[1].as_u64(), Some(0xBABE));
}

#[test]
fn e2e_fc23_limit() {
    let (_, mut s) = setup();
    // 122 个应被拒绝
    let too_many: Vec<Value> = vec![json!(0); 122];
    let resp = s.send("reject", "tcp_read_write_multiple", json!({
        "connectionId": "c", "readAddress": 0, "readQuantity": 1, "writeAddress": 0, "writeValues": too_many
    }));
    assert!(resp.get("ok").and_then(|v| v.as_bool()) != Some(true), "FC23 写 122 应被拒绝");
    // 121 个应成功
    let ok_vals: Vec<Value> = vec![json!(0x1111); 121];
    s.send_ok("accept", "tcp_read_write_multiple", json!({
        "connectionId": "c", "readAddress": 0, "readQuantity": 1, "writeAddress": 0, "writeValues": ok_vals
    }));
}

// FC43 设备标识
#[test]
fn e2e_fc43() {
    let (_, mut s) = setup();
    let r = s.send_ok("fc43", "tcp_read_device_id", json!({
        "connectionId": "c", "readDeviceIdCode": 1, "objectId": 0
    }));
    let pages = r["pages"].as_array().expect("pages 数组");
    assert!(pages.len() >= 1, "至少 1 页");
    assert_eq!(pages[0]["meiType"].as_u64(), Some(0x0E));
}
