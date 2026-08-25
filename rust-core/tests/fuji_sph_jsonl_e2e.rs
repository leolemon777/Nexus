//! Fuji SPH Loader Command JSONL E2E; software TCP peer only, no CPU control.

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
fn fuji_jsonl_maps_addresses_and_builds_loader_frames() {
    let mut sidecar = Sidecar::spawn();
    let address = sidecar.ok(
        "address",
        "fuji_sph_parse_address",
        json!({ "address": "M10.258" }),
    );
    assert_eq!(address["typeCode"].as_u64(), Some(8));
    assert_eq!(address["wordAddress"].as_u64(), Some(258));
    assert_eq!(
        address["underlyingProtocol"].as_str(),
        Some("Fuji MICREX-SX SPH Loader Command")
    );
    let read = sidecar.ok(
        "read",
        "fuji_sph_build_read",
        json!({ "connectionId": 254, "address": "M10.258", "words": 2 }),
    );
    assert_eq!(read["command"].as_u64(), Some(0));
    assert_eq!(read["frame"].as_array().unwrap().len(), 26);
    let write = sidecar.ok(
        "write",
        "fuji_sph_build_write",
        json!({ "connectionId": 254, "address": "M1.0", "data": [52, 18] }),
    );
    assert_eq!(write["command"].as_u64(), Some(1));
    assert_eq!(write["frame"].as_array().unwrap().len(), 28);
}

#[test]
fn fuji_jsonl_parses_response_and_rejects_bit_write() {
    let mut sidecar = Sidecar::spawn();
    let read = sidecar.ok(
        "read",
        "fuji_sph_build_read",
        json!({ "connectionId": 254, "address": "M1.0", "words": 2 }),
    );
    let mut response = read["frame"].as_array().unwrap().clone();
    response[4] = json!(0);
    response[18] = json!(10);
    response[19] = json!(0);
    response.extend([json!(52), json!(18), json!(120), json!(86)]);
    let parsed = sidecar.ok(
        "parse",
        "fuji_sph_parse_response",
        json!({
            "connectionId": 254,
            "command": 0,
            "expectedDataBytes": 4,
            "response": response,
        }),
    );
    assert_eq!(parsed["typeCode"].as_u64(), Some(2));
    assert_eq!(parsed["dataHex"].as_str(), Some("34 12 78 56"));
    let bad = sidecar.send(
        "bad",
        "fuji_sph_build_write",
        json!({ "connectionId": 254, "address": "M1.0.2", "data": [0, 1] }),
    );
    assert_eq!(bad["ok"].as_bool(), Some(false), "{bad}");
    assert_eq!(
        bad["error"]["code"].as_str(),
        Some("FUJI_SPH_PARAM_INVALID")
    );
}

#[test]
fn fuji_jsonl_opens_tcp_session_and_performs_read_only_transaction() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = std::thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        let mut request = [0u8; 26];
        socket.read_exact(&mut request).unwrap();
        assert_eq!(&request[..6], &[0xFB, 0x80, 0x80, 0x00, 0xFF, 0x7B]);
        assert_eq!(request[6], 0xFE);
        assert_eq!(request[14], 0x00);
        assert_eq!(&request[20..26], &[0x02, 0x64, 0x00, 0x00, 0x02, 0x00]);

        let mut response = request.to_vec();
        response[4] = 0;
        response[18..20].copy_from_slice(&10u16.to_le_bytes());
        response.extend_from_slice(&[0x34, 0x12, 0x78, 0x56]);
        socket.write_all(&response[..20]).unwrap();
        socket.write_all(&response[20..]).unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_fuji_sph_connection",
        json!({
            "connectionId": "fuji-test",
            "host": "127.0.0.1",
            "port": port,
            "connectionIdByte": 254,
        }),
    );
    assert_eq!(opened["sessionInitialized"].as_bool(), Some(true));
    assert_eq!(opened["handshake"].as_bool(), Some(false));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));

    let read = sidecar.ok(
        "live-read",
        "fuji_sph_read",
        json!({ "connectionId": "fuji-test", "address": "M1.100", "words": 2 }),
    );
    assert_eq!(read["dataHex"].as_str(), Some("34 12 78 56"));
    assert_eq!(read["readOnly"].as_bool(), Some(true));
    let closed = sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "fuji-test" }),
    );
    assert_eq!(closed["closed"].as_bool(), Some(true));
    peer.join().expect("Fuji SPH test peer failed");
}
