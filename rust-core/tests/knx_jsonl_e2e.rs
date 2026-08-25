//! KNXnet/IP Tunneling v1 JSONL offline read-only codec evidence.
//!
//! Fixed vectors below come from the audited C# Tunneling v1 subset and its
//! independent UDP peer tests. This file does not claim real gateway L2.

use std::io::{BufRead, BufReader, Write};
use std::net::UdpSocket;
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
fn hello_advertises_only_fifteen_knx_read_only_commands() {
    let mut sidecar = Sidecar::spawn();
    let hello = sidecar.ok("hello", "hello", json!({}));
    let capabilities = hello["capabilities"].as_array().unwrap();
    for command in [
        "knx_parse_group_address",
        "knx_build_connect_request",
        "knx_parse_connect_response",
        "knx_build_group_read_request",
        "knx_parse_tunneling_request",
        "knx_parse_group_value_response",
        "knx_parse_tunneling_ack",
        "knx_build_tunneling_ack",
        "open_knx_connection",
        "knx_group_read",
        "knx_disconnect",
        "knx_connection_state",
        "knx_start_keepalive",
        "knx_stop_keepalive",
        "knx_keepalive_status",
    ] {
        assert!(
            capabilities.iter().any(|entry| entry == command),
            "{command}"
        );
    }
    for forbidden in [
        "knx_group_write",
        "knx_write_group_value",
        "knx_group_write_live",
        "knx_scene_control",
        "knx_auto_reconnect",
    ] {
        assert!(
            !capabilities.iter().any(|entry| entry == forbidden),
            "{forbidden}"
        );
    }
}

#[test]
fn jsonl_automatic_keepalive_failure_requires_explicit_reconnect() {
    let gateway = UdpSocket::bind("127.0.0.1:0").unwrap();
    gateway.set_read_timeout(Some(RESPONSE_TIMEOUT)).unwrap();
    let port = gateway.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let mut buffer = [0u8; 2048];

        let (size, client) = gateway.recv_from(&mut buffer).unwrap();
        assert_eq!(size, 26);
        assert_eq!(&buffer[..6], &[0x06, 0x10, 0x02, 0x05, 0x00, 0x1A]);
        gateway
            .send_to(
                &[
                    0x06,
                    0x10,
                    0x02,
                    0x06,
                    0x00,
                    0x14,
                    0x15,
                    0x00,
                    0x08,
                    0x01,
                    0x7F,
                    0x00,
                    0x00,
                    0x01,
                    (port >> 8) as u8,
                    port as u8,
                    0x04,
                    0x04,
                    0x11,
                    0x01,
                ],
                client,
            )
            .unwrap();

        // Consume automatic Connection State retries and deliberately drop them.
        let mut client;
        loop {
            let (size, peer) = gateway.recv_from(&mut buffer).unwrap();
            client = peer;
            if size == 26 {
                assert_eq!(&buffer[..6], &[0x06, 0x10, 0x02, 0x05, 0x00, 0x1A]);
                break;
            }
            assert_eq!(
                &buffer[..8],
                &[0x06, 0x10, 0x02, 0x07, 0x00, 0x10, 0x15, 0x00]
            );
            assert_eq!(size, 16);
        }
        gateway
            .send_to(
                &[
                    0x06,
                    0x10,
                    0x02,
                    0x06,
                    0x00,
                    0x14,
                    0x16,
                    0x00,
                    0x08,
                    0x01,
                    0x7F,
                    0x00,
                    0x00,
                    0x01,
                    (port >> 8) as u8,
                    port as u8,
                    0x04,
                    0x04,
                    0x12,
                    0x01,
                ],
                client,
            )
            .unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    sidecar.ok(
        "open",
        "open_knx_connection",
        json!({
            "connectionId": "knx-auto",
            "host": "127.0.0.1",
            "port": port,
            "timeoutMs": 300
        }),
    );
    sidecar.ok(
        "start",
        "knx_start_keepalive",
        json!({
            "connectionId": "knx-auto",
            "intervalMs": 120
        }),
    );

    // The serve loop runs Connection State every 50 ms and retries three times.
    thread::sleep(Duration::from_millis(250));
    let status = sidecar.ok(
        "status-after-failure",
        "knx_keepalive_status",
        json!({ "connectionId": "knx-auto" }),
    );
    assert_eq!(status["keepaliveEnabled"].as_bool(), Some(false));

    let read = sidecar.send(
        "read-after-failure",
        "knx_group_read",
        json!({
            "connectionId": "knx-auto",
            "address": "1/2/3",
            "timeoutMs": 100
        }),
    );
    assert_eq!(read["error"]["code"], "CONNECTION_NOT_FOUND");

    let reopened = sidecar.ok(
        "reconnect",
        "open_knx_connection",
        json!({
            "connectionId": "knx-auto",
            "host": "127.0.0.1",
            "port": port,
            "timeoutMs": 400
        }),
    );
    assert_eq!(reopened["response"]["channelId"], 0x16);
    peer.join().unwrap();
}

