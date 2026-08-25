//! Inovance H3U/H5U address-profile JSONL E2E.
//! Only validates the independent vendor profile; no live PLC connection.

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
        let request = json!({
            "protocolVersion": 1,
            "requestId": id,
            "command": "inovance_parse_address",
            "payload": payload,
        });
        let stdin = self.stdin.as_mut().unwrap();
        writeln!(stdin, "{request}").unwrap();
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
fn inovance_jsonl_maps_h3u_h5u_and_reports_underlying_modbus() {
    let mut sidecar = Sidecar::spawn();
    let h3u = sidecar.ok(
        "h3u",
        json!({ "series": "h3u", "address": "X10", "kind": "bit" }),
    );
    assert_eq!(h3u["modbusAddress"].as_u64(), Some(0xF808));
    assert_eq!(h3u["readFunction"].as_u64(), Some(1));
    assert_eq!(h3u["writeFunction"], Value::Null);
    assert_eq!(
        h3u["underlyingProtocol"].as_str(),
        Some("Modbus RTU / Modbus TCP")
    );
    let h5u = sidecar.ok(
        "h5u",
        json!({ "series": "h5u", "address": "Y1777", "kind": "bit" }),
    );
    assert_eq!(h5u["modbusAddress"].as_u64(), Some(0xFFFF));
    let counter = sidecar.ok(
        "counter",
        json!({ "series": "h3u", "address": "C200", "kind": "word" }),
    );
    assert_eq!(counter["registerWidth"].as_u64(), Some(2));
    assert_eq!(counter["writeFunction"].as_u64(), Some(16));
}

#[test]
fn inovance_jsonl_fails_closed_for_gaps_mismatched_kind_and_am() {
    let mut sidecar = Sidecar::spawn();
    let gap = sidecar.send(
        "gap",
        json!({ "series": "h3u", "address": "M7680", "kind": "bit" }),
    );
    assert_eq!(gap["ok"].as_bool(), Some(false));
    assert_eq!(
        gap["error"]["code"].as_str(),
        Some("INOVANCE_ADDRESS_UNSUPPORTED")
    );
    let mismatch = sidecar.send(
        "mismatch",
        json!({ "series": "h3u", "address": "M0", "kind": "word" }),
    );
    assert_eq!(mismatch["ok"].as_bool(), Some(false));
    let am = sidecar.send("am", json!({ "series": "am", "address": "D0" }));
    assert_eq!(am["ok"].as_bool(), Some(false));
    assert_eq!(
        am["error"]["code"].as_str(),
        Some("INOVANCE_SERIES_UNSUPPORTED")
    );
}
