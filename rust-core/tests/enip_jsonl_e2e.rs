//! Allen-Bradley EtherNet/IP/CIP JSONL E2E。
//!
//! 这组测试只验证 sidecar 的命令分发和 explicit codec，不启动真实 TCP
//! 设备，也不把软件黄金帧当作 CompactLogix/ControlLogix L2 证据。

use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpListener;
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
        let request = json!({
            "protocolVersion": 1,
            "requestId": id,
            "command": command,
            "payload": payload,
        });
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
fn enip_jsonl_builds_register_and_read_tag_frames() {
    let mut sidecar = Sidecar::spawn();
    let register = sidecar.ok(
        "register",
        "enip_build_register_session",
        json!({
            "senderContext": 1,
        }),
    );
    assert_eq!(register["command"].as_u64(), Some(0x65));
    assert_eq!(register["frame"][0].as_u64(), Some(0x65));
    assert_eq!(register["frame"].as_array().unwrap().len(), 28);

    let read = sidecar.ok(
        "read",
        "enip_build_read_tag",
        json!({
            "sessionHandle": 0x1122_3344u64,
            "senderContext": 7,
            "tag": "MyTag[3]",
            "elements": 2,
        }),
    );
    assert_eq!(read["command"].as_u64(), Some(0x6F));
    assert_eq!(read["cipService"].as_u64(), Some(0x4C));
    assert_eq!(read["tagPathHex"].as_str(), Some("91054D79546167002803"));

    let parsed = sidecar.ok(
        "parse",
        "enip_parse_frame",
        json!({
            "frame": read["frame"].clone(),
        }),
    );
    assert_eq!(parsed["command"].as_u64(), Some(0x6F));
    assert_eq!(parsed["sessionHandle"].as_u64(), Some(0x1122_3344));
    assert_eq!(
        parsed["senderContext"].as_array().unwrap()[0].as_u64(),
        Some(7)
    );
}

#[test]
fn enip_jsonl_parses_cip_response_and_rejects_bad_input() {
    let mut sidecar = Sidecar::spawn();
    // ENIP SendRRData + CPF(null address, unconnected data) + CIP Read Tag reply.
    let frame = vec![
        0x6F, 0x00, 0x16, 0x00, // command + 22-byte payload
        0x05, 0x00, 0x00, 0x00, // session
        0x00, 0x00, 0x00, 0x00, // encapsulation status
        0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // sender context
        0x00, 0x00, 0x00, 0x00, // options
        0x00, 0x00, 0x00, 0x00, // interface handle
        0x00, 0x00, // timeout
        0x02, 0x00, // item count
        0x00, 0x00, 0x00, 0x00, // null address
        0xB2, 0x00, 0x06, 0x00, // unconnected data, 6 bytes
        0xCC, 0x00, 0x00, 0x00, 0x34, 0x12, // reply service + DINT data
    ];
    let parsed = sidecar.ok(
        "response",
        "enip_parse_cip_response",
        json!({ "frame": frame }),
    );
    assert_eq!(parsed["service"].as_u64(), Some(0xCC));
    assert_eq!(parsed["generalStatus"].as_u64(), Some(0));
    assert_eq!(parsed["dataHex"].as_str(), Some("3412"));
    assert_eq!(parsed["cipOk"].as_bool(), Some(true));

    let bad = sidecar.send(
        "bad",
        "enip_parse_frame",
        json!({
            "frame": [0x65, 0x00, 0x05, 0x00],
        }),
    );
    assert_eq!(bad["ok"].as_bool(), Some(false));
    assert_eq!(bad["error"]["code"].as_str(), Some("ENIP_INVALID"));
}

fn read_enip_frame(socket: &mut std::net::TcpStream) -> Vec<u8> {
    let mut header = [0u8; 24];
    socket.read_exact(&mut header).unwrap();
    let length = u16::from_le_bytes([header[2], header[3]]) as usize;
    let mut frame = header.to_vec();
    let mut payload = vec![0u8; length];
    socket.read_exact(&mut payload).unwrap();
    frame.extend_from_slice(&payload);
    frame
}

