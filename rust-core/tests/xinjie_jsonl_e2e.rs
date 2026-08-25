//! Xinje XC/XD address-profile JSONL E2E.
//! Only validates the confirmed D-register profile; other regions remain fail-closed.

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
            "command": "xinjie_parse_address",
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
fn xinjie_jsonl_maps_confirmed_d_for_xc_and_xd() {
    let mut sidecar = Sidecar::spawn();
    for (id, series) in [("xc", "xc"), ("xd", "xd")] {
        let result = sidecar.ok(id, json!({ "series": series, "address": "D100" }));
        assert_eq!(result["modbusAddress"].as_u64(), Some(100));
        assert_eq!(result["readFunction"].as_u64(), Some(3));
        assert_eq!(result["writeFunction"].as_u64(), Some(6));
        assert_eq!(
            result["underlyingProtocol"].as_str(),
            Some("Modbus RTU / Modbus TCP")
        );
    }
}

#[test]
fn xinjie_jsonl_fails_closed_for_unconfirmed_areas() {
    let mut sidecar = Sidecar::spawn();
    for (id, address, kind) in [
        ("x", "X10", "bit"),
        ("m", "M0", "auto"),
        ("hd", "HD0", "word"),
    ] {
        let response = sidecar.send(
            id,
            json!({ "series": "xc", "address": address, "kind": kind }),
        );
        assert_eq!(response["ok"].as_bool(), Some(false), "{response}");
        assert_eq!(
            response["error"]["code"].as_str(),
            Some("XINJE_ADDRESS_UNSUPPORTED")
        );
    }
}
