//! LS Electric XGT FEnet JSONL E2E。
//!
//! 只验证 Rust sidecar 的 20-byte XGT header、显式变量和软件 TCP 只读边界；不连接真实 PLC。

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

fn checksum(frame: &[u8]) -> u8 {
    frame
        .iter()
        .take(19)
        .fold(0u8, |sum, byte| sum.wrapping_add(*byte))
}

#[test]
fn ls_xgt_jsonl_builds_typed_requests() {
    let mut sidecar = Sidecar::spawn();
    let address = sidecar.ok(
        "address",
        "ls_xgt_parse_address",
        json!({ "address": "%dw100" }),
    );
    assert_eq!(address["variableName"].as_str(), Some("%DW100"));
    assert_eq!(address["dataType"].as_u64(), Some(2));

    let read = sidecar.ok(
        "read",
        "ls_xgt_build_read",
        json!({
            "variableName": "%DW100", "dataType": 2, "invokeId": 7,
            "cpu": 160, "baseNo": 0, "slotNo": 3, "companyId": "LSIS-XGT"
        }),
    );
    assert_eq!(read["port"].as_u64(), Some(2004));
    assert_eq!(read["frame"].as_array().unwrap().len(), 36);
    assert_eq!(read["frame"][13].as_u64(), Some(0x33));

    let continuous = sidecar.ok(
        "continuous",
        "ls_xgt_build_continuous_read",
        json!({
            "variableName": "%DB0", "byteCount": 8
        }),
    );
    assert_eq!(continuous["frame"][22].as_u64(), Some(0x14));

    let write = sidecar.ok(
        "write",
        "ls_xgt_build_write",
        json!({
            "variableName": "%DW100", "dataType": 2, "value": [1, 2, 3, 4]
        }),
    );
    assert_eq!(write["frame"][20].as_u64(), Some(0x58));
}

#[test]
fn ls_xgt_jsonl_parses_response_and_rejects_untyped_address() {
    let mut sidecar = Sidecar::spawn();
    let mut response = vec![0u8; 36];
    response[..8].copy_from_slice(b"LSIS-XGT");
    response[12] = 0xA0;
    response[13] = 0x11;
    response[14..16].copy_from_slice(&7u16.to_le_bytes());
    response[16..18].copy_from_slice(&16u16.to_le_bytes());
    response[18] = 3;
    response[20] = 0x55;
    response[22] = 0x02;
    response[28..30].copy_from_slice(&1u16.to_le_bytes());
    response[30..32].copy_from_slice(&4u16.to_le_bytes());
    response[32..].copy_from_slice(&[0x11, 0x22, 0x33, 0x44]);
    response[19] = checksum(&response);
    let parsed = sidecar.ok(
        "parse",
        "ls_xgt_parse_response",
        json!({
            "frame": response, "expectedInvokeId": 7
        }),
    );
    assert_eq!(parsed["command"].as_u64(), Some(0x55));
    assert_eq!(parsed["dataHex"].as_str(), Some("11223344"));

    let bad = sidecar.send(
        "bad",
        "ls_xgt_build_read",
        json!({ "variableName": "D100", "dataType": 3 }),
    );
    assert_eq!(bad["ok"].as_bool(), Some(false));
    assert_eq!(bad["error"]["code"].as_str(), Some("LS_XGT_PARAM_INVALID"));
}

fn read_xgt_frame(socket: &mut std::net::TcpStream) -> Vec<u8> {
    let mut frame = vec![0u8; 20];
    socket.read_exact(&mut frame).unwrap();
    let application_length = u16::from_le_bytes([frame[16], frame[17]]) as usize;
    let mut application = vec![0u8; application_length];
    socket.read_exact(&mut application).unwrap();
    frame.extend_from_slice(&application);
    frame
}

fn xgt_read_response(invoke_id: u16, data: &[u8]) -> Vec<u8> {
    let application_length = 12 + data.len();
    let mut response = vec![0u8; 20 + application_length];
    response[..8].copy_from_slice(b"LSIS-XGT");
    response[12] = 0xA0;
    response[13] = 0x11;
    response[14..16].copy_from_slice(&invoke_id.to_le_bytes());
    response[16..18].copy_from_slice(&(application_length as u16).to_le_bytes());
    response[18] = 0x03;
    response[20] = 0x55;
    response[22] = 0x02;
    response[28..30].copy_from_slice(&1u16.to_le_bytes());
    response[30..32].copy_from_slice(&(data.len() as u16).to_le_bytes());
    response[32..].copy_from_slice(data);
    response[19] = checksum(&response);
    response
}

#[test]
fn ls_xgt_jsonl_opens_tcp_session_and_performs_read_only_transactions() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        let first = read_xgt_frame(&mut socket);
        assert_eq!(&first[..8], b"LSIS-XGT");
        assert_eq!(first[12], 0xA0);
        assert_eq!(first[13], 0x33);
        assert_eq!(u16::from_le_bytes([first[14], first[15]]), 1);
        assert_eq!(first[20], 0x54);
        assert_eq!(first[22], 0x02);
        socket
            .write_all(&xgt_read_response(1, &[0x11, 0x22, 0x33, 0x44]))
            .unwrap();

        let second = read_xgt_frame(&mut socket);
        assert_eq!(u16::from_le_bytes([second[14], second[15]]), 2);
        assert_eq!(second[20], 0x54);
        assert_eq!(second[22], 0x14);
        socket
            .write_all(&xgt_read_response(2, &[0xAA, 0xBB, 0xCC, 0xDD]))
            .unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_ls_xgt_connection",
        json!({
            "connectionId": "xgt-test",
            "host": "127.0.0.1",
            "port": port,
            "cpu": 160,
            "baseNo": 0,
            "slotNo": 3,
            "companyId": "LSIS-XGT",
        }),
    );
    assert_eq!(opened["transport"].as_str(), Some("tcp"));
    assert_eq!(opened["handshake"].as_bool(), Some(false));
    assert_eq!(opened["sessionInitialized"].as_bool(), Some(true));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));

    let first = sidecar.ok(
        "read",
        "ls_xgt_read",
        json!({ "connectionId": "xgt-test", "variableName": "%DW100", "dataType": 2 }),
    );
    assert_eq!(first["invokeId"].as_u64(), Some(1));
    assert_eq!(first["dataHex"].as_str(), Some("11223344"));
    assert_eq!(first["readOnly"].as_bool(), Some(true));

    let second = sidecar.ok(
        "continuous",
        "ls_xgt_read_continuous",
        json!({ "connectionId": "xgt-test", "variableName": "%DB0", "byteCount": 4 }),
    );
    assert_eq!(second["invokeId"].as_u64(), Some(2));
    assert_eq!(second["dataHex"].as_str(), Some("AABBCCDD"));
    assert_eq!(second["readOnly"].as_bool(), Some(true));

    let closed = sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "xgt-test" }),
    );
    assert_eq!(closed["closed"].as_bool(), Some(true));
    peer.join().expect("XGT test peer failed");
}
