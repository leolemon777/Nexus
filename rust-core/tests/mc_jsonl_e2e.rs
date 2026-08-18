//! 三菱 MC 协议 JSONL E2E:启动 sidecar → 启动 MC 虚拟从站 → JSONL 命令在线读写。
//! 验证整条链路:JSONL 命令 → mc_pdu 组帧 → TCP → mc_slave 从站 → 响应解析。
//!
//! cargo test --test mc_jsonl_e2e -- --test-threads=1

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
static PORT_GEN: AtomicU16 = AtomicU16::new(16030);
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

fn setup() -> (u16, Sidecar) {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("mc-slave", "start_mc_tcp_slave", json!({
        "slaveId": "mc", "port": port, "seed": true
    }));
    std::thread::sleep(Duration::from_millis(200));
    s.send_ok("mc-conn", "open_mc_tcp_connection", json!({
        "connectionId": "c", "host": "127.0.0.1", "port": port
    }));
    (port, s)
}

/// E2E:读 D100(seed 预置 0x1234)
#[test]
fn mc_e2e_read_d100() {
    let (_, mut s) = setup();
    let r = s.send_ok("r1", "mc_tcp_read", json!({
        "connectionId": "c", "address": "D100", "points": 2
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0x1234), "D100 应为 0x1234");
    assert_eq!(values[1].as_u64(), Some(0xABCD), "D101 应为 0xABCD");
}

/// E2E:读 M0~M11 交替位(seed 预置)
#[test]
fn mc_e2e_read_m_bits() {
    let (_, mut s) = setup();
    let r = s.send_ok("r2", "mc_tcp_read", json!({
        "connectionId": "c", "address": "M0", "points": 12
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    assert_eq!(r["isBit"].as_bool(), Some(true));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values.len(), 12);
    for i in 0..12 {
        assert_eq!(values[i].as_u64(), Some((i % 2 == 0) as u64), "M{i}");
    }
}

/// E2E:写 D500 → 读回
#[test]
fn mc_e2e_write_read_roundtrip() {
    let (_, mut s) = setup();
    let w = s.send_ok("w1", "mc_tcp_write", json!({
        "connectionId": "c", "address": "D500", "values": [0xCAFE, 0xBABE, 255]
    }));
    assert_eq!(w["endCode"].as_u64(), Some(0));

    let r = s.send_ok("r3", "mc_tcp_read", json!({
        "connectionId": "c", "address": "D500", "points": 3
    }));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0xCAFE));
    assert_eq!(values[1].as_u64(), Some(0xBABE));
    assert_eq!(values[2].as_u64(), Some(255));
}

/// E2E:写 M100 = ON → 读回
#[test]
fn mc_e2e_write_bit() {
    let (_, mut s) = setup();
    s.send_ok("w2", "mc_tcp_write", json!({
        "connectionId": "c", "address": "M100", "values": [1, 0, 1]
    }));
    let r = s.send_ok("r4", "mc_tcp_read", json!({
        "connectionId": "c", "address": "M100", "points": 3
    }));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(1));
    assert_eq!(values[1].as_u64(), Some(0));
    assert_eq!(values[2].as_u64(), Some(1));
}

