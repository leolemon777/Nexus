//! BACnet/IP Who-Is/I-Am and ReadProperty JSONL boundary evidence.
//!
//! Fixed frames below are cross-checked against the public BACnet-stack
//! encoding semantics and the historical Nexus.Bacnet removal audit. These
//! are not UDP interoperability or real-device L2 acceptance records.

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
fn hello_advertises_only_nine_bacnet_ip_read_only_commands() {
    let mut sidecar = Sidecar::spawn();
    let hello = sidecar.ok("hello", "hello", json!({}));
    let capabilities = hello["capabilities"].as_array().unwrap();
    for command in [
        "bacnet_ip_build_whois",
        "bacnet_ip_build_iam",
        "bacnet_ip_parse_frame",
        "bacnet_ip_build_read_property_request",
        "bacnet_ip_parse_read_property_request",
        "bacnet_ip_parse_read_property_ack",
        "open_bacnet_ip_connection",
        "bacnet_ip_whois",
        "bacnet_ip_read_property_live",
    ] {
        assert!(
            capabilities.iter().any(|entry| entry == command),
            "{command}"
        );
    }
    for forbidden in [
        "bacnet_ip_discover",
        "bacnet_ip_read_property_multiple",
        "bacnet_ip_write_property",
        "bacnet_ip_subscribe_cov",
        "bacnet_ip_register_foreign_device",
    ] {
        assert!(
            !capabilities.iter().any(|entry| entry == forbidden),
            "{forbidden}"
        );
    }
}

#[test]
fn jsonl_builds_and_parses_exact_read_property_request() {
    let mut sidecar = Sidecar::spawn();
    let request = sidecar.ok(
        "rp-build",
        "bacnet_ip_build_read_property_request",
        json!({
            "objectType": 0,
            "objectInstance": 1001,
            "propertyIdentifier": 85,
            "invokeId": 1
        }),
    );
    assert_eq!(
        request["frame"],
        json!([
            0x81, 0x0A, 0x00, 0x11, 0x01, 0x04, 0x00, 0x03, 0x01, 0x0C, 0x0C, 0x00, 0x00, 0x03,
            0xE9, 0x19, 0x55
        ])
    );

    let parsed = sidecar.ok(
        "rp-parse",
        "bacnet_ip_parse_read_property_request",
        json!({ "frame": request["frame"].clone() }),
    );
    assert_eq!(parsed["request"]["objectType"], 0);
    assert_eq!(parsed["request"]["objectInstance"], 1001);
    assert_eq!(parsed["request"]["propertyIdentifier"], 85);
    assert_eq!(parsed["request"]["invokeId"], 1);
}

#[test]
fn jsonl_parses_read_property_ack_and_rejects_mismatch() {
    let mut sidecar = Sidecar::spawn();
    let expected_request = json!({
        "objectType": 0,
        "objectInstance": 1001,
        "propertyIdentifier": 85,
        "invokeId": 1
    });
    let response = [
        0x81, 0x0A, 0x00, 0x17, 0x01, 0x00, 0x30, 0x01, 0x0C, 0x0C, 0x00, 0x00, 0x03, 0xE9, 0x19,
        0x55, 0x3E, 0x44, 0x42, 0xF6, 0xE6, 0x66, 0x3F,
    ];
    let parsed = sidecar.ok(
        "rp-ack",
        "bacnet_ip_parse_read_property_ack",
        json!({
            "frame": response,
            "expectedRequest": expected_request
        }),
    );
    assert_eq!(parsed["ack"]["invokeId"], 1);
    assert_eq!(parsed["ack"]["objectType"], 0);
    assert_eq!(parsed["ack"]["objectInstance"], 1001);
    assert_eq!(parsed["ack"]["propertyIdentifier"], 85);
    assert_eq!(parsed["ack"]["value"]["kind"], "real");
    let real = parsed["ack"]["value"]["real"].as_f64().unwrap();
    assert!((real - 123.45).abs() < 0.0001, "{real}");

    let mut mismatch = response.to_vec();
    mismatch[7] = 0x02;
    let rejected = sidecar.send(
        "rp-mismatch",
        "bacnet_ip_parse_read_property_ack",
        json!({
            "frame": mismatch,
            "expectedRequest": expected_request
        }),
    );
    assert_eq!(rejected["error"]["code"], "BACNET_READ_PROPERTY_MISMATCH");
}

