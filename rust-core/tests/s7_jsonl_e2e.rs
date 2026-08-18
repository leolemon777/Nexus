//! 西门子 S7comm JSONL E2E:启动 sidecar → 启动 S7 虚拟从站 → JSONL 命令在线读写。
//! 验证整条链路:JSONL 命令 → s7_pdu 组帧 → TPKT/COTP → TCP → s7_slave 从站 → 响应解析。
//!
//! cargo test --test s7_jsonl_e2e -- --test-threads=1

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
static PORT_GEN: AtomicU16 = AtomicU16::new(16130);
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

impl Sidecar {
    fn spawn_sidecar_only() -> (u16, Sidecar) {
        (0, Self::spawn())
    }
}

fn setup() -> (u16, Sidecar) {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("s7-slave", "start_s7_slave", json!({
        "slaveId": "s7", "port": port, "seed": true
    }));
    std::thread::sleep(Duration::from_millis(200));
    s.send_ok("s7-conn", "open_s7_connection", json!({
        "connectionId": "c", "host": "127.0.0.1", "port": port,
        "rack": 0, "slot": 1, "connType": 1
    }));
    (port, s)
}

/// E2E 01:握手协商 PDU(默认请求 480,从站上限 480 → min = 480)
#[test]
fn s7_e2e_setup_negotiates_pdu() {
    let (port, mut s) = Sidecar::spawn_sidecar_only();
    let _ = port;
    let port2 = next_port();
    s.send_ok("s7-slave2", "start_s7_slave", json!({
        "slaveId": "s7x", "port": port2, "seed": true
    }));
    std::thread::sleep(Duration::from_millis(200));
    let r = s.send_ok("p1", "open_s7_connection", json!({
        "connectionId": "cx", "host": "127.0.0.1", "port": port2,
        "rack": 0, "slot": 1, "connType": 1, "pduRequest": 960
    }));
    assert_eq!(r["pduSize"].as_u64(), Some(480), "从站上限 480:min(960,480)");
    assert_eq!(r["maxReadBytes"].as_u64(), Some(449));
    assert_eq!(r["maxWriteBytes"].as_u64(), Some(452));
}

/// E2E 02:读 DB1.DBD0 = 0x12345678(seed)
#[test]
fn s7_e2e_read_db_seed() {
    let (_, mut s) = setup();
    let r = s.send_ok("r1", "s7_read", json!({
        "connectionId": "c", "items": [{ "address": "DB1.DBB0", "count": 4 }]
    }));
    let items = r["items"].as_array().unwrap();
    assert_eq!(items[0]["returnCode"].as_u64(), Some(0xFF));
    assert_eq!(items[0]["data"].as_array().unwrap(), &vec![json!(0x12), json!(0x34), json!(0x56), json!(0x78)]);
}

/// E2E 03:读 MW0(seed 0x1234)
#[test]
fn s7_e2e_read_marker() {
    let (_, mut s) = setup();
    let r = s.send_ok("r2", "s7_read", json!({
        "connectionId": "c", "items": ["MW0"]
    }));
    let data = r["items"][0]["data"].as_array().unwrap();
    assert_eq!(data[0].as_u64(), Some(0x12));
    assert_eq!(data[1].as_u64(), Some(0x34));
}

/// E2E 04:写 MW20 → 读回
#[test]
fn s7_e2e_write_read_roundtrip() {
    let (_, mut s) = setup();
    s.send_ok("w1", "s7_write", json!({
        "connectionId": "c", "items": [{ "address": "MW20", "values": [0xAB, 0xCD] }]
    }));
    let r = s.send_ok("r3", "s7_read", json!({
        "connectionId": "c", "items": ["MW20"]
    }));
    let data = r["items"][0]["data"].as_array().unwrap();
    assert_eq!(data[0].as_u64(), Some(0xAB));
    assert_eq!(data[1].as_u64(), Some(0xCD));
}

/// E2E 05:位写 M30.3(读改写,不破坏同字节其他位)
#[test]
fn s7_e2e_bit_write_does_not_clobber_byte() {
    let (_, mut s) = setup();
    // 预置 MB30 = 0x81
    s.send_ok("w2", "s7_write", json!({
        "connectionId": "c", "items": [{ "address": "MB30", "values": [0x81] }]
    }));
    // 写 M30.3 = 1 → 0x81 | 0x08 = 0x89
    s.send_ok("w3", "s7_write", json!({
        "connectionId": "c", "items": [{ "address": "M30.3", "values": [1] }]
    }));
    let r = s.send_ok("r4", "s7_read", json!({
        "connectionId": "c", "items": ["MB30"]
    }));
    assert_eq!(r["items"][0]["data"][0].as_u64(), Some(0x89));
    // 位读回
    let b = s.send_ok("r5", "s7_read", json!({
        "connectionId": "c", "items": ["M30.3"]
    }));
    assert_eq!(b["items"][0]["data"][0].as_u64(), Some(1));
}