#[test]
fn jsonl_drops_knx_session_on_state_error_and_reconnects_explicitly() {
    let gateway = UdpSocket::bind("127.0.0.1:0").unwrap();
    gateway.set_read_timeout(Some(RESPONSE_TIMEOUT)).unwrap();
    let port = gateway.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let mut buffer = [0u8; 2048];

        let (size, client) = gateway.recv_from(&mut buffer).unwrap();
        assert_eq!(size, 26);
        assert_eq!(&buffer[..6], &[0x06, 0x10, 0x02, 0x05, 0x00, 0x1A]);
        gateway
            .send_to(
                &[
                    0x06,
                    0x10,
                    0x02,
                    0x06,
                    0x00,
                    0x14,
                    0x15,
                    0x00,
                    0x08,
                    0x01,
                    0x7F,
                    0x00,
                    0x00,
                    0x01,
                    (port >> 8) as u8,
                    port as u8,
                    0x04,
                    0x04,
                    0x11,
                    0x01,
                ],
                client,
            )
            .unwrap();

        let (size, _) = gateway.recv_from(&mut buffer).unwrap();
        assert_eq!(
            &buffer[..8],
            &[0x06, 0x10, 0x02, 0x07, 0x00, 0x10, 0x15, 0x00]
        );
        assert_eq!(size, 16);
        gateway
            .send_to(&[0x06, 0x10, 0x02, 0x08, 0x00, 0x08, 0x15, 0x29], client)
            .unwrap();

        let (size, client) = gateway.recv_from(&mut buffer).unwrap();
        assert_eq!(size, 26);
        assert_eq!(&buffer[..6], &[0x06, 0x10, 0x02, 0x05, 0x00, 0x1A]);
        gateway
            .send_to(
                &[
                    0x06,
                    0x10,
                    0x02,
                    0x06,
                    0x00,
                    0x14,
                    0x16,
                    0x00,
                    0x08,
                    0x01,
                    0x7F,
                    0x00,
                    0x00,
                    0x01,
                    (port >> 8) as u8,
                    port as u8,
                    0x04,
                    0x04,
                    0x12,
                    0x01,
                ],
                client,
            )
            .unwrap();

        let (size, _) = gateway.recv_from(&mut buffer).unwrap();
        assert_eq!(
            &buffer[..8],
            &[0x06, 0x10, 0x02, 0x07, 0x00, 0x10, 0x16, 0x00]
        );
        assert_eq!(size, 16);
        gateway
            .send_to(&[0x06, 0x10, 0x02, 0x08, 0x00, 0x08, 0x16, 0x00], client)
            .unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let open_payload = json!({
        "connectionId": "knx-state-loopback",
        "host": "127.0.0.1",
        "port": port,
        "timeoutMs": 400
    });
    sidecar.ok("open-first", "open_knx_connection", open_payload.clone());
    let failed = sidecar.send(
        "state-error",
        "knx_connection_state",
        json!({
            "connectionId": "knx-state-loopback",
            "timeoutMs": 200
        }),
    );
    assert_eq!(failed["error"]["code"], "KNX_CONNECTION_STATE_STATUS");

    let reopened = sidecar.ok("open-reconnect", "open_knx_connection", open_payload);
    assert_eq!(reopened["response"]["channelId"], 0x16);
    let state = sidecar.ok(
        "state-ok",
        "knx_connection_state",
        json!({
            "connectionId": "knx-state-loopback",
            "timeoutMs": 300
        }),
    );
    assert_eq!(state["response"]["channelId"], 0x16);
    assert_eq!(state["response"]["status"], 0);
    assert_eq!(state["attempts"], 1);
    peer.join().unwrap();
}