#[test]
fn jsonl_builds_exact_whois_and_iam_vectors() {
    let mut sidecar = Sidecar::spawn();
    let global = sidecar.ok(
        "whois-global",
        "bacnet_ip_build_whois",
        json!({ "broadcast": true }),
    );
    assert_eq!(
        global["frame"],
        json!([0x81, 0x0B, 0x00, 0x08, 0x01, 0x00, 0x10, 0x08])
    );
    assert_eq!(global["global"], true);
    assert_eq!(global["readOnly"], true);

    let ranged = sidecar.ok(
        "whois-range",
        "bacnet_ip_build_whois",
        json!({ "lowLimit": 1000, "highLimit": 2000, "broadcast": true }),
    );
    assert_eq!(
        ranged["frame"],
        json!([
            0x81, 0x0B, 0x00, 0x0E, 0x01, 0x00, 0x10, 0x08, 0x0A, 0x03, 0xE8, 0x1A, 0x07, 0xD0
        ])
    );

    let i_am = sidecar.ok(
        "iam",
        "bacnet_ip_build_iam",
        json!({
            "deviceInstance": 1001,
            "maxApdu": 480,
            "segmentation": "none",
            "vendorId": 42,
            "broadcast": false
        }),
    );
    assert_eq!(
        i_am["frame"],
        json!([
            0x81, 0x0A, 0x00, 0x14, 0x01, 0x00, 0x10, 0x00, 0xC4, 0x02, 0x00, 0x03, 0xE9, 0x22,
            0x01, 0xE0, 0x91, 0x03, 0x21, 0x2A
        ])
    );
}

#[test]
fn jsonl_parses_layers_and_rejects_old_nexus_boundaries() {
    let mut sidecar = Sidecar::spawn();
    let parsed = sidecar.ok(
        "parse",
        "bacnet_ip_parse_frame",
        json!({
            "frame": [
                0x81, 0x0B, 0x00, 0x14, 0x01, 0x00, 0x10, 0x00, 0xC4, 0x02, 0x00,
                0x03, 0xE9, 0x22, 0x01, 0xE0, 0x91, 0x03, 0x21, 0x2A
            ]
        }),
    );
    assert_eq!(parsed["parsed"]["bvlcFunction"], 0x0B);
    assert_eq!(parsed["parsed"]["npdu"]["protocolVersion"], 1);
    assert_eq!(parsed["parsed"]["serviceName"], "i-am");
    assert_eq!(parsed["parsed"]["iAm"]["deviceInstance"], 1001);
    assert_eq!(parsed["parsed"]["iAm"]["maxApdu"], 480);
    assert_eq!(parsed["parsed"]["iAm"]["segmentation"], "none");
    assert_eq!(parsed["parsed"]["iAm"]["vendorId"], 42);

    let old_function = sidecar.send(
        "old-function",
        "bacnet_ip_parse_frame",
        json!({
            "frame": [0x81, 0x00, 0x00, 0x08, 0x01, 0x00, 0x10, 0x08]
        }),
    );
    assert_eq!(
        old_function["error"]["code"],
        "BACNET_BVLC_FUNCTION_UNSUPPORTED"
    );

    let routed = sidecar.send(
        "routed",
        "bacnet_ip_parse_frame",
        json!({
            "frame": [0x81, 0x0B, 0x00, 0x08, 0x01, 0x20, 0x10, 0x08]
        }),
    );
    assert_eq!(routed["error"]["code"], "BACNET_NPDU_CONTROL_UNSUPPORTED");

    let partial = sidecar.send(
        "partial",
        "bacnet_ip_build_whois",
        json!({ "lowLimit": 1, "broadcast": true }),
    );
    assert_eq!(partial["error"]["code"], "BACNET_WHOIS_LIMIT_PAIR_INVALID");
}

