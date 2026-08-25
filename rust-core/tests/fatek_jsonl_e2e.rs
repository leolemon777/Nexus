//! FATEK native ASCII codec/session JSONL E2E; software peer only, no PLC control.

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
fn fatek_jsonl_builds_address_and_ascii_frames() {
    let mut sidecar = Sidecar::spawn();
    let address = sidecar.ok(
        "address",
        "fatek_parse_address",
        json!({ "address": "R12" }),
    );
    assert_eq!(address["dataCode"].as_str(), Some("R"));
    assert_eq!(address["number"].as_u64(), Some(12));
    assert_eq!(
        address["underlyingProtocol"].as_str(),
        Some("FATEK native ASCII over TCP")
    );
    let read = sidecar.ok(
        "read",
        "fatek_build_read_words",
        json!({ "station": 1, "address": "R12", "count": 3 }),
    );
    assert_eq!(
        read["frameHex"].as_str(),
        Some("02 30 31 34 36 30 33 52 30 30 30 31 32 37 35 03")
    );
    let bits = sidecar.ok(
        "bits",
        "fatek_build_write_discrete",
        json!({ "station": 1, "address": "Y0", "values": [true, false, true, true] }),
    );
    assert_eq!(bits["command"].as_str(), Some("45"));
}

#[test]
fn fatek_jsonl_parses_response_and_rejects_bad_inputs() {
    let mut sidecar = Sidecar::spawn();
    let frame = sidecar.ok(
        "frame",
        "fatek_pack_command",
        json!({ "station": 1, "command": "4600000" }),
    );
    let response = sidecar.ok(
        "parse",
        "fatek_parse_response",
        json!({ "station": 1, "command": "46", "response": frame["frame"].clone() }),
    );
    assert_eq!(response["status"].as_str(), Some("0"));
    assert_eq!(response["dataAscii"].as_str(), Some("0000"));
    let bad = sidecar.send(
        "bad",
        "fatek_build_read_discrete",
        json!({ "station": 1, "address": "R0", "count": 1 }),
    );
    assert_eq!(bad["ok"].as_bool(), Some(false));
}

#[test]
fn fatek_jsonl_opens_tcp_session_and_performs_read_only_transaction() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = std::thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        let mut request = Vec::new();
        loop {
            let mut byte = [0u8; 1];
            socket.read_exact(&mut byte).unwrap();
            request.push(byte[0]);
            if byte[0] == 0x03 {
                break;
            }
        }
        assert_eq!(
            request, b"\x02014603R0001275\x03",
            "unexpected FATEK read request"
        );

        // Response data is raw ASCII hex: three words => 3 * 4 bytes.
        let response = b"\x020146010A57FC4000189\x03";
        socket.write_all(&response[..8]).unwrap();
        socket.write_all(&response[8..]).unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_fatek_connection",
        json!({
            "connectionId": "fatek-test",
            "host": "127.0.0.1",
            "port": port,
            "station": 1,
        }),
    );
    assert_eq!(opened["transport"].as_str(), Some("tcp"));
    assert_eq!(opened["sessionInitialized"].as_bool(), Some(true));
    assert_eq!(opened["handshake"].as_bool(), Some(false));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));

    let read = sidecar.ok(
        "live-read",
        "fatek_read_words",
        json!({ "connectionId": "fatek-test", "address": "R12", "count": 3 }),
    );
    assert_eq!(read["dataAscii"].as_str(), Some("10A57FC40001"));
    assert_eq!(read["expectedDataBytes"].as_u64(), Some(12));
    assert_eq!(read["readOnly"].as_bool(), Some(true));
    let closed = sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "fatek-test" }),
    );
    assert_eq!(closed["closed"].as_bool(), Some(true));
    peer.join().expect("FATEK test peer failed");
}