#[test]
fn jsonl_runs_connect_group_read_and_disconnect_over_udp_gateway() {
    let gateway = UdpSocket::bind("127.0.0.1:0").unwrap();
    gateway.set_read_timeout(Some(RESPONSE_TIMEOUT)).unwrap();
    let port = gateway.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let mut buffer = [0u8; 2048];

        let (size, client) = gateway.recv_from(&mut buffer).unwrap();
        assert_eq!(size, 26);
        assert_eq!(&buffer[..6], &[0x06, 0x10, 0x02, 0x05, 0x00, 0x1A]);
        assert_eq!(&buffer[22..26], &[0x04, 0x04, 0x02, 0x00]);
        gateway
            .send_to(
                &[
                    0x06,
                    0x10,
                    0x02,
                    0x06,
                    0x00,
                    0x14,
                    0x15,
                    0x00,
                    0x08,
                    0x01,
                    0x7F,
                    0x00,
                    0x00,
                    0x01,
                    (port >> 8) as u8,
                    port as u8,
                    0x04,
                    0x04,
                    0x11,
                    0x01,
                ],
                client,
            )
            .unwrap();

        let (size, _) = gateway.recv_from(&mut buffer).unwrap();
        assert_eq!(
            &buffer[..size],
            &[
                0x06, 0x10, 0x04, 0x20, 0x00, 0x15, 0x04, 0x15, 0x00, 0x00, 0x11, 0x00, 0xBC, 0xE0,
                0x00, 0x00, 0x0A, 0x03, 0x01, 0x00, 0x00,
            ]
        );
        gateway
            .send_to(
                &[0x06, 0x10, 0x04, 0x21, 0x00, 0x0A, 0x04, 0x15, 0x00, 0x00],
                client,
            )
            .unwrap();
        gateway
            .send_to(
                &[
                    0x06, 0x10, 0x04, 0x20, 0x00, 0x17, 0x04, 0x15, 0x00, 0x00, 0x29, 0x00, 0xBC,
                    0xE0, 0x11, 0x01, 0x0A, 0x03, 0x03, 0x00, 0x40, 0x12, 0x34,
                ],
                client,
            )
            .unwrap();

        let (size, _) = gateway.recv_from(&mut buffer).unwrap();
        assert_eq!(
            &buffer[..size],
            &[0x06, 0x10, 0x04, 0x21, 0x00, 0x0A, 0x04, 0x15, 0x00, 0x00]
        );

        let (size, _) = gateway.recv_from(&mut buffer).unwrap();
        assert_eq!(
            &buffer[..8],
            &[0x06, 0x10, 0x02, 0x09, 0x00, 0x10, 0x15, 0x00]
        );
        assert_eq!(size, 16);
        gateway
            .send_to(&[0x06, 0x10, 0x02, 0x0A, 0x00, 0x08, 0x15, 0x00], client)
            .unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_knx_connection",
        json!({
            "connectionId": "knx-loopback",
            "host": "127.0.0.1",
            "port": port,
            "timeoutMs": 500
        }),
    );
    assert_eq!(opened["udpConnected"].as_bool(), Some(true));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));
    assert_eq!(opened["response"]["channelId"], 0x15);
    assert_eq!(opened["response"]["individualAddress"], 0x1101);

    let read = sidecar.ok(
        "read",
        "knx_group_read",
        json!({
            "connectionId": "knx-loopback",
            "address": "1/2/3",
            "timeoutMs": 500
        }),
    );
    assert_eq!(read["sequence"], 0);
    assert_eq!(read["attempts"], 1);
    assert_eq!(read["address"]["value"], 0x0A03);
    assert_eq!(read["source"], 0x1101);
    assert_eq!(read["payload"], json!([0x12, 0x34]));

    let disconnected = sidecar.ok(
        "disconnect",
        "knx_disconnect",
        json!({ "connectionId": "knx-loopback", "timeoutMs": 500 }),
    );
    assert_eq!(disconnected["disconnected"].as_bool(), Some(true));
    peer.join().unwrap();
}

