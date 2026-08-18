//! 欧姆龙 FINS JSONL E2E:启动 sidecar → FINS 虚拟 PLC → JSONL 在线读写(TCP + UDP)。
//!
//! cargo test --test fins_jsonl_e2e -- --test-threads=1

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
static PORT_GEN: AtomicU16 = AtomicU16::new(16230);
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
        assert!(resp.get("ok").and_then(|v| v.as_bool()) == Some(true), "命令 {} 失败: {}", cmd, resp);
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

fn setup_tcp() -> (u16, Sidecar) {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("fins-slave", "start_fins_slave", json!({ "slaveId": "fins", "port": port, "seed": true }));
    std::thread::sleep(Duration::from_millis(250));
    s.send_ok("fins-conn", "open_fins_tcp", json!({
        "connectionId": "c", "host": "127.0.0.1", "port": port, "destNode": 0, "sourceNode": 0
    }));
    (port, s)
}

/// E2E 01:握手 + 读 D100/D101(seed 0x1234/0xABCD)
#[test]
fn fins_e2e_tcp_read_dm_seed() {
    let (_, mut s) = setup_tcp();
    let r = s.send_ok("r1", "fins_read", json!({
        "connectionId": "c", "address": "D100", "count": 2
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0x1234));
    assert_eq!(values[1].as_u64(), Some(0xABCD));
    assert_eq!(r["isBit"].as_bool(), Some(false));
}

/// E2E 02:读 CIO0 = 0xBEEF
#[test]
fn fins_e2e_tcp_read_cio() {
    let (_, mut s) = setup_tcp();
    let r = s.send_ok("r2", "fins_read", json!({
        "connectionId": "c", "address": "CIO0", "count": 1
    }));
    assert_eq!(r["values"][0].as_u64(), Some(0xBEEF));
}

/// E2E 03:位读 CIO0.00(0xBEEF 低 4 位全 1)
#[test]
fn fins_e2e_tcp_bit_read() {
    let (_, mut s) = setup_tcp();
    let r = s.send_ok("r3", "fins_read", json!({
        "connectionId": "c", "address": "CIO0.00", "count": 4
    }));
    assert_eq!(r["isBit"].as_bool(), Some(true));
    let values = r["values"].as_array().unwrap();
    for v in values { assert_eq!(v.as_u64(), Some(1)); }
}

/// E2E 04:写 W20 → 读回
#[test]
fn fins_e2e_tcp_write_readback() {
    let (_, mut s) = setup_tcp();
    s.send_ok("w1", "fins_write", json!({
        "connectionId": "c", "address": "W20", "values": [0xCAFE, 0xBABE]
    }));
    let r = s.send_ok("r4", "fins_read", json!({
        "connectionId": "c", "address": "W20", "count": 2
    }));
    assert_eq!(r["values"][0].as_u64(), Some(0xCAFE));
    assert_eq!(r["values"][1].as_u64(), Some(0xBABE));
}

/// E2E 05:位写(读-改-写:写 CIO1.00=1 后 CIO1.01 不受影响——先写 CIO1=0)
#[test]
fn fins_e2e_tcp_bit_write() {
    let (_, mut s) = setup_tcp();
    s.send_ok("w2", "fins_write", json!({
        "connectionId": "c", "address": "CIO1", "values": [0]
    }));
    s.send_ok("w3", "fins_write", json!({
        "connectionId": "c", "address": "CIO1.00", "values": [1]
    }));
    let r = s.send_ok("r5", "fins_read", json!({
        "connectionId": "c", "address": "CIO1", "count": 1
    }));
    assert_eq!(r["values"][0].as_u64(), Some(1));
}

/// E2E 06:UDP 传输(裸应用帧)
#[test]
fn fins_e2e_udp_read() {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("fins-slave2", "start_fins_slave", json!({ "slaveId": "fins-u", "port": port }));
    std::thread::sleep(Duration::from_millis(250));
    s.send_ok("fins-udp", "open_fins_udp", json!({
        "connectionId": "cu", "host": "127.0.0.1", "port": port
    }));
    let r = s.send_ok("r6", "fins_read", json!({
        "connectionId": "cu", "address": "D200", "count": 1
    }));
    assert_eq!(r["values"][0].as_u64(), Some(0xBEEF));
}

/// E2E 07:定时器/计数器当前值(T0=100)
#[test]
fn fins_e2e_tim_current_value() {
    let (_, mut s) = setup_tcp();
    let r = s.send_ok("r7", "fins_read", json!({
        "connectionId": "c", "address": "T0", "count": 1
    }));
    assert_eq!(r["values"][0].as_u64(), Some(100));
}

/// E2E 08:越界 → FINS 结束码 0x0203
#[test]
fn fins_e2e_out_of_range_end_code() {
    let (_, mut s) = setup_tcp();
    let resp = s.send("r8", "fins_read", json!({
        "connectionId": "c", "address": "D40000", "count": 2
    }));
    assert_eq!(resp["ok"].as_bool(), Some(false));
    let msg = resp["error"]["message"].as_str().unwrap_or("");
    assert!(msg.contains("0203") || msg.contains("越界") || msg.contains("超限"), "消息:{msg}");
}

/// E2E 09:TS/CS 未内置 → 明确 MANUAL 提示(不猜)
#[test]
fn fins_e2e_ts_cs_manual_hint() {
    let (_, mut s) = setup_tcp();
    let resp = s.send("r9", "fins_read", json!({
        "connectionId": "c", "address": "TS0", "count": 1
    }));
    assert_eq!(resp["ok"].as_bool(), Some(false));
    let msg = resp["error"]["message"].as_str().unwrap_or("");
    assert!(msg.contains("W342"), "应提示查手册:{msg}");
}

/// E2E 10:从站 set/get 直通道
#[test]
fn fins_e2e_slave_set_get() {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("sl", "start_fins_slave", json!({ "slaveId": "fs", "port": port }));
    s.send_ok("ss", "fins_slave_set", json!({
        "slaveId": "fs", "address": "D500", "values": [111, 222]
    }));
    let g = s.send_ok("sg", "fins_slave_get", json!({
        "slaveId": "fs", "address": "D500", "count": 2
    }));
    assert_eq!(g["values"][0].as_u64(), Some(111));
    // 在线通道也可见
    s.send_ok("fc", "open_fins_tcp", json!({
        "connectionId": "c2", "host": "127.0.0.1", "port": port
    }));
    let r = s.send_ok("r10", "fins_read", json!({
        "connectionId": "c2", "address": "D500", "count": 2
    }));
    assert_eq!(r["values"][1].as_u64(), Some(222));
}

/// E2E 11:地址解析命令
#[test]
fn fins_e2e_parse_address() {
    let mut s = Sidecar::spawn();
    let r = s.send_ok("pa", "fins_parse_address", json!({ "address": "D100.15" }));
    assert_eq!(r["areaCode"].as_str(), Some("0x02"));
    assert_eq!(r["address"].as_u64(), Some(1615));
    let r2 = s.send_ok("pa2", "fins_parse_address", json!({ "address": "W10" }));
    assert_eq!(r2["areaCode"].as_str(), Some("0xB1"));
}

/// E2E 12:非法地址
#[test]
fn fins_e2e_invalid_address() {
    let (_, mut s) = setup_tcp();
    let resp = s.send("r11", "fins_read", json!({
        "connectionId": "c", "address": "ZZ10", "count": 1
    }));
    assert_eq!(resp["ok"].as_bool(), Some(false));
}

/// E2E 13:停从站后连接失败
#[test]
fn fins_e2e_stop_slave() {
    let (port, mut s) = setup_tcp();
    s.send_ok("stop", "stop_fins_slave", json!({ "slaveId": "fins" }));
    s.send_ok("cl", "close_connection", json!({ "connectionId": "c" }));
    std::thread::sleep(Duration::from_millis(300));
    let resp = s.send("rc", "open_fins_tcp", json!({
        "connectionId": "c9", "host": "127.0.0.1", "port": port
    }));
    assert_eq!(resp["ok"].as_bool(), Some(false));
}

/// E2E 14:连续多轮事务(SID 递增不串扰)
#[test]
fn fins_e2e_sequential_transactions() {
    let (_, mut s) = setup_tcp();
    for i in 0..5u64 {
        s.send_ok("w-seq", "fins_write", json!({
            "connectionId": "c", "address": "H0", "values": [i as u16]
        }));
        let r = s.send_ok("r-seq", "fins_read", json!({
            "connectionId": "c", "address": "H0", "count": 1
        }));
        assert_eq!(r["values"][0].as_u64(), Some(i), "第 {i} 轮");
    }
}
