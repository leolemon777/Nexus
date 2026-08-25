//! CJ/T 188-2004 JSONL offline codec boundary evidence.
//!
//! The fixed request/response vectors come from the audited mainstream 2004
//! worked examples (single 68H, arithmetic checksum, plain data). These tests
//! are not a real water/gas/heat meter L2 acceptance record.

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
            for line in BufReader::new(stdout).lines().map_while(Result::ok) {
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
        let request = json!({
            "protocolVersion": 1,
            "requestId": id,
            "command": command,
            "payload": payload,
        });
        let stdin = self.stdin.as_mut().unwrap();
        writeln!(stdin, "{request}").unwrap();
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
fn hello_advertises_only_the_cjt188_offline_read_only_codec_commands() {
    let mut sidecar = Sidecar::spawn();
    let hello = sidecar.ok("hello", "hello", json!({}));
    let capabilities = hello["capabilities"].as_array().unwrap();
    for command in [
        "cjt188_parse_meter_type",
        "cjt188_parse_address",
        "cjt188_parse_data_id",
        "cjt188_build_read_request",
        "cjt188_parse_frame",
        "cjt188_parse_read_response",
    ] {
        assert!(
            capabilities.iter().any(|entry| entry == command),
            "{command}"
        );
    }
    for absent in [
        "cjt188_write",
        "cjt188_set_address",
        "cjt188_valve_control",
        "cjt188_read_follow",
        "cjt188_read_address",
        "cjt188_serial_read",
    ] {
        assert!(
            !capabilities.iter().any(|entry| entry == absent),
            "{absent}"
        );
    }
}

#[test]
fn jsonl_builds_the_audited_request_and_decodes_the_worked_response() {
    let mut sidecar = Sidecar::spawn();
    let request = sidecar.ok(
        "build",
        "cjt188_build_read_request",
        json!({
            "meterType": "cold-water",
            "address": "11223344556677",
            "dataId": "901F",
            "sequence": 1,
            "preambleCount": 0
        }),
    );
    assert_eq!(
        request["frame"],
        json!([
            0x68, 0x10, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x01, 0x03, 0x90, 0x1F, 0x01,
            0x08, 0x16
        ])
    );
    assert_eq!(request["control"], 1);
    assert_eq!(request["readOnly"], true);

    let response = sidecar.ok(
        "parse",
        "cjt188_parse_read_response",
        json!({
            "meterType": "cold-water",
            "address": "11223344556677",
            "dataId": "901F",
            "sequence": 1,
            "frame": [
                0xFE, 0xFE, 0xFE, 0x68, 0x10, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
                0x81, 0x09, 0x90, 0x1F, 0x01, 0x78, 0x56, 0x34, 0x12, 0x00, 0xFF, 0xA1, 0x16
            ]
        }),
    );
    assert_eq!(response["response"]["control"], 0x81);
    assert_eq!(response["response"]["dataId"], "901F");
    assert_eq!(response["response"]["sequence"], 1);
    assert_eq!(
        response["response"]["knownValue"]["value"]["decimal"],
        "123456.78"
    );
    assert_eq!(response["response"]["knownValue"]["unit"], "m3");
}

#[test]
fn jsonl_keeps_heat_raw_error_payload_and_rejects_bad_frames() {
    let mut sidecar = Sidecar::spawn();
    let heat = sidecar.ok(
        "heat",
        "cjt188_parse_read_response",
        json!({
            "meterType": "heat",
            "address": "11223344556677",
            "dataId": "901F",
            "sequence": 1,
            "frame": [
                0x68, 0x20, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
                0x81, 0x09, 0x90, 0x1F, 0x01, 0x78, 0x56, 0x34, 0x12, 0x00, 0xFF, 0xB1, 0x16
            ]
        }),
    );
    assert!(heat["response"]["knownValue"].is_null());
    assert_eq!(
        heat["response"]["payload"],
        json!([0x78, 0x56, 0x34, 0x12, 0x00, 0xFF])
    );

    let error = sidecar.ok(
        "error",
        "cjt188_parse_read_response",
        json!({
            "meterType": "gas",
            "address": "11223344556677",
            "dataId": "901F",
            "sequence": 1,
            "frame": [
                0x68, 0x30, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
                0xC1, 0x04, 0x90, 0x1F, 0x01, 0x02, 0xEB, 0x16
            ]
        }),
    );
    assert_eq!(error["response"]["exception"]["control"], 0xC1);
    assert_eq!(error["response"]["exception"]["payload"], json!([0x02]));

    let bad_checksum = sidecar.send(
        "bad-cs",
        "cjt188_parse_frame",
        json!({
            "frame": [
                0x68, 0x10, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
                0x01, 0x03, 0x90, 0x1F, 0x01, 0x09, 0x16
            ]
        }),
    );
    assert_eq!(bad_checksum["error"]["code"], "CJT188_CHECKSUM_MISMATCH");

    let electric = sidecar.send(
        "electric",
        "cjt188_parse_meter_type",
        json!({ "meterType": "electric" }),
    );
    assert_eq!(electric["error"]["code"], "CJT188_METER_TYPE_UNCONFIRMED");

    let broadcast = sidecar.send(
        "broadcast",
        "cjt188_build_read_request",
        json!({
            "meterType": "gas",
            "address": "AAAAAAAAAAAAAA",
            "dataId": "901F",
            "sequence": 1
        }),
    );
    assert_eq!(
        broadcast["error"]["code"],
        "CJT188_BROADCAST_READ_FORBIDDEN"
    );
}