#[test]
fn jsonl_rejects_response_sequence_mismatch_and_drops_knx_session() {
    let gateway = UdpSocket::bind("127.0.0.1:0").unwrap();
    gateway.set_read_timeout(Some(RESPONSE_TIMEOUT)).unwrap();
    let port = gateway.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let mut buffer = [0u8; 2048];
        let (_, client) = gateway.recv_from(&mut buffer).unwrap();
        gateway
            .send_to(
                &[
                    0x06,
                    0x10,
                    0x02,
                    0x06,
                    0x00,
                    0x14,
                    0x15,
                    0x00,
                    0x08,
                    0x01,
                    0x7F,
                    0x00,
                    0x00,
                    0x01,
                    (port >> 8) as u8,
                    port as u8,
                    0x04,
                    0x04,
                    0x11,
                    0x01,
                ],
                client,
            )
            .unwrap();
        let (_, _) = gateway.recv_from(&mut buffer).unwrap();
        gateway
            .send_to(
                &[0x06, 0x10, 0x04, 0x21, 0x00, 0x0A, 0x04, 0x15, 0x00, 0x00],
                client,
            )
            .unwrap();
        // Deliberately use incoming sequence 1 when the client expects 0.
        gateway
            .send_to(
                &[
                    0x06, 0x10, 0x04, 0x20, 0x00, 0x17, 0x04, 0x15, 0x01, 0x00, 0x29, 0x00, 0xBC,
                    0xE0, 0x11, 0x01, 0x0A, 0x03, 0x03, 0x00, 0x40, 0x12, 0x34,
                ],
                client,
            )
            .unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    sidecar.ok(
        "open-bad",
        "open_knx_connection",
        json!({
            "connectionId": "knx-mismatch",
            "host": "127.0.0.1",
            "port": port,
            "timeoutMs": 500
        }),
    );
    let rejected = sidecar.send(
        "bad-read",
        "knx_group_read",
        json!({
            "connectionId": "knx-mismatch",
            "address": "1/2/3",
            "timeoutMs": 300
        }),
    );
    assert_eq!(rejected["error"]["code"], "KNX_GROUP_RESPONSE_MISMATCH");
    let after_drop = sidecar.send(
        "after-drop",
        "knx_group_read",
        json!({
            "connectionId": "knx-mismatch",
            "address": "1/2/3",
            "timeoutMs": 100
        }),
    );
    assert_eq!(after_drop["error"]["code"], "CONNECTION_NOT_FOUND");
    peer.join().unwrap();
}

