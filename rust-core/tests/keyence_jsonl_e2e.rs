//! Keyence KV Host Link ASCII JSONL E2E; software TCP peer only, no PLC control.

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

fn read_cr(socket: &mut std::net::TcpStream) -> Vec<u8> {
    let mut frame = Vec::new();
    loop {
        let mut byte = [0u8; 1];
        socket.read_exact(&mut byte).unwrap();
        frame.push(byte[0]);
        if byte[0] == b'\r' {
            return frame;
        }
    }
}

#[test]
fn keyence_jsonl_builds_addresses_and_ascii_frames() {
    let mut sidecar = Sidecar::spawn();
    let address = sidecar.ok(
        "address",
        "keyence_parse_address",
        json!({ "address": "MR10.1" }),
    );
    assert_eq!(address["commandAddress"].as_str(), Some("MR1001"));
    let read = sidecar.ok(
        "read",
        "keyence_build_read_words",
        json!({ "address": "DM0", "count": 2 }),
    );
    assert_eq!(read["text"].as_str(), Some("RDS DM0.U 2\r"));
    assert_eq!(read["frameHex"].as_str(), Some("52445320444D302E5520320D"));
    let bits = sidecar.ok(
        "bits",
        "keyence_build_read_bits",
        json!({ "address": "MR10.1", "count": 3 }),
    );
    assert_eq!(bits["text"].as_str(), Some("RDS MR1001 3\r"));
}

#[test]
fn keyence_jsonl_parses_responses_and_rejects_bad_inputs() {
    let mut sidecar = Sidecar::spawn();
    let words = sidecar.ok(
        "words",
        "keyence_parse_words",
        json!({ "response": "1 65535\r\n", "expectedCount": 2 }),
    );
    assert_eq!(words["count"].as_u64(), Some(2));
    let bits = sidecar.ok(
        "bits",
        "keyence_parse_bits",
        json!({ "response": "0 1 0\r\n", "expectedCount": 3 }),
    );
    assert_eq!(bits["values"].as_array().unwrap().len(), 3);
    let bad = sidecar.send(
        "bad",
        "keyence_build_read_words",
        json!({ "address": "DM0.1", "count": 1 }),
    );
    assert_eq!(bad["ok"].as_bool(), Some(false));
}

#[test]
fn keyence_jsonl_opens_tcp_session_and_performs_read_only_transactions() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = std::thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        assert_eq!(read_cr(&mut socket), b"CR 02\r");
        socket.write_all(b"CC\r").unwrap();
        socket.write_all(b"\n").unwrap();

        assert_eq!(read_cr(&mut socket), b"RDS DM0.U 2\r");
        socket.write_all(b"1 65").unwrap();
        socket.write_all(b"535\r\n").unwrap();

        assert_eq!(read_cr(&mut socket), b"RDS MR1001 3\r");
        socket.write_all(b"0 1").unwrap();
        socket.write_all(b" 0\r\n").unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_keyence_connection",
        json!({
            "connectionId": "keyence-test",
            "host": "127.0.0.1",
            "port": port,
            "useStation": true,
            "station": 2,
        }),
    );
    assert_eq!(opened["transport"].as_str(), Some("tcp"));
    assert_eq!(opened["handshakeValidated"].as_bool(), Some(true));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));

    let words = sidecar.ok(
        "read-words",
        "keyence_read_words",
        json!({ "connectionId": "keyence-test", "address": "DM0", "count": 2 }),
    );
    assert_eq!(words["dataAscii"].as_str(), Some("1 65535"));
    assert_eq!(words["station"].as_u64(), Some(2));
    assert_eq!(words["readOnly"].as_bool(), Some(true));

    let bits = sidecar.ok(
        "read-bits",
        "keyence_read_bits",
        json!({ "connectionId": "keyence-test", "address": "MR10.1", "count": 3 }),
    );
    assert_eq!(bits["dataAscii"].as_str(), Some("0 1 0"));
    assert_eq!(bits["readOnly"].as_bool(), Some(true));

    let closed = sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "keyence-test" }),
    );
    assert_eq!(closed["closed"].as_bool(), Some(true));
    peer.join().expect("Keyence test peer failed");
}
