//! GE SRTP JSONL E2E; software TCP peer only, no PLC state inference or writes.

use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpListener;
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

    fn send(&mut self, id: &str, command: &str, payload: Value) -> Value {
        let request = json!({ "protocolVersion": 1, "requestId": id, "command": command, "payload": payload });
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
fn ge_jsonl_maps_addresses_and_builds_session_read_write_frames() {
    let mut sidecar = Sidecar::spawn();
    let address = sidecar.ok(
        "address",
        "ge_srtp_parse_address",
        json!({ "address": "%M1" }),
    );
    assert_eq!(address["offset"].as_u64(), Some(0));
    assert_eq!(address["bitDataCodeHex"].as_str(), Some("0x4C"));
    assert_eq!(address["defaultPort"].as_u64(), Some(18245));

    let handshake = sidecar.ok("handshake", "ge_srtp_build_handshake", json!({}));
    assert_eq!(handshake["frame"].as_array().unwrap().len(), 56);
    assert!(
        handshake["frame"]
            .as_array()
            .unwrap()
            .iter()
            .all(|byte| byte == 0)
    );

    let read = sidecar.ok(
        "read",
        "ge_srtp_build_read",
        json!({
            "transactionId": 0x1234,
            "address": "AI10",
            "elementCount": 2,
            "bitAccess": false,
        }),
    );
    assert_eq!(read["frame"].as_array().unwrap().len(), 56);
    assert_eq!(
        read["frameHex"].as_str().unwrap().split_whitespace().nth(2),
        Some("34")
    );

    let write = sidecar.ok(
        "write",
        "ge_srtp_build_write",
        json!({
            "transactionId": 7,
            "address": "M1",
            "data": [170, 85],
            "elementCount": 2,
            "bitAccess": false,
        }),
    );
    assert_eq!(write["frame"].as_array().unwrap().len(), 58);
    assert_eq!(write["dataBytes"].as_u64(), Some(2));
}

#[test]
fn ge_jsonl_parses_handshake_responses_and_rejects_mismatches() {
    let mut sidecar = Sidecar::spawn();
    let mut handshake = vec![0u8; 56];
    handshake[0] = 1;
    handshake[8] = 0x0F;
    let parsed = sidecar.ok(
        "hs",
        "ge_srtp_parse_handshake",
        json!({ "response": handshake }),
    );
    assert_eq!(parsed["sessionInitialized"].as_bool(), Some(true));

    let mut short = vec![0u8; 56];
    short[0] = 3;
    short[2..4].copy_from_slice(&7u16.to_le_bytes());
    short[31] = 0xD4;
    short[44..46].copy_from_slice(&[0x34, 0x12]);
    let short_result = sidecar.ok(
        "short",
        "ge_srtp_parse_response",
        json!({
            "transactionId": 7,
            "expectedDataLength": 2,
            "response": short,
        }),
    );
    assert_eq!(short_result["responseForm"].as_str(), Some("short"));
    assert_eq!(short_result["dataHex"].as_str(), Some("34 12"));

    let mut long = vec![0u8; 58];
    long[0] = 3;
    long[2..4].copy_from_slice(&8u16.to_le_bytes());
    long[4..6].copy_from_slice(&2u16.to_le_bytes());
    long[31] = 0x94;
    long[56..].copy_from_slice(&[0x78, 0x56]);
    let long_result = sidecar.ok(
        "long",
        "ge_srtp_parse_response",
        json!({
            "transactionId": 8,
            "expectedDataLength": 2,
            "response": long,
        }),
    );
    assert_eq!(long_result["responseForm"].as_str(), Some("long"));
    assert_eq!(long_result["dataHex"].as_str(), Some("78 56"));

    let bad = sidecar.send(
        "bad",
        "ge_srtp_parse_response",
        json!({
            "transactionId": 7,
            "expectedDataLength": 2,
            "response": [0, 0, 0],
        }),
    );
    assert_eq!(bad["ok"].as_bool(), Some(false), "{bad}");
}

#[test]
fn ge_jsonl_opens_tcp_session_and_performs_read_only_transaction() {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind GE SRTP test peer");
    let port = listener.local_addr().unwrap().port();
    let peer = std::thread::spawn(move || {
        let (mut stream, _) = listener.accept().expect("accept GE SRTP test client");
        let mut handshake = [0u8; 56];
        stream.read_exact(&mut handshake).unwrap();
        assert_eq!(handshake, [0u8; 56]);

        let mut handshake_response = [0u8; 56];
        handshake_response[0] = 0x01;
        handshake_response[8] = 0x0F;
        stream.write_all(&handshake_response).unwrap();

        let mut request = [0u8; 56];
        stream.read_exact(&mut request).unwrap();
        assert_eq!(request[0], 0x02);
        assert_eq!(request[42], 0x04);
        assert_eq!(request[43], 0x08); // R word area
        assert_eq!(&request[44..46], &[99, 0]); // R100 is encoded as offset 99
        let transaction_id = u16::from_le_bytes([request[2], request[3]]);

        let mut response = vec![0u8; 58];
        response[0] = 0x03;
        response[2..4].copy_from_slice(&transaction_id.to_le_bytes());
        response[4..6].copy_from_slice(&2u16.to_le_bytes());
        response[31] = 0x94; // long response with two data bytes
        response[56..].copy_from_slice(&[0x34, 0x12]);
        stream.write_all(&response).unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_ge_srtp_connection",
        json!({
            "connectionId": "ge-test",
            "host": "127.0.0.1",
            "port": port,
        }),
    );
    assert_eq!(opened["sessionInitialized"].as_bool(), Some(true));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));

    let read = sidecar.ok(
        "live-read",
        "ge_srtp_read",
        json!({
            "connectionId": "ge-test",
            "address": "R100",
            "elementCount": 1,
        }),
    );
    assert_eq!(read["responseForm"].as_str(), Some("long"));
    assert_eq!(read["dataHex"].as_str(), Some("34 12"));
    assert_eq!(read["readOnly"].as_bool(), Some(true));

    let closed = sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "ge-test" }),
    );
    assert_eq!(closed["closed"].as_bool(), Some(true));
    peer.join().expect("GE SRTP test peer failed");
}