/// E2E 06:读 M10.0/M10.1(seed M10=0x55 交替位)
#[test]
fn s7_e2e_read_seed_bits() {
    let (_, mut s) = setup();
    let r = s.send_ok("r6", "s7_read", json!({
        "connectionId": "c", "items": [{ "address": "M10.0", "count": 8 }]
    }));
    let data = r["items"][0]["data"].as_array().unwrap();
    assert_eq!(data.len(), 1, "8 位 → 1 字节");
    assert_eq!(data[0].as_u64(), Some(0x55));
}

/// E2E 07:SMART V 区语法(VW100 → DB1.DBW100)
#[test]
fn s7_e2e_smart_v_syntax() {
    let (_, mut s) = setup();
    s.send_ok("w4", "s7_write", json!({
        "connectionId": "c", "items": [{ "address": "VW100", "values": [0xCA, 0xFE] }]
    }));
    let r = s.send_ok("r7", "s7_read", json!({
        "connectionId": "c", "items": [{ "address": "VW100", "count": 1 }]
    }));
    assert_eq!(r["items"][0]["data"][0].as_u64(), Some(0xCA));
    assert_eq!(r["items"][0]["data"][1].as_u64(), Some(0xFE));
    // 与 DB1.DBW100 等价
    let r2 = s.send_ok("r8", "s7_read", json!({
        "connectionId": "c", "items": [{ "address": "DB1.DBW100", "count": 1 }]
    }));
    assert_eq!(r2["items"][0]["data"][0].as_u64(), Some(0xCA));
}

/// E2E 08:多 Item 一次读(DB + M + I + Q)
#[test]
fn s7_e2e_multi_item_read() {
    let (_, mut s) = setup();
    let r = s.send_ok("r9", "s7_read", json!({
        "connectionId": "c", "items": [
            { "address": "DB1.DBB0", "count": 4 },
            "MW0",
            "IW0",
            "QW0"
        ]
    }));
    let items = r["items"].as_array().unwrap();
    assert_eq!(items.len(), 4);
    assert_eq!(items[0]["data"][0].as_u64(), Some(0x12));
    assert_eq!(items[1]["data"][0].as_u64(), Some(0x12));
    assert_eq!(items[2]["data"][0].as_u64(), Some(0x11));
    assert_eq!(items[3]["data"][0].as_u64(), Some(0x22));
}

/// E2E 09:Timer/Counter 读(seed T0=0x2510, C0=0x0005)
#[test]
fn s7_e2e_timer_counter_read() {
    let (_, mut s) = setup();
    let r = s.send_ok("r10", "s7_read", json!({
        "connectionId": "c", "items": ["T0", "C0"]
    }));
    let items = r["items"].as_array().unwrap();
    assert_eq!(items[0]["data"][0].as_u64(), Some(0x25));
    assert_eq!(items[1]["data"][1].as_u64(), Some(0x05));
}

/// E2E 10:Timer 写 → 读回
#[test]
fn s7_e2e_timer_write_readback() {
    let (_, mut s) = setup();
    s.send_ok("w5", "s7_write", json!({
        "connectionId": "c", "items": [{ "address": "T3", "values": [0x12, 0x34] }]
    }));
    let r = s.send_ok("r11", "s7_read", json!({
        "connectionId": "c", "items": [{ "address": "T3", "count": 1 }]
    }));
    assert_eq!(r["items"][0]["data"][0].as_u64(), Some(0x12));
}

/// E2E 11:大块读自动分片(400 字节 > PDU-31=449?480-31=449 → 单片内;改用 600 字节强制分片)
#[test]
fn s7_e2e_large_read_chunked() {
    let (_, mut s) = setup();
    // 先写一段模式数据
    let pattern: Vec<u8> = (0..64u8).collect();
    s.send_ok("w6", "s7_write", json!({
        "connectionId": "c", "items": [{ "address": "DB1.DBB1000", "count": 64, "values": pattern }]
    }));
    let r = s.send_ok("r12", "s7_read", json!({
        "connectionId": "c", "items": [{ "address": "DB1.DBB1000", "count": 600 }]
    }));
    let data = r["items"][0]["data"].as_array().unwrap();
    assert_eq!(data.len(), 600, "分片后应合并为 600 字节");
    // 前 64 字节为模式数据
    for (i, v) in data.iter().take(64).enumerate() {
        assert_eq!(v.as_u64(), Some(i as u64), "offset {i}");
    }
}