fn build_enip_frame(
    command: u16,
    session_handle: u32,
    sender_context: u64,
    payload: &[u8],
) -> Vec<u8> {
    let mut frame = Vec::with_capacity(24 + payload.len());
    frame.extend_from_slice(&command.to_le_bytes());
    frame.extend_from_slice(&(payload.len() as u16).to_le_bytes());
    frame.extend_from_slice(&session_handle.to_le_bytes());
    frame.extend_from_slice(&0u32.to_le_bytes());
    frame.extend_from_slice(&sender_context.to_le_bytes());
    frame.extend_from_slice(&0u32.to_le_bytes());
    frame.extend_from_slice(payload);
    frame
}

#[test]
fn enip_jsonl_opens_registers_tcp_session_and_reads_tag() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = std::thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        let register = read_enip_frame(&mut socket);
        let register_header = nexus_rust_core::enip::parse_encapsulation(&register).unwrap();
        assert_eq!(
            register_header.command,
            nexus_rust_core::enip::ENIP_REGISTER_SESSION
        );
        assert_eq!(register_header.session_handle, 0);
        assert_eq!(u64::from_le_bytes(register_header.sender_context), 1);
        let register_response = build_enip_frame(
            nexus_rust_core::enip::ENIP_REGISTER_SESSION,
            0x1122_3344,
            1,
            &[0x01, 0x00, 0x00, 0x00],
        );
        socket.write_all(&register_response[..10]).unwrap();
        socket.write_all(&register_response[10..]).unwrap();

        let read = read_enip_frame(&mut socket);
        let read_header = nexus_rust_core::enip::parse_encapsulation(&read).unwrap();
        assert_eq!(
            read_header.command,
            nexus_rust_core::enip::ENIP_SEND_RR_DATA
        );
        assert_eq!(read_header.session_handle, 0x1122_3344);
        assert_eq!(u64::from_le_bytes(read_header.sender_context), 2);
        let response_cpf = [
            0, 0, 0, 0, // interface handle
            0, 0, // timeout
            2, 0, // item count
            0, 0, 0, 0, // null address
            0xB2, 0, 6, 0, // unconnected data
            0xCC, 0, 0, 0, 0x34, 0x12, // Read Tag success + data
        ];
        let response = build_enip_frame(
            nexus_rust_core::enip::ENIP_SEND_RR_DATA,
            0x1122_3344,
            2,
            &response_cpf,
        );
        socket.write_all(&response[..17]).unwrap();
        socket.write_all(&response[17..]).unwrap();

        let unregister = read_enip_frame(&mut socket);
        let unregister_header = nexus_rust_core::enip::parse_encapsulation(&unregister).unwrap();
        assert_eq!(
            unregister_header.command,
            nexus_rust_core::enip::ENIP_UNREGISTER_SESSION
        );
        assert_eq!(unregister_header.session_handle, 0x1122_3344);
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_enip_connection",
        json!({ "connectionId": "enip-test", "host": "127.0.0.1", "port": port }),
    );
    assert_eq!(opened["transport"].as_str(), Some("tcp"));
    assert_eq!(opened["sessionHandle"].as_u64(), Some(0x1122_3344));
    assert_eq!(opened["sessionInitialized"].as_bool(), Some(true));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));

    let read = sidecar.ok(
        "read-live",
        "enip_read_tag",
        json!({ "connectionId": "enip-test", "tag": "MyTag", "elements": 1 }),
    );
    assert_eq!(read["service"].as_u64(), Some(0xCC));
    assert_eq!(read["dataHex"].as_str(), Some("3412"));
    assert_eq!(read["generalStatus"].as_u64(), Some(0));
    assert_eq!(read["readOnly"].as_bool(), Some(true));

    let closed = sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "enip-test" }),
    );
    assert_eq!(closed["closed"].as_bool(), Some(true));
    peer.join().expect("EtherNet/IP test peer failed");
}
