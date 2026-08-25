//! Panasonic MEWTOCOL-COM JSONL E2E。
//!
//! 只验证 ASCII/BCC 编解码和 Rust 路由，不打开真实 COM。

use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::thread;
use std::time::Duration;

use serde_json::{Value, json};

const RESPONSE_TIMEOUT: Duration = Duration::from_secs(10);

struct Sidecar {
    child: Child,
    stdin: Option<ChildStdin>,
    stdout_lines: Receiver<String>,
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
        thread::spawn(move || {
            for line in BufReader::new(stdout).lines().flatten() {
                if tx.send(line).is_err() {
                    break;
                }
            }
        });
        Self {
            child,
            stdin: Some(stdin),
            stdout_lines: rx,
        }
    }

    fn send(&mut self, id: &str, command: &str, payload: Value) -> Value {
        let request = json!({ "protocolVersion": 1, "requestId": id, "command": command, "payload": payload });
        let stdin = self.stdin.as_mut().unwrap();
        stdin.write_all(request.to_string().as_bytes()).unwrap();
        stdin.write_all(b"\n").unwrap();
        stdin.flush().unwrap();
        loop {
            let raw = self
                .stdout_lines
                .recv_timeout(RESPONSE_TIMEOUT)
                .expect("sidecar response timeout");
            let response: Value = serde_json::from_str(&raw).unwrap();
            if response.get("requestId").and_then(Value::as_str) == Some(id) {
                return response;
            }
        }
    }

    fn ok(&mut self, id: &str, command: &str, payload: Value) -> Value {
        let response = self.send(id, command, payload);
        assert!(
            response["ok"].as_bool() == Some(true),
            "{command} failed: {response}"
        );
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
fn panasonic_jsonl_builds_official_ascii_vectors() {
    let mut sidecar = Sidecar::spawn();
    let data = sidecar.ok(
        "data",
        "panasonic_parse_data_address",
        json!({ "address": "DT100" }),
    );
    assert_eq!(data["areaCode"].as_str(), Some("D"));
    assert_eq!(data["address"].as_u64(), Some(100));
    let contact = sidecar.ok(
        "contact",
        "panasonic_parse_contact_address",
        json!({ "address": "X1F" }),
    );
    assert_eq!(contact["contactNumber"].as_str(), Some("001F"));

    let read = sidecar.ok(
        "read",
        "panasonic_build_read",
        json!({ "station": 1, "address": "DT100", "wordCount": 1 }),
    );
    assert_eq!(read["text"].as_str(), Some("%01#RDD001000010055\r"));
    let write = sidecar.ok(
        "write",
        "panasonic_build_write",
        json!({ "station": 1, "address": "DT100", "data": [100, 0] }),
    );
    assert_eq!(write["text"].as_str(), Some("%01#WDD0010000100640052\r"));
    let rcs = sidecar.ok(
        "rcs",
        "panasonic_build_read_contact",
        json!({ "station": 1, "address": "X1F" }),
    );
    assert_eq!(rcs["text"].as_str(), Some("%01#RCSX001F6A\r"));
}

#[test]
fn panasonic_jsonl_parses_response_and_rejects_unsafe_input() {
    let mut sidecar = Sidecar::spawn();
    let response = sidecar.ok(
        "response",
        "panasonic_parse_response",
        json!({
            "station": 1, "expectedCommand": "RD", "expectedHeader": "%",
            "response": b"%01$RD640014\r"
        }),
    );
    assert_eq!(response["payload"].as_str(), Some("6400"));

    let bad = sidecar.send(
        "bad",
        "panasonic_build_read",
        json!({ "station": 0, "address": "D0", "wordCount": 1 }),
    );
    assert_eq!(bad["ok"].as_bool(), Some(false));
    assert_eq!(
        bad["error"]["code"].as_str(),
        Some("PANASONIC_PARAM_INVALID")
    );
    let bad_addr = sidecar.send(
        "bad-address",
        "panasonic_build_read",
        json!({ "station": 1, "address": "D99999", "wordCount": 2 }),
    );
    assert_eq!(bad_addr["ok"].as_bool(), Some(false));
}