/// E2E 12:s7_slave_set/get(从站内存直写直读)
#[test]
fn s7_e2e_slave_set_get() {
    let (_, mut s) = setup();
    s.send_ok("ss1", "s7_slave_set", json!({
        "slaveId": "s7", "address": "DB5.DBB10", "values": [1, 2, 3, 4]
    }));
    let g = s.send_ok("sg1", "s7_slave_get", json!({
        "slaveId": "s7", "address": "DB5.DBB10", "count": 4
    }));
    let data = g["data"].as_array().unwrap();
    assert_eq!(data[0].as_u64(), Some(1));
    // 在线读也可见
    let r = s.send_ok("r13", "s7_read", json!({
        "connectionId": "c", "items": [{ "address": "DB5.DBB10", "count": 4 }]
    }));
    assert_eq!(r["items"][0]["data"][3].as_u64(), Some(4));
}

/// E2E 13:地址越界 → Item RC 0x05 + 人话消息
#[test]
fn s7_e2e_address_out_of_range() {
    let (_, mut s) = setup();
    let r = s.send_ok("r14", "s7_read", json!({
        "connectionId": "c", "items": [{ "address": "DB1.DBB65534", "count": 4 }]
    }));
    assert_eq!(r["items"][0]["returnCode"].as_u64(), Some(0x05));
    let msg = r["items"][0]["returnCodeMessage"].as_str().unwrap();
    assert!(msg.contains("越界") || msg.contains("范围"), "消息应说明越界:{msg}");
}

/// E2E 14:s7_parse_address 输出结构化信息
#[test]
fn s7_e2e_parse_address() {
    let (_, mut s) = setup();
    let r = s.send_ok("pa1", "s7_parse_address", json!({ "address": "DB1.DBW20" }));
    assert_eq!(r["area"].as_u64(), Some(0x84));
    assert_eq!(r["db"].as_u64(), Some(1));
    assert_eq!(r["byte"].as_u64(), Some(20));
    assert_eq!(r["anyAddressHex"].as_str(), Some("0000A0"));
    // SMART 语法
    let r2 = s.send_ok("pa2", "s7_parse_address", json!({ "address": "VW100" }));
    assert_eq!(r2["db"].as_u64(), Some(1));
    assert_eq!(r2["display"].as_str(), Some("DB1.DBW100"));
}

/// E2E 15:非法地址报错
#[test]
fn s7_e2e_invalid_address_fails() {
    let (_, mut s) = setup();
    let resp = s.send("pa3", "s7_read", json!({
        "connectionId": "c", "items": ["ZZ9"]
    }));
    assert_eq!(resp["ok"].as_bool(), Some(false));
    assert!(resp["error"]["message"].as_str().unwrap_or("").contains("S7 地址"));
}

/// E2E 16:多轮事务 PDU Ref 配对(连续读 5 次不串扰)
#[test]
fn s7_e2e_sequential_transactions() {
    let (_, mut s) = setup();
    for i in 0..5u64 {
        s.send_ok("w-seq", "s7_write", json!({
            "connectionId": "c", "items": [{ "address": "MW40", "values": [i as u8, 0xAA] }]
        }));
        let r = s.send_ok("r-seq", "s7_read", json!({
            "connectionId": "c", "items": ["MW40"]
        }));
        assert_eq!(r["items"][0]["data"][0].as_u64(), Some(i), "第 {i} 轮");
    }
}

/// E2E 18(SW1):CPU 控制——停(结果码回显)
#[test]
fn s7_e2e_cpu_stop() {
    let (_, mut s) = setup();
    let r = s.send_ok("ctl1", "s7_cpu_control", json!({
        "connectionId": "c", "action": "stop"
    }));
    // 响应 para 回显 Fun 0x29(与 deep-dive §6.2 抓包一致)
    assert_eq!(r["result"].as_u64(), Some(0x29));
}

/// E2E 19(SW1):SZL 0x0424 → RUN
#[test]
fn s7_e2e_read_status_run() {
    let (_, mut s) = setup();
    let r = s.send_ok("st1", "s7_read_status", json!({ "connectionId": "c" }));
    assert_eq!(r["mode"].as_str(), Some("RUN"));
}

/// E2E 20(SW1):密码登录(虚拟 CPU 直通)
#[test]
fn s7_e2e_password_login() {
    let (_, mut s) = setup();
    s.send_ok("pw1", "s7_password", json!({
        "connectionId": "c", "password": "abc123"
    }));
}

/// E2E 21(SW1):200 家族 SM/AI/AQ 区(seed)
#[test]
fn s7_e2e_200_family_areas() {
    let (_, mut s) = setup();
    let sm = s.send_ok("sm1", "s7_read", json!({ "connectionId": "c", "items": ["SMW1"] }));
    assert_eq!(sm["items"][0]["data"][0].as_u64(), Some(0x55));
    let ai = s.send_ok("ai1", "s7_read", json!({ "connectionId": "c", "items": ["AIW0"] }));
    assert_eq!(ai["items"][0]["data"][0].as_u64(), Some(0x12));
    assert_eq!(ai["items"][0]["data"][1].as_u64(), Some(0x34));
    let aq = s.send_ok("aq1", "s7_read", json!({ "connectionId": "c", "items": ["AQW0"] }));
    assert_eq!(aq["items"][0]["data"][0].as_u64(), Some(0x56));
}

