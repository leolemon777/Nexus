//! Beckhoff ADS/AMS JSONL E2E。
//!
//! 只启动 Rust sidecar 验证命令分发和离线编解码；不连接 TwinCAT，也不把
//! 软件黄金帧当成 AMS Route 或 PLC L2 证据。

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

fn context() -> Value {
    json!({
        "targetNetId": "5.72.144.1.1.1",
        "targetPort": 851,
        "sourceNetId": "5.72.144.2.1.1",
        "sourcePort": 32905,
        "invokeId": 1,
    })
}

#[test]
fn ads_jsonl_builds_read_write_and_state_frames() {
    let mut sidecar = Sidecar::spawn();
    let mut read_payload = context();
    read_payload["indexGroup"] = json!(0xF000u64);
    read_payload["indexOffset"] = json!(0x20u64);
    read_payload["readLength"] = json!(4);
    let read = sidecar.ok("read", "ads_build_read", read_payload);
    assert_eq!(read["command"].as_u64(), Some(0x0002));
    assert_eq!(read["frame"].as_array().unwrap().len(), 50);
    assert_eq!(
        read["frameHex"]
            .as_str()
            .unwrap()
            .starts_with("00002C000000"),
        true
    );

    let mut write_payload = context();
    write_payload["indexGroup"] = json!(0xF005u64);
    write_payload["indexOffset"] = json!(7);
    write_payload["data"] = json!([0x11, 0x22]);
    let write = sidecar.ok("write", "ads_build_write", write_payload);
    assert_eq!(write["command"].as_u64(), Some(0x0003));
    assert_eq!(
        write["payloadHex"]
            .as_str()
            .unwrap()
            .ends_with("020000001122"),
        true
    );

    let mut state_payload = context();
    state_payload["invokeId"] = json!(2);
    let state = sidecar.ok("state", "ads_build_read_state", state_payload);
    assert_eq!(state["command"].as_u64(), Some(0x0004));
    assert_eq!(state["dataLength"].as_u64(), Some(0));
}

#[test]
fn ads_jsonl_parses_response_and_rejects_bad_net_id() {
    let mut sidecar = Sidecar::spawn();
    let frame = vec![
        0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x05, 0x48, 0x90, 0x02, 0x01, 0x01, 0x89, 0x80, 0x05,
        0x48, 0x90, 0x01, 0x01, 0x01, 0x53, 0x03, 0x02, 0x00, 0x05, 0x00, 0x0C, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00,
        0x00, 0x11, 0x22, 0x33, 0x44,
    ];
    let parsed = sidecar.ok(
        "response",
        "ads_parse_response",
        json!({
            "frame": frame,
            "expectedInvokeId": 1,
            "expectedCommand": 2,
            "expectedTargetNetId": "5.72.144.2.1.1",
            "expectedTargetPort": 32905,
            "expectedSourceNetId": "5.72.144.1.1.1",
            "expectedSourcePort": 851,
        }),
    );
    assert_eq!(parsed["command"].as_u64(), Some(2));
    assert_eq!(parsed["adsResult"].as_u64(), Some(0));
    assert_eq!(parsed["dataHex"].as_str(), Some("11223344"));
    assert_eq!(parsed["adsOk"].as_bool(), Some(true));

    let bad = sidecar.send(
        "bad",
        "ads_build_read",
        json!({
            "targetNetId": "5.72.144.1.1",
            "targetPort": 851,
            "sourceNetId": "5.72.144.2.1.1",
            "sourcePort": 32905,
            "indexGroup": 0,
            "indexOffset": 0,
            "readLength": 1,
        }),
    );
    assert_eq!(bad["ok"].as_bool(), Some(false));
    assert_eq!(bad["error"]["code"].as_str(), Some("ADS_PARAM_INVALID"));
}

fn read_ads_frame(socket: &mut std::net::TcpStream) -> Vec<u8> {
    let mut header = [0u8; 6];
    socket.read_exact(&mut header).unwrap();
    let length = u32::from_le_bytes([header[2], header[3], header[4], header[5]]) as usize;
    let mut frame = header.to_vec();
    let mut payload = vec![0u8; length];
    socket.read_exact(&mut payload).unwrap();
    frame.extend_from_slice(&payload);
    frame
}

