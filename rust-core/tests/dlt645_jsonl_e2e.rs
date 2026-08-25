//! DL/T 645 JSONL boundary evidence.
//!
//! These tests exercise the compiled sidecar protocol envelope and fixed
//! independent byte vectors. Shared-COM fragmented/local-echo behavior is
//! covered by electron/serial-service.test.cjs; neither proof is a real-meter
//! L2 acceptance record.

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
fn hello_advertises_only_the_five_dlt645_read_only_codec_commands() {
    let mut sidecar = Sidecar::spawn();
    let hello = sidecar.ok("hello", "hello", json!({}));
    let capabilities = hello["capabilities"].as_array().unwrap();
    for command in [
        "dlt645_parse_address",
        "dlt645_parse_data_id",
        "dlt645_build_read_request",
        "dlt645_parse_frame",
        "dlt645_parse_read_response",
    ] {
        assert!(
            capabilities.iter().any(|entry| entry == command),
            "{command}"
        );
    }
    for forbidden in [
        "dlt645_write",
        "dlt645_set_time",
        "dlt645_set_address",
        "dlt645_freeze",
        "dlt645_breaker_control",
    ] {
        assert!(!capabilities.iter().any(|entry| entry == forbidden));
    }
}

#[test]
fn jsonl_builds_fixed_2007_and_1997_read_vectors() {
    let mut sidecar = Sidecar::spawn();
    let request_2007 = sidecar.ok(
        "build-07",
        "dlt645_build_read_request",
        json!({
            "version": "2007",
            "address": "123456789012",
            "dataId": "00010000",
            "preambleCount": 4
        }),
    );
    assert_eq!(
        request_2007["frame"],
        json!([
            0xFE, 0xFE, 0xFE, 0xFE, 0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0x11, 0x04,
            0x33, 0x33, 0x34, 0x33, 0x68, 0x16
        ])
    );
    assert_eq!(request_2007["readOnly"], true);

    let request_1997 = sidecar.ok(
        "build-97",
        "dlt645_build_read_request",
        json!({
            "version": "1997",
            "address": "123456789012",
            "dataId": "9010",
            "preambleCount": 0
        }),
    );
    assert_eq!(
        request_1997["frame"],
        json!([
            0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0x01, 0x02, 0x43, 0xC3, 0x8F, 0x16
        ])
    );
}

#[test]
fn jsonl_decodes_version_specific_energy_and_exception_responses() {
    let mut sidecar = Sidecar::spawn();
    let response_2007 = sidecar.ok(
        "parse-07",
        "dlt645_parse_read_response",
        json!({
            "version": "2007",
            "address": "123456789012",
            "dataId": "00010000",
            "frame": [
                0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0x91, 0x08,
                0x33, 0x33, 0x34, 0x33, 0xAB, 0x89, 0x67, 0x45, 0xCC, 0x16
            ]
        }),
    );
    assert_eq!(
        response_2007["response"]["knownValue"]["value"]["decimal"],
        "123456.78"
    );
    assert_eq!(response_2007["response"]["dataId"], "00010000");

    let response_1997 = sidecar.ok(
        "parse-97",
        "dlt645_parse_read_response",
        json!({
            "version": "1997",
            "address": "123456789012",
            "dataId": "9010",
            "frame": [
                0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0x81, 0x06,
                0x43, 0xC3, 0x33, 0x33, 0x34, 0x33, 0xE0, 0x16
            ]
        }),
    );
    assert_eq!(
        response_1997["response"]["knownValue"]["value"]["decimal"],
        "000100.00"
    );

    let exception = sidecar.ok(
        "parse-error",
        "dlt645_parse_read_response",
        json!({
            "version": "2007",
            "address": "123456789012",
            "dataId": "00010000",
            "frame": [
                0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0xD1, 0x01,
                0x39, 0x91, 0x16
            ]
        }),
    );
    assert_eq!(exception["response"]["exception"]["code"], 6);
    assert_eq!(
        exception["response"]["exception"]["flags"],
        json!(["noRequestedData", "passwordOrAuthorizationError"])
    );
}

#[test]
fn jsonl_rejects_bad_checksum_address_identifier_and_cross_revision_input() {
    let mut sidecar = Sidecar::spawn();
    let bad_checksum = sidecar.send(
        "bad-cs",
        "dlt645_parse_frame",
        json!({
            "frame": [
                0x68, 0x12, 0x90, 0x78, 0x56, 0x34, 0x12, 0x68, 0x91, 0x08,
                0x33, 0x33, 0x34, 0x33, 0xAB, 0x89, 0x67, 0x45, 0xCD, 0x16
            ]
        }),
    );
    assert_eq!(bad_checksum["error"]["code"], "DLT645_CHECKSUM_MISMATCH");

    let broadcast = sidecar.send(
        "broadcast",
        "dlt645_build_read_request",
        json!({
            "version": "2007",
            "address": "999999999999",
            "dataId": "00010000",
            "preambleCount": 4
        }),
    );
    assert_eq!(
        broadcast["error"]["code"],
        "DLT645_BROADCAST_READ_FORBIDDEN"
    );

    let wrong_identifier_size = sidecar.send(
        "wrong-di",
        "dlt645_parse_data_id",
        json!({ "version": "1997", "dataId": "00010000" }),
    );
    assert_eq!(
        wrong_identifier_size["error"]["code"],
        "DLT645_DATA_ID_LENGTH_INVALID"
    );
}
