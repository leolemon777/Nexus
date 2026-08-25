//! MQTT 3.1.1 JSONL E2E。
//!
//! The TCP peer is an in-process broker double.  It proves MQTT framing,
//! CONNECT/CONNACK, SUBSCRIBE/SUBACK, QoS-0 PUBLISH, PING and DISCONNECT; it
//! is not a real broker, TLS/authentication or Sparkplug L2 evidence.

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

fn read_mqtt_frame(socket: &mut std::net::TcpStream) -> Vec<u8> {
    let mut frame = vec![0u8; 1];
    socket.read_exact(&mut frame).unwrap();
    let mut multiplier = 1usize;
    let mut remaining = 0usize;
    loop {
        let mut byte = [0u8; 1];
        socket.read_exact(&mut byte).unwrap();
        frame.push(byte[0]);
        remaining += usize::from(byte[0] & 0x7F) * multiplier;
        if byte[0] & 0x80 == 0 {
            break;
        }
        multiplier *= 128;
    }
    let mut payload = vec![0u8; remaining];
    socket.read_exact(&mut payload).unwrap();
    frame.extend_from_slice(&payload);
    frame
}

fn packet(packet_type: u8, flags: u8, payload: &[u8]) -> Vec<u8> {
    let mut frame = vec![(packet_type << 4) | flags];
    let mut remaining = payload.len();
    loop {
        let mut byte = (remaining % 128) as u8;
        remaining /= 128;
        if remaining > 0 {
            byte |= 0x80;
        }
        frame.push(byte);
        if remaining == 0 {
            break;
        }
    }
    frame.extend_from_slice(payload);
    frame
}

#[test]
fn mqtt_jsonl_builds_and_parses_first_round_vectors() {
    let mut sidecar = Sidecar::spawn();
    let connect = sidecar.ok(
        "connect",
        "mqtt_build_connect",
        json!({ "clientId": "nexus-test", "keepAlive": 30, "cleanSession": true }),
    );
    assert_eq!(connect["packetType"].as_u64(), Some(1));
    assert_eq!(connect["protocolLevel"].as_u64(), Some(4));
    assert_eq!(
        connect["frameHex"].as_str(),
        Some("10 16 00 04 4D 51 54 54 04 02 00 1E 00 0A 6E 65 78 75 73 2D 74 65 73 74")
    );

    let subscribe = sidecar.ok(
        "subscribe",
        "mqtt_build_subscribe",
        json!({ "packetId": 7, "topicFilter": "factory/line1/#", "qos": 0 }),
    );
    assert_eq!(subscribe["packetType"].as_u64(), Some(8));
    assert_eq!(
        subscribe["frameHex"].as_str(),
        Some("82 14 00 07 00 0F 66 61 63 74 6F 72 79 2F 6C 69 6E 65 31 2F 23 00")
    );

    let publish = sidecar.ok(
        "publish",
        "mqtt_parse_publish",
        json!({ "frame": [0x30, 0x0D, 0, 5, 115, 116, 97, 116, 101, 0x4F, 0x4B, 0x21, 0, 1, 2] }),
    );
    assert_eq!(publish["topic"].as_str(), Some("state"));
    assert_eq!(publish["qos"].as_u64(), Some(0));
    assert_eq!(publish["payloadHex"].as_str(), Some("4F 4B 21 00 01 02"));
}

#[test]
fn mqtt_jsonl_rejects_bad_frames_and_subscribe_ids() {
    let mut sidecar = Sidecar::spawn();
    let bad_publish = sidecar.send(
        "bad-publish",
        "mqtt_parse_publish",
        json!({ "frame": [0x30, 0x02, 0, 5] }),
    );
    assert_eq!(bad_publish["ok"].as_bool(), Some(false));
    assert_eq!(bad_publish["error"]["code"].as_str(), Some("MQTT_INVALID"));

    let bad_subscribe = sidecar.send(
        "bad-subscribe",
        "mqtt_build_subscribe",
        json!({ "packetId": 0, "topicFilter": "state", "qos": 0 }),
    );
    assert_eq!(bad_subscribe["ok"].as_bool(), Some(false));
    assert_eq!(
        bad_subscribe["error"]["code"].as_str(),
        Some("MQTT_PARAM_INVALID")
    );
}

#[test]
fn mqtt_jsonl_opens_subscribes_reads_publishes_and_pings_tcp() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        let connect = read_mqtt_frame(&mut socket);
        assert_eq!(connect[0], 0x10);
        assert!(connect.windows(4).any(|window| window == b"MQTT"));
        let connack = packet(2, 0, &[0, 0]);
        socket.write_all(&connack[..2]).unwrap();
        socket.write_all(&connack[2..]).unwrap();

        let subscribe = read_mqtt_frame(&mut socket);
        assert_eq!(subscribe[0], 0x82);
        assert_eq!(&subscribe[2..4], &[0, 1]);
        let suback = packet(9, 0, &[0, 1, 0]);
        socket.write_all(&suback[..2]).unwrap();
        socket.write_all(&suback[2..]).unwrap();

        let publish = packet(
            3,
            0,
            &[0, 5, b's', b't', b'a', b't', b'e', 0x4F, 0x4B, 0x21],
        );
        socket.write_all(&publish[..3]).unwrap();
        socket.write_all(&publish[3..]).unwrap();

        let ping = read_mqtt_frame(&mut socket);
        assert_eq!(ping, vec![0xC0, 0]);
        socket.write_all(&[0xD0, 0]).unwrap();

        let disconnect = read_mqtt_frame(&mut socket);
        assert_eq!(disconnect, vec![0xE0, 0]);
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_mqtt_connection",
        json!({ "connectionId": "mqtt-test", "host": "127.0.0.1", "port": port, "clientId": "nexus-test", "keepAlive": 30, "cleanSession": true }),
    );
    assert_eq!(opened["transport"].as_str(), Some("tcp"));
    assert_eq!(opened["returnCode"].as_u64(), Some(0));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));

    let subscribed = sidecar.ok(
        "subscribe-live",
        "mqtt_subscribe",
        json!({ "connectionId": "mqtt-test", "topicFilter": "state", "qos": 0 }),
    );
    assert_eq!(subscribed["packetId"].as_u64(), Some(1));
    assert_eq!(subscribed["returnCode"].as_u64(), Some(0));

    let publish = sidecar.ok(
        "publish-live",
        "mqtt_read_publish",
        json!({ "connectionId": "mqtt-test" }),
    );
    assert_eq!(publish["topic"].as_str(), Some("state"));
    assert_eq!(publish["payloadHex"].as_str(), Some("4F 4B 21"));

    let ping = sidecar.ok("ping", "mqtt_ping", json!({ "connectionId": "mqtt-test" }));
    assert_eq!(ping["ping"].as_bool(), Some(true));

    let closed = sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "mqtt-test" }),
    );
    assert_eq!(closed["closed"].as_bool(), Some(true));
    peer.join().expect("MQTT TCP test peer failed");
}