fn build_ads_response(request: &[u8], command: u16, payload: &[u8]) -> Vec<u8> {
    let parsed = nexus_rust_core::ads::parse_frame(request).unwrap();
    let mut ams = Vec::with_capacity(32 + payload.len());
    ams.extend_from_slice(&parsed.source_net_id);
    ams.extend_from_slice(&parsed.source_port.to_le_bytes());
    ams.extend_from_slice(&parsed.target_net_id);
    ams.extend_from_slice(&parsed.target_port.to_le_bytes());
    ams.extend_from_slice(&command.to_le_bytes());
    ams.extend_from_slice(&0x0005u16.to_le_bytes());
    ams.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    ams.extend_from_slice(&0u32.to_le_bytes());
    ams.extend_from_slice(&parsed.invoke_id.to_le_bytes());
    ams.extend_from_slice(payload);
    let mut frame = vec![0, 0];
    frame.extend_from_slice(&(ams.len() as u32).to_le_bytes());
    frame.extend_from_slice(&ams);
    frame
}

#[test]
fn ads_jsonl_opens_tcp_session_and_performs_read_only_transactions() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = std::thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        let read_request = read_ads_frame(&mut socket);
        let parsed = nexus_rust_core::ads::parse_frame(&read_request).unwrap();
        assert_eq!(parsed.command, nexus_rust_core::ads::ADS_READ);
        assert_eq!(parsed.invoke_id, 1);
        let read_response = build_ads_response(
            &read_request,
            nexus_rust_core::ads::ADS_READ,
            &[0, 0, 0, 0, 4, 0, 0, 0, 0x11, 0x22, 0x33, 0x44],
        );
        socket.write_all(&read_response[..7]).unwrap();
        socket.write_all(&read_response[7..]).unwrap();

        let info_request = read_ads_frame(&mut socket);
        let info_response = build_ads_response(
            &info_request,
            nexus_rust_core::ads::ADS_READ_DEVICE_INFO,
            &[
                0, 0, 0, 0, 3, 0, 1, 0, 84, 119, 105, 110, 67, 65, 84, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            ],
        );
        socket.write_all(&info_response).unwrap();

        let state_request = read_ads_frame(&mut socket);
        let state_response = build_ads_response(
            &state_request,
            nexus_rust_core::ads::ADS_READ_STATE,
            &[0, 0, 0, 0, 5, 0, 0, 0],
        );
        socket.write_all(&state_response).unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_ads_connection",
        json!({
            "connectionId": "ads-test",
            "host": "127.0.0.1",
            "port": port,
            "targetNetId": "5.72.144.1.1.1",
            "targetPort": 851,
            "sourceNetId": "5.72.144.2.1.1",
            "sourcePort": 32905,
        }),
    );
    assert_eq!(opened["transport"].as_str(), Some("tcp"));
    assert_eq!(opened["sessionInitialized"].as_bool(), Some(true));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));

    let read = sidecar.ok(
        "read-live",
        "ads_read",
        json!({ "connectionId": "ads-test", "indexGroup": 0xF000u64, "indexOffset": 0x20, "readLength": 4 }),
    );
    assert_eq!(read["dataHex"].as_str(), Some("11223344"));
    assert_eq!(read["invokeId"].as_u64(), Some(1));
    assert_eq!(read["readOnly"].as_bool(), Some(true));

    let info = sidecar.ok(
        "info-live",
        "ads_read_device_info",
        json!({ "connectionId": "ads-test" }),
    );
    assert_eq!(info["deviceInfo"]["deviceName"].as_str(), Some("TwinCAT"));

    let state = sidecar.ok(
        "state-live",
        "ads_read_state",
        json!({ "connectionId": "ads-test" }),
    );
    assert_eq!(state["state"]["adsState"].as_u64(), Some(5));

    let closed = sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "ads-test" }),
    );
    assert_eq!(closed["closed"].as_bool(), Some(true));
    peer.join().expect("ADS test peer failed");
}
