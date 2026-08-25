//! Delta DVP/AS Modbus profile JSONL E2E。
//! 只验证地址映射和功能码，不打开真实 Modbus 连接。

use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::time::Duration;

use serde_json::{Value, json};

struct Sidecar {
    child: Child,
    stdin: Option<ChildStdin>,
    lines: Receiver<String>,
}

impl Sidecar {
    fn spawn() -> Self {
        let mut child = Command::new(sidecar_binary())
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .expect("failed to start sidecar");
        let stdin = child.stdin.take().unwrap();
        let stdout = child.stdout.take().unwrap();
        let (tx, rx) = mpsc::channel();
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines().flatten() {
                if tx.send(line).is_err() {
                    break;
                }
            }
        });
        Self {
            child,
            stdin: Some(stdin),
            lines: rx,
        }
    }

    fn send(&mut self, id: &str, payload: Value) -> Value {
        let request = json!({ "protocolVersion": 1, "requestId": id, "command": "delta_parse_address", "payload": payload });
        let stdin = self.stdin.as_mut().unwrap();
        writeln!(stdin, "{}", request).unwrap();
        stdin.flush().unwrap();
        loop {
            let raw = self
                .lines
                .recv_timeout(Duration::from_secs(10))
                .expect("sidecar response timeout");
            let response: Value = serde_json::from_str(&raw).unwrap();
            if response.get("requestId").and_then(Value::as_str) == Some(id) {
                return response;
            }
        }
    }

    fn ok(&mut self, id: &str, payload: Value) -> Value {
        let response = self.send(id, payload);
        assert_eq!(response["ok"].as_bool(), Some(true), "{response}");
        response["result"].clone()
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
        .map(PathBuf::from)
        .expect("binary path is not available for integration test")
}

#[test]
fn delta_jsonl_maps_dvp_and_as_profiles() {
    let mut sidecar = Sidecar::spawn();
    let dvp = sidecar.ok("dvp", json!({ "series": "dvp", "address": "D4096" }));
    assert_eq!(dvp["modbusAddress"].as_u64(), Some(0x9000));
    assert_eq!(dvp["readFunction"].as_u64(), Some(3));
    let as_address = sidecar.ok("as", json!({ "series": "as", "address": "X1.2" }));
    assert_eq!(as_address["modbusAddress"].as_u64(), Some(0x6012));
    assert_eq!(as_address["readOnly"].as_bool(), Some(true));
}

#[test]
fn delta_jsonl_rejects_wrong_octals_and_unsafe_register_bit_access() {
    let mut sidecar = Sidecar::spawn();
    let bad_octal = sidecar.send("bad-octal", json!({ "series": "dvp", "address": "Y378" }));
    assert_eq!(bad_octal["ok"].as_bool(), Some(false));
    assert_eq!(
        bad_octal["error"]["code"].as_str(),
        Some("DELTA_PARAM_INVALID")
    );
    let bad_bit = sidecar.send("bad-bit", json!({ "series": "as", "address": "D100.5" }));
    assert_eq!(bad_bit["ok"].as_bool(), Some(false));
}