#[test]
fn jsonl_builds_exact_connect_group_read_and_ack_vectors() {
    let mut sidecar = Sidecar::spawn();
    let connect = sidecar.ok(
        "connect",
        "knx_build_connect_request",
        json!({ "localIp": "127.0.0.1", "localPort": 50000 }),
    );
    assert_eq!(
        connect["frame"],
        json!([
            0x06, 0x10, 0x02, 0x05, 0x00, 0x1A, 0x08, 0x01, 0x7F, 0x00, 0x00, 0x01, 0xC3, 0x50,
            0x08, 0x01, 0x7F, 0x00, 0x00, 0x01, 0xC3, 0x50, 0x04, 0x04, 0x02, 0x00
        ])
    );

    let read = sidecar.ok(
        "read",
        "knx_build_group_read_request",
        json!({ "channelId": 0x15, "sequence": 0, "address": "1/2/3" }),
    );
    assert_eq!(
        read["frame"],
        json!([
            0x06, 0x10, 0x04, 0x20, 0x00, 0x15, 0x04, 0x15, 0x00, 0x00, 0x11, 0x00, 0xBC, 0xE0,
            0x00, 0x00, 0x0A, 0x03, 0x01, 0x00, 0x00
        ])
    );

    let ack = sidecar.ok(
        "ack",
        "knx_build_tunneling_ack",
        json!({ "channelId": 0x15, "sequence": 0, "status": 0 }),
    );
    assert_eq!(
        ack["frame"],
        json!([0x06, 0x10, 0x04, 0x21, 0x00, 0x0A, 0x04, 0x15, 0x00, 0x00])
    );
}

#[test]
fn jsonl_parses_connect_response_group_response_and_rejects_old_header() {
    let mut sidecar = Sidecar::spawn();
    let connect_response = sidecar.ok(
        "connect-resp",
        "knx_parse_connect_response",
        json!({
            "frame": [
                0x06, 0x10, 0x02, 0x06, 0x00, 0x14, 0x15, 0x00,
                0x08, 0x01, 0x7F, 0x00, 0x00, 0x01, 0xC3, 0x50,
                0x04, 0x04, 0x11, 0x01
            ]
        }),
    );
    assert_eq!(connect_response["response"]["channelId"], 0x15);
    assert_eq!(connect_response["response"]["individualAddress"], 0x1101);

    let mut group_response = vec![
        0x06, 0x10, 0x04, 0x20, 0x00, 0x17, 0x04, 0x15, 0x00, 0x00, 0x29, 0x00, 0xBC, 0xE0, 0x11,
        0x01, 0x0A, 0x03, 0x03, 0x00, 0x40,
    ];
    group_response.extend_from_slice(&[0x12, 0x34]);
    let parsed = sidecar.ok(
        "group-resp",
        "knx_parse_group_value_response",
        json!({ "frame": group_response }),
    );
    assert_eq!(parsed["response"]["messageCodeName"], "L_Data.ind");
    assert_eq!(parsed["response"]["service"], "GroupValueResponse");
    assert_eq!(parsed["response"]["payload"], json!([0x12, 0x34]));
    assert_eq!(
        parsed["suggestedAck"],
        json!([0x06, 0x10, 0x04, 0x21, 0x00, 0x0A, 0x04, 0x15, 0x00, 0x00])
    );

    let old_frame = [
        0x10, 0x00, 0x02, 0x06, 0x00, 0x14, 0x15, 0x00, 0x08, 0x01, 0x7F, 0x00, 0x00, 0x01, 0xC3,
        0x50, 0x04, 0x04, 0x11, 0x01,
    ];
    let rejected = sidecar.send(
        "old-header",
        "knx_parse_connect_response",
        json!({ "frame": old_frame }),
    );
    assert_eq!(rejected["error"]["code"], "KNX_HEADER_INVALID");

    let bad_address = sidecar.send(
        "bad-address",
        "knx_parse_group_address",
        json!({ "address": "32/0/0" }),
    );
    assert_eq!(
        bad_address["error"]["code"],
        "KNX_GROUP_ADDRESS_RANGE_INVALID"
    );
}