/// E2E 22(SW1):Fetch/Write 全流程(独立从站 127.0.0.1:port)
#[test]
fn s7_e2e_fetch_write_flow() {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("fw-slave", "start_fw_slave", json!({ "slaveId": "fw", "port": port, "seed": true }));
    std::thread::sleep(Duration::from_millis(200));
    s.send_ok("fw-conn", "open_fw_tcp", json!({
        "connectionId": "f", "host": "127.0.0.1", "port": port
    }));
    // 读 DB1 0..4 = AA BB CC DD(seed)
    let r = s.send_ok("fw-r1", "fw_read", json!({
        "connectionId": "f", "area": "DB", "db": 1, "address": 0, "length": 4
    }));
    let data = r["data"].as_array().unwrap();
    assert_eq!(data[0].as_u64(), Some(0xAA));
    assert_eq!(data[3].as_u64(), Some(0xDD));
    // 写 M50 → 读回
    s.send_ok("fw-w1", "fw_write", json!({
        "connectionId": "f", "area": "M", "address": 50, "values": [0xCA, 0xFE]
    }));
    let r2 = s.send_ok("fw-r2", "fw_read", json!({
        "connectionId": "f", "area": "M", "address": 50, "length": 2
    }));
    assert_eq!(r2["data"][0].as_u64(), Some(0xCA));
}

/// E2E 23(SW1):非法控制动作
#[test]
fn s7_e2e_bad_control_action() {
    let (_, mut s) = setup();
    let resp = s.send("ctl2", "s7_cpu_control", json!({
        "connectionId": "c", "action": "explode"
    }));
    assert_eq!(resp["ok"].as_bool(), Some(false));
}

/// E2E 24(SW2):PPI over TCP 双拍——读 VB0(V=DB1 seed 12 34 56 78)
#[test]
fn s7_e2e_ppi_read_v() {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("ppi-slave", "start_ppi_slave", json!({ "slaveId": "ppi", "port": port, "seed": true }));
    std::thread::sleep(Duration::from_millis(250));
    s.send_ok("ppi-conn", "open_ppi_tcp", json!({
        "connectionId": "p", "host": "127.0.0.1", "port": port, "station": 2
    }));
    let r = s.send_ok("ppi-r1", "ppi_read", json!({
        "connectionId": "p", "address": "VB0", "count": 4
    }));
    let item = &r["items"][0];
    assert_eq!(item["returnCode"].as_u64(), Some(0xFF));
    assert_eq!(item["data"].as_array().unwrap(), &vec![json!(0x12), json!(0x34), json!(0x56), json!(0x78)]);
}

/// E2E 25(SW2):PPI 写 VW100 → 读回 + 越界结束码
#[test]
fn s7_e2e_ppi_write_and_oob() {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("ppi-slave2", "start_ppi_slave", json!({ "slaveId": "ppi2", "port": port, "seed": true }));
    std::thread::sleep(Duration::from_millis(250));
    s.send_ok("ppi-conn2", "open_ppi_tcp", json!({ "connectionId": "p2", "host": "127.0.0.1", "port": port }));
    s.send_ok("ppi-w1", "ppi_write", json!({
        "connectionId": "p2", "address": "VW100", "count": 1, "values": [0xCA, 0xFE]
    }));
    let r = s.send_ok("ppi-r2", "ppi_read", json!({
        "connectionId": "p2", "address": "VW100", "count": 1
    }));
    assert_eq!(r["items"][0]["data"][0].as_u64(), Some(0xCA));
    // 越界 → Item RC 0x05
    let r3 = s.send_ok("ppi-r3", "ppi_read", json!({
        "connectionId": "p2", "address": "VB65534", "count": 4
    }));
    assert_eq!(r3["items"][0]["returnCode"].as_u64(), Some(0x05));
}

/// E2E 17:停从站后连接失败
#[test]
fn s7_e2e_stop_slave() {
    let (port, mut s) = setup();
    s.send_ok("stop1", "stop_s7_slave", json!({ "slaveId": "s7" }));
    s.send_ok("close1", "close_connection", json!({ "connectionId": "c" }));
    std::thread::sleep(Duration::from_millis(300));
    let resp = s.send("reconn", "open_s7_connection", json!({
        "connectionId": "c9", "host": "127.0.0.1", "port": port, "rack": 0, "slot": 1
    }));
    assert_eq!(resp["ok"].as_bool(), Some(false), "从站已停,连接应失败");
}