/// E2E:4E 帧模式往返
#[test]
fn mc_e2e_frame_4e() {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("s4", "start_mc_tcp_slave", json!({ "slaveId": "mc4", "port": port, "seed": true }));
    std::thread::sleep(Duration::from_millis(200));
    s.send_ok("c4", "open_mc_tcp_connection", json!({
        "connectionId": "c4e", "host": "127.0.0.1", "port": port, "frameType": "4e"
    }));
    let r = s.send_ok("r5", "mc_tcp_read", json!({
        "connectionId": "c4e", "address": "D100", "points": 1
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    assert_eq!(r["values"][0].as_u64(), Some(0x1234));
}

/// E2E:越界地址 → 业务错误经结束代码返回(ok=true + endCode=D2)
#[test]
fn mc_e2e_out_of_range_end_code() {
    let (_, mut s) = setup();
    let resp = s.send("r6", "mc_tcp_read", json!({
        "connectionId": "c", "address": "D16777215", "points": 1
    }));
    // 帧收发成功(ok=true),PLC 返回结束代码 D2
    assert!(resp.get("ok").and_then(|v| v.as_bool()) == Some(true));
    assert_eq!(resp["result"]["endCode"].as_u64(), Some(0x00D2));
}

/// E2E:离线命令 mc_build_read 输出完整帧字节
#[test]
fn mc_e2e_offline_build_read_frame() {
    let mut s = Sidecar::spawn();
    let r = s.send_ok("b1", "mc_build_read", json!({ "address": "D100", "points": 1 }));
    let frame = r["frame"].as_array().unwrap();
    // 文档 §2.1.4-(2) 完整 21 字节帧
    let expected = [0x50u8, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x0C, 0x00, 0x10, 0x00,
                    0x01, 0x04, 0x01, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00];
    assert_eq!(frame.len(), expected.len());
    for (i, b) in expected.iter().enumerate() {
        assert_eq!(frame[i].as_u64(), Some(*b as u64), "帧字节 {i} 不匹配");
    }
}

/// E2E:mc_parse_address 离线解析
#[test]
fn mc_e2e_parse_address_command() {
    let mut s = Sidecar::spawn();
    let r = s.send_ok("a1", "mc_parse_address", json!({ "address": "D100" }));
    assert_eq!(r["deviceCode"].as_u64(), Some(0xA8));
    assert_eq!(r["headNumber"].as_u64(), Some(100));
    assert_eq!(r["isBit"].as_bool(), Some(false));
    // 八进制 X10 = 8
    let r = s.send_ok("a2", "mc_parse_address", json!({ "address": "X10" }));
    assert_eq!(r["headNumber"].as_u64(), Some(8));
}

/// E2E:非法地址返回错误(ok=false + MC_ADDRESS_INVALID)
#[test]
fn mc_e2e_invalid_address_error() {
    let mut s = Sidecar::spawn();
    let resp = s.send("a3", "mc_parse_address", json!({ "address": "X8" }));
    assert!(resp.get("ok").and_then(|v| v.as_bool()) == Some(false));
    assert_eq!(resp["error"]["code"], "MC_ADDRESS_INVALID");
}

/// E2E:mc_slave_set 远程设值后读取
#[test]
fn mc_e2e_slave_set_then_read() {
    let (_, mut s) = setup();
    s.send_ok("ss", "mc_slave_set", json!({
        "slaveId": "mc", "device": "D", "start": 300, "values": [111, 222, 333]
    }));
    let r = s.send_ok("r7", "mc_tcp_read", json!({
        "connectionId": "c", "address": "D300", "points": 3
    }));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(111));
    assert_eq!(values[1].as_u64(), Some(222));
    assert_eq!(values[2].as_u64(), Some(333));
}

// ===== M2 进阶指令 E2E =====

/// E2E:随机读 D100、D200(seed 值)
#[test]
fn mc_e2e_read_random() {
    let (_, mut s) = setup();
    let r = s.send_ok("rr", "mc_tcp_read_random", json!({
        "connectionId": "c", "addresses": ["D100", "D200"]
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0x1234), "D100");
    assert_eq!(values[1].as_u64(), Some(0xBEEF), "D200");
}

/// E2E:随机写 D600=0xCAFE, D601=0xBABE → 成批读回验证
#[test]
fn mc_e2e_write_random_then_read() {
    let (_, mut s) = setup();
    s.send_ok("wr", "mc_tcp_write_random", json!({
        "connectionId": "c",
        "entries": [ { "address": "D600", "value": 0xCAFE }, { "address": "D601", "value": 0xBABE } ]
    }));
    let r = s.send_ok("rd", "mc_tcp_read", json!({
        "connectionId": "c", "address": "D600", "points": 2
    }));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0xCAFE));
    assert_eq!(values[1].as_u64(), Some(0xBABE));
}

/// E2E:多块读 D100(2字)+ D200(1字)
#[test]
fn mc_e2e_read_blocks() {
    let (_, mut s) = setup();
    let r = s.send_ok("rb", "mc_tcp_read_blocks", json!({
        "connectionId": "c",
        "blocks": [ { "address": "D100", "points": 2 }, { "address": "D200", "points": 1 } ]
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    let blocks = r["blocks"].as_array().unwrap();
    assert_eq!(blocks.len(), 2);
    let b0 = blocks[0].as_array().unwrap();
    let b1 = blocks[1].as_array().unwrap();
    assert_eq!(b0[0].as_u64(), Some(0x1234));
    assert_eq!(b0[1].as_u64(), Some(0xABCD));
    assert_eq!(b1[0].as_u64(), Some(0xBEEF));
}

/// E2E:远程 RUN/STOP(虚拟从站接受,endCode=0)
#[test]
fn mc_e2e_remote_run_stop() {
    let (_, mut s) = setup();
    let r = s.send_ok("run", "mc_remote_run", json!({ "connectionId": "c" }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    let r = s.send_ok("stop", "mc_remote_stop", json!({ "connectionId": "c" }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
}

/// E2E:读时钟(虚拟从站返回 2026-08-15 14:30:00 周五)
#[test]
fn mc_e2e_read_clock() {
    let (_, mut s) = setup();
    let r = s.send_ok("clk", "mc_read_clock", json!({ "connectionId": "c" }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    assert_eq!(r["clock"]["year"].as_u64(), Some(26), "BCD 年两位(0x26=20xx 的 26)");
    assert_eq!(r["clock"]["month"].as_u64(), Some(8));
    assert_eq!(r["clock"]["day"].as_u64(), Some(15));
    assert_eq!(r["clock"]["hour"].as_u64(), Some(14));
    assert_eq!(r["clock"]["weekday"].as_u64(), Some(5), "周五");
}

/// E2E:回送测试(默认载荷 ABCDEF)
#[test]
fn mc_e2e_echo_test() {
    let (_, mut s) = setup();
    let r = s.send_ok("echo", "mc_echo_test", json!({ "connectionId": "c" }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    assert_eq!(r["matched"].as_bool(), Some(true), "回送数据应一致");
}

/// E2E:读 CPU 型号与状态
#[test]
fn mc_e2e_cpu_type_status() {
    let (_, mut s) = setup();
    let r = s.send_ok("ct", "mc_read_cpu_type", json!({ "connectionId": "c" }));
    assert_eq!(r["cpuType"].as_str(), Some("Nexus-Rust-VM"));
    let r = s.send_ok("cs", "mc_read_cpu_status", json!({ "connectionId": "c" }));
    assert_eq!(r["cpuStatus"].as_str(), Some("RUN"));
}

/// E2E:ASCII 帧离线构建(文档 §2.3.2 向量)
#[test]
fn mc_e2e_build_ascii() {
    let mut s = Sidecar::spawn();
    let r = s.send_ok("ab", "mc_build_ascii_read", json!({ "address": "D100", "points": 1 }));
    assert_eq!(
        r["ascii"].as_str(),
        Some("500000FFFF0300000C001004010001000064A80001")
    );
}

// ===== ASCII 模式在线 E2E =====

fn setup_ascii(port: Option<u16>) -> (u16, Sidecar) {
    let mut s = Sidecar::spawn();
    let port = port.unwrap_or_else(next_port);
    s.send_ok("as", "start_mc_tcp_slave", json!({ "slaveId": "mca", "port": port, "seed": true }));
    std::thread::sleep(Duration::from_millis(200));
    s.send_ok("ac", "open_mc_ascii_connection", json!({
        "connectionId": "ca", "host": "127.0.0.1", "port": port, "frameType": "3e"
    }));
    (port, s)
}

/// ASCII 读 D100(seed 0x1234)→ 响应 "1234"
#[test]
fn mc_ascii_e2e_read_d100() {
    let (_, mut s) = setup_ascii(None);
    let r = s.send_ok("ar", "mc_ascii_read", json!({
        "connectionId": "ca", "address": "D100", "points": 2
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    assert_eq!(r["isBit"].as_bool(), Some(false));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0x1234));
    assert_eq!(values[1].as_u64(), Some(0xABCD));
}

/// ASCII 位读 M0~M5(seed 交替)
#[test]
fn mc_ascii_e2e_read_bits() {
    let (_, mut s) = setup_ascii(None);
    let r = s.send_ok("ab", "mc_ascii_read", json!({
        "connectionId": "ca", "address": "M0", "points": 5
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    assert_eq!(r["isBit"].as_bool(), Some(true));
    let values = r["values"].as_array().unwrap();
    for i in 0..5 {
        assert_eq!(values[i].as_u64(), Some((i % 2 == 0) as u64), "M{i}");
    }
}

/// ASCII 写 D700 → 读回
#[test]
fn mc_ascii_e2e_write_read() {
    let (_, mut s) = setup_ascii(None);
    let w = s.send_ok("aw", "mc_ascii_write", json!({
        "connectionId": "ca", "address": "D700", "values": [0xCAFE, 0xBABE]
    }));
    assert_eq!(w["endCode"].as_u64(), Some(0));
    let r = s.send_ok("av", "mc_ascii_read", json!({
        "connectionId": "ca", "address": "D700", "points": 2
    }));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0xCAFE));
    assert_eq!(values[1].as_u64(), Some(0xBABE));
}

/// Binary 连接上调 ASCII 命令 → 明确报错
#[test]
fn mc_ascii_on_binary_conn_rejected() {
    let (_, mut s) = setup();
    let resp = s.send("ax", "mc_ascii_read", json!({
        "connectionId": "c", "address": "D100", "points": 1
    }));
    assert!(resp.get("ok").and_then(|v| v.as_bool()) != Some(true));
}

// ===== MC UDP E2E =====

fn setup_udp() -> (u16, Sidecar) {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("us", "start_mc_tcp_slave", json!({ "slaveId": "mcu", "port": port, "seed": true }));
    std::thread::sleep(Duration::from_millis(200));
    s.send_ok("uc", "open_mc_udp_connection", json!({
        "connectionId": "cu", "host": "127.0.0.1", "port": port, "frameType": "3e"
    }));
    (port, s)
}

/// UDP 读 D100(seed 0x1234)
#[test]
fn mc_udp_e2e_read_d100() {
    let (_, mut s) = setup_udp();
    let r = s.send_ok("ur", "mc_udp_read", json!({
        "connectionId": "cu", "address": "D100", "points": 2
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0x1234));
    assert_eq!(values[1].as_u64(), Some(0xABCD));
}

/// UDP 写 → 读回
#[test]
fn mc_udp_e2e_write_read() {
    let (_, mut s) = setup_udp();
    let w = s.send_ok("uw", "mc_udp_write", json!({
        "connectionId": "cu", "address": "D800", "values": [0x1111, 0x2222]
    }));
    assert_eq!(w["endCode"].as_u64(), Some(0));
    let r = s.send_ok("uv", "mc_udp_read", json!({
        "connectionId": "cu", "address": "D800", "points": 2
    }));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0x1111));
    assert_eq!(values[1].as_u64(), Some(0x2222));
}

/// UDP 4E 帧序列号配对(正常路径)
#[test]
fn mc_udp_e2e_4e() {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("us4", "start_mc_tcp_slave", json!({ "slaveId": "mcu4", "port": port, "seed": true }));
    std::thread::sleep(Duration::from_millis(200));
    s.send_ok("uc4", "open_mc_udp_connection", json!({
        "connectionId": "cu4", "host": "127.0.0.1", "port": port, "frameType": "4e"
    }));
    let r = s.send_ok("ur4", "mc_udp_read", json!({
        "connectionId": "cu4", "address": "D100", "points": 1
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    assert_eq!(r["values"][0].as_u64(), Some(0x1234));
}

// ===== A-1E / SLMP-1E over TCP E2E =====

fn setup_1e() -> (u16, Sidecar) {
    let mut s = Sidecar::spawn();
    let port = next_port();
    s.send_ok("s1e", "start_mc_tcp_slave", json!({ "slaveId": "mc1e", "port": port, "seed": true }));
    std::thread::sleep(Duration::from_millis(200));
    s.send_ok("c1e", "open_mc_1e_tcp", json!({
        "connectionId": "c1", "host": "127.0.0.1", "port": port
    }));
    (port, s)
}

/// 1E 字读 D100(seed 0x1234)
#[test]
fn mc_1e_e2e_read_d100() {
    let (_, mut s) = setup_1e();
    let r = s.send_ok("r1e", "mc_1e_read", json!({
        "connectionId": "c1", "address": "D100", "points": 2
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    assert_eq!(r["isBit"].as_bool(), Some(false));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0x1234));
    assert_eq!(values[1].as_u64(), Some(0xABCD));
}

/// 1E 位读 M0~M5(seed 交替)
#[test]
fn mc_1e_e2e_read_bits() {
    let (_, mut s) = setup_1e();
    let r = s.send_ok("rb1", "mc_1e_read", json!({
        "connectionId": "c1", "address": "M0", "points": 5
    }));
    assert_eq!(r["endCode"].as_u64(), Some(0));
    assert_eq!(r["isBit"].as_bool(), Some(true));
    let values = r["values"].as_array().unwrap();
    for i in 0..5 {
        assert_eq!(values[i].as_u64(), Some((i % 2 == 0) as u64), "M{i}");
    }
}

/// 1E 字写 + 读回
#[test]
fn mc_1e_e2e_write_read() {
    let (_, mut s) = setup_1e();
    let w = s.send_ok("w1e", "mc_1e_write", json!({
        "connectionId": "c1", "address": "D900", "values": [0xCAFE, 42]
    }));
    assert_eq!(w["endCode"].as_u64(), Some(0));
    let r = s.send_ok("rv1", "mc_1e_read", json!({
        "connectionId": "c1", "address": "D900", "points": 2
    }));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(0xCAFE));
    assert_eq!(values[1].as_u64(), Some(42));
}

/// 1E 位写 + 读回
#[test]
fn mc_1e_e2e_write_bits() {
    let (_, mut s) = setup_1e();
    s.send_ok("wb1", "mc_1e_write", json!({
        "connectionId": "c1", "address": "M200", "values": [1, 0, 1]
    }));
    let r = s.send_ok("rbb", "mc_1e_read", json!({
        "connectionId": "c1", "address": "M200", "points": 3
    }));
    let values = r["values"].as_array().unwrap();
    assert_eq!(values[0].as_u64(), Some(1));
    assert_eq!(values[1].as_u64(), Some(0));
    assert_eq!(values[2].as_u64(), Some(1));
}

/// 1E 软元件代号错误 → 0x50
#[test]
fn mc_1e_e2e_bad_device() {
    let (_, mut s) = setup_1e();
    let resp = s.send("bd1", "mc_1e_read", json!({
        "connectionId": "c1", "address": "Q100", "points": 1
    }));
    // 组帧侧就拒绝(0x50 或 JSONL error)
    assert!(resp.get("ok").and_then(|v| v.as_bool()) != Some(true)
        || resp["result"]["endCode"].as_u64() == Some(0x50));
}