#[test]
fn jsonl_runs_directed_whois_and_read_property_over_udp_peer() {
    let peer_socket = UdpSocket::bind("127.0.0.1:0").unwrap();
    peer_socket
        .set_read_timeout(Some(RESPONSE_TIMEOUT))
        .unwrap();
    let port = peer_socket.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let mut buffer = [0u8; 2048];

        let (size, client) = peer_socket.recv_from(&mut buffer).unwrap();
        assert_eq!(
            &buffer[..size],
            &[0x81, 0x0A, 0x00, 0x08, 0x01, 0x00, 0x10, 0x08]
        );
        peer_socket
            .send_to(
                &[
                    0x81, 0x0A, 0x00, 0x14, 0x01, 0x00, 0x10, 0x00, 0xC4, 0x02, 0x00, 0x03, 0xE9,
                    0x22, 0x01, 0xE0, 0x91, 0x03, 0x21, 0x2A,
                ],
                client,
            )
            .unwrap();

        let (size, _) = peer_socket.recv_from(&mut buffer).unwrap();
        assert_eq!(
            &buffer[..size],
            &[
                0x81, 0x0A, 0x00, 0x11, 0x01, 0x04, 0x00, 0x03, 0x01, 0x0C, 0x0C, 0x00, 0x00, 0x03,
                0xE9, 0x19, 0x55
            ]
        );
        peer_socket
            .send_to(
                &[
                    0x81, 0x0A, 0x00, 0x17, 0x01, 0x00, 0x30, 0x01, 0x0C, 0x0C, 0x00, 0x00, 0x03,
                    0xE9, 0x19, 0x55, 0x3E, 0x44, 0x42, 0xF6, 0xE6, 0x66, 0x3F,
                ],
                client,
            )
            .unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_bacnet_ip_connection",
        json!({
            "connectionId": "bacnet-loopback",
            "host": "127.0.0.1",
            "port": port
        }),
    );
    assert_eq!(opened["udpConnected"].as_bool(), Some(true));
    assert_eq!(opened["readOnly"].as_bool(), Some(true));
    assert_eq!(opened["controlsEnabled"].as_bool(), Some(false));

    let discovered = sidecar.ok(
        "whois",
        "bacnet_ip_whois",
        json!({
            "connectionId": "bacnet-loopback",
            "timeoutMs": 300
        }),
    );
    assert_eq!(discovered["responseCount"].as_u64(), Some(1));
    assert_eq!(discovered["responses"][0]["serviceName"], "i-am");
    assert_eq!(discovered["responses"][0]["iAm"]["deviceInstance"], 1001);

    let read = sidecar.ok(
        "read",
        "bacnet_ip_read_property_live",
        json!({
            "connectionId": "bacnet-loopback",
            "objectType": 0,
            "objectInstance": 1001,
            "propertyIdentifier": 85,
            "timeoutMs": 500
        }),
    );
    assert_eq!(read["request"]["invokeId"], 1);
    assert_eq!(read["ack"]["invokeId"], 1);
    assert_eq!(read["ack"]["value"]["kind"], "real");
    let real = read["ack"]["value"]["real"].as_f64().unwrap();
    assert!((real - 123.45).abs() < 0.0001, "{real}");

    sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "bacnet-loopback" }),
    );
    peer.join().unwrap();
}

#[test]
fn jsonl_rejects_read_property_echo_mismatch_and_drops_udp_session() {
    let peer_socket = UdpSocket::bind("127.0.0.1:0").unwrap();
    peer_socket
        .set_read_timeout(Some(RESPONSE_TIMEOUT))
        .unwrap();
    let port = peer_socket.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let mut buffer = [0u8; 2048];
        let (_, client) = peer_socket.recv_from(&mut buffer).unwrap();
        assert_eq!(buffer[8], 0x01);
        peer_socket
            .send_to(
                &[
                    0x81, 0x0A, 0x00, 0x17, 0x01, 0x00, 0x30, 0x02, 0x0C, 0x0C, 0x00, 0x00, 0x03,
                    0xE9, 0x19, 0x55, 0x3E, 0x44, 0x42, 0xF6, 0xE6, 0x66, 0x3F,
                ],
                client,
            )
            .unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    sidecar.ok(
        "open",
        "open_bacnet_ip_connection",
        json!({
            "connectionId": "bacnet-mismatch",
            "host": "127.0.0.1",
            "port": port
        }),
    );
    let rejected = sidecar.send(
        "bad-read",
        "bacnet_ip_read_property_live",
        json!({
            "connectionId": "bacnet-mismatch",
            "objectType": 0,
            "objectInstance": 1001,
            "propertyIdentifier": 85,
            "timeoutMs": 300
        }),
    );
    assert_eq!(rejected["error"]["code"], "BACNET_READ_PROPERTY_MISMATCH");
    let after_drop = sidecar.send(
        "after-drop",
        "bacnet_ip_read_property_live",
        json!({
            "connectionId": "bacnet-mismatch",
            "objectType": 0,
            "objectInstance": 1001,
            "propertyIdentifier": 85,
            "timeoutMs": 100
        }),
    );
    assert_eq!(after_drop["error"]["code"], "CONNECTION_NOT_FOUND");
    peer.join().unwrap();
}
