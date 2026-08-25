//! DNP3 JSONL and TCP integration evidence.
//!
//! The peer below implements its own CRC/framing and transport helpers instead
//! of calling the Nexus codec. It proves a scripted loopback Outstation only;
//! it is not a third-party production stack or real RTU/IED L2 record.

use std::io::{BufRead, BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
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

fn peer_crc(data: &[u8]) -> u16 {
    let mut crc = 0u16;
    for value in data {
        crc ^= u16::from(*value);
        for _ in 0..8 {
            crc = if crc & 1 != 0 {
                (crc >> 1) ^ 0xA6BC
            } else {
                crc >> 1
            };
        }
    }
    !crc
}

fn peer_link(control: u8, destination: u16, source: u16, user_data: &[u8]) -> Vec<u8> {
    assert!(user_data.len() <= 250);
    let mut frame = vec![
        0x05,
        0x64,
        (5 + user_data.len()) as u8,
        control,
        destination as u8,
        (destination >> 8) as u8,
        source as u8,
        (source >> 8) as u8,
    ];
    frame.extend_from_slice(&peer_crc(&frame).to_le_bytes());
    for block in user_data.chunks(16) {
        frame.extend_from_slice(block);
        frame.extend_from_slice(&peer_crc(block).to_le_bytes());
    }
    frame
}

fn read_link(socket: &mut TcpStream) -> (u8, u16, u16, Vec<u8>) {
    let mut header = [0u8; 10];
    socket.read_exact(&mut header).unwrap();
    assert_eq!(&header[..2], &[0x05, 0x64]);
    assert!(header[2] >= 5);
    assert_eq!(
        u16::from_le_bytes([header[8], header[9]]),
        peer_crc(&header[..8])
    );
    let user_length = usize::from(header[2] - 5);
    let mut user_data = Vec::with_capacity(user_length);
    while user_data.len() < user_length {
        let block_length = (user_length - user_data.len()).min(16);
        let mut block = vec![0u8; block_length];
        socket.read_exact(&mut block).unwrap();
        let mut crc = [0u8; 2];
        socket.read_exact(&mut crc).unwrap();
        assert_eq!(u16::from_le_bytes(crc), peer_crc(&block));
        user_data.extend(block);
    }
    (
        header[3],
        u16::from_le_bytes([header[4], header[5]]),
        u16::from_le_bytes([header[6], header[7]]),
        user_data,
    )
}

fn read_application(socket: &mut TcpStream) -> Vec<u8> {
    let mut bytes = Vec::new();
    let mut expected_sequence = None;
    loop {
        let (control, destination, source, segment) = read_link(socket);
        assert_eq!(control, 0xC4);
        assert_eq!((destination, source), (1024, 1));
        assert!(segment.len() >= 2);
        let transport = segment[0];
        let first = transport & 0x80 != 0;
        let final_segment = transport & 0x40 != 0;
        let sequence = transport & 0x3F;
        if first {
            assert!(bytes.is_empty());
            expected_sequence = Some(sequence);
        }
        assert_eq!(expected_sequence, Some(sequence));
        expected_sequence = Some((sequence + 1) & 0x3F);
        bytes.extend_from_slice(&segment[1..]);
        if final_segment {
            return bytes;
        }
    }
}

fn send_fragmented(socket: &mut TcpStream, frame: &[u8]) {
    socket.write_all(&frame[..5]).unwrap();
    socket.flush().unwrap();
    thread::sleep(Duration::from_millis(8));
    socket.write_all(&frame[5..]).unwrap();
    socket.flush().unwrap();
}

fn send_application(
    socket: &mut TcpStream,
    application: &[u8],
    transport_sequence: &mut u8,
    chunk_size: usize,
) {
    let mut offset = 0usize;
    while offset < application.len() {
        let count = (application.len() - offset).min(chunk_size);
        let first = offset == 0;
        let final_segment = offset + count == application.len();
        let mut segment = vec![
            (if first { 0x80 } else { 0 })
                | (if final_segment { 0x40 } else { 0 })
                | (*transport_sequence & 0x3F),
        ];
        segment.extend_from_slice(&application[offset..offset + count]);
        let frame = peer_link(0x44, 1, 1024, &segment);
        if first {
            send_fragmented(socket, &frame);
        } else {
            socket.write_all(&frame).unwrap();
            socket.flush().unwrap();
        }
        *transport_sequence = transport_sequence.wrapping_add(1) & 0x3F;
        offset += count;
    }
}

fn append_u48(target: &mut Vec<u8>, value: u64) {
    target.extend_from_slice(&value.to_le_bytes()[..6]);
}

fn integrity_response(sequence: u8) -> Vec<u8> {
    let timestamp = 1_700_000_000_123u64;
    let mut response = vec![
        0xE0 | sequence,
        0x81,
        0x02,
        0x00, // FIR/FIN/CON, Class 1 available
        1,
        2,
        1,
        0,
        0,
        1,
        0,
        0x81,
        0x01, // BI0=true, BI1=false
        20,
        1,
        0x28,
        1,
        0,
        3,
        0,
        0x01, // Counter3
    ];
    response.extend_from_slice(&1234u32.to_le_bytes());
    response.extend_from_slice(&[30, 5, 1, 2, 0, 2, 0, 0x01]);
    response.extend_from_slice(&12.5f32.to_le_bytes());
    response.extend_from_slice(&[2, 2, 0x28, 1, 0, 4, 0, 0x81]);
    append_u48(&mut response, timestamp);
    response
}

fn binary_event_response(sequence: u8, unsolicited: bool, index: u16) -> Vec<u8> {
    let timestamp = 1_700_000_001_234u64;
    let mut response = vec![
        0xC0 | (if unsolicited { 0x30 } else { 0 }) | sequence,
        if unsolicited { 0x82 } else { 0x81 },
        0,
        0,
        2,
        2,
        0x28,
        1,
        0,
        index as u8,
        (index >> 8) as u8,
        0x81,
    ];
    append_u48(&mut response, timestamp);
    response
}

#[test]
fn dnp3_jsonl_builds_and_parses_read_only_vectors() {
    let mut sidecar = Sidecar::spawn();
    let scan = sidecar.ok(
        "class",
        "dnp3_build_class_scan",
        json!({ "sequence": 0, "class0": true, "class1": true, "class2": true, "class3": true }),
    );
    assert_eq!(
        scan["pduHex"].as_str(),
        Some("C0 01 3C 01 06 3C 02 06 3C 03 06 3C 04 06")
    );
    assert_eq!(scan["readOnly"].as_bool(), Some(true));

    let request = sidecar.ok(
        "read",
        "dnp3_build_read_request",
        json!({ "sequence": 3, "group": 30, "variation": 5, "start": 4660, "stop": 4661 }),
    );
    assert_eq!(
        request["pduHex"].as_str(),
        Some("C3 01 1E 05 01 34 12 35 12")
    );

    let link = sidecar.ok(
        "link",
        "dnp3_build_link_frame",
        json!({
            "control": 0xC4,
            "destination": 1024,
            "source": 1,
            "userData": [0xC0, 0xC0, 0x01, 60, 1, 6]
        }),
    );
    let parsed_link = sidecar.ok(
        "parse-link",
        "dnp3_parse_link_frame",
        json!({ "frame": link["frame"] }),
    );
    assert_eq!(parsed_link["link"]["destination"].as_u64(), Some(1024));
    assert_eq!(parsed_link["link"]["control"].as_u64(), Some(0xC4));

    let parsed_app = sidecar.ok(
        "parse-app",
        "dnp3_parse_application_response",
        json!({ "pdu": integrity_response(0) }),
    );
    assert_eq!(parsed_app["pointCount"].as_u64(), Some(5));
    assert_eq!(parsed_app["response"]["iin"]["labels"][0], "class-1-events");
    assert_eq!(parsed_app["response"]["points"][3]["value"]["value"], 12.5);
    assert_eq!(parsed_app["response"]["points"][4]["event"], true);
}

#[test]
fn dnp3_jsonl_rejects_bad_crc_ranges_and_control_surface() {
    let mut sidecar = Sidecar::spawn();
    let mut bad = peer_link(0xC4, 1024, 1, &[0xC0, 0xC0, 0x01]);
    *bad.last_mut().unwrap() ^= 1;
    let crc = sidecar.send("bad-crc", "dnp3_parse_link_frame", json!({ "frame": bad }));
    assert_eq!(crc["ok"].as_bool(), Some(false));
    assert_eq!(
        crc["error"]["code"].as_str(),
        Some("DNP3_LINK_DATA_CRC_MISMATCH")
    );

    let range = sidecar.send(
        "bad-range",
        "dnp3_build_read_request",
        json!({ "sequence": 0, "group": 30, "variation": 5, "start": 2, "stop": 1 }),
    );
    assert_eq!(range["ok"].as_bool(), Some(false));
    assert_eq!(range["error"]["code"].as_str(), Some("DNP3_RANGE_INVALID"));

    for (id, command) in [("select", "dnp3_select"), ("operate", "dnp3_operate")] {
        let response = sidecar.send(id, command, json!({}));
        assert_eq!(response["ok"].as_bool(), Some(false));
        assert_eq!(response["error"]["code"].as_str(), Some("UNKNOWN_COMMAND"));
    }
}

#[test]
fn dnp3_jsonl_runs_integrity_class_events_and_confirms_over_tcp() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        socket.set_read_timeout(Some(RESPONSE_TIMEOUT)).unwrap();
        let mut tx_transport_sequence = 0u8;

        let integrity = read_application(&mut socket);
        assert_eq!(
            integrity,
            [
                0xC0, 0x01, 60, 1, 0x06, 60, 2, 0x06, 60, 3, 0x06, 60, 4, 0x06
            ]
        );
        send_application(
            &mut socket,
            &integrity_response(0),
            &mut tx_transport_sequence,
            13,
        );
        assert_eq!(read_application(&mut socket), [0xC0, 0x00]);

        let class1 = read_application(&mut socket);
        assert_eq!(class1, [0xC1, 0x01, 60, 2, 0x06]);
        send_application(
            &mut socket,
            &binary_event_response(7, true, 9),
            &mut tx_transport_sequence,
            249,
        );
        assert_eq!(read_application(&mut socket), [0xD7, 0x00]);
        send_application(
            &mut socket,
            &binary_event_response(1, false, 10),
            &mut tx_transport_sequence,
            249,
        );
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_dnp3_connection",
        json!({
            "connectionId": "dnp3-loopback",
            "host": "127.0.0.1",
            "port": port,
            "masterAddress": 1,
            "outstationAddress": 1024
        }),
    );
    assert_eq!(opened["tcpConnected"].as_bool(), Some(true));
    assert_eq!(opened["controlsEnabled"].as_bool(), Some(false));
    assert_eq!(opened["timeSyncEnabled"].as_bool(), Some(false));

    let integrity = sidecar.ok(
        "integrity",
        "dnp3_integrity_poll",
        json!({ "connectionId": "dnp3-loopback" }),
    );
    assert_eq!(integrity["classes"], json!([0, 1, 2, 3]));
    assert_eq!(integrity["pointCount"].as_u64(), Some(5));
    assert_eq!(integrity["confirmationFrames"].as_array().unwrap().len(), 1);
    assert_eq!(integrity["iin"]["labels"][0], "class-1-events");

    let events = sidecar.ok(
        "events",
        "dnp3_class_scan",
        json!({ "connectionId": "dnp3-loopback", "class1": true }),
    );
    assert_eq!(events["classes"], json!([1]));
    assert_eq!(events["pointCount"].as_u64(), Some(1));
    assert_eq!(events["points"][0]["index"].as_u64(), Some(10));
    assert_eq!(events["unsolicitedResponseCount"].as_u64(), Some(1));
    assert_eq!(
        events["unsolicitedResponses"][0]["points"][0]["index"].as_u64(),
        Some(9)
    );

    sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "dnp3-loopback" }),
    );
    peer.join().unwrap();
}

#[test]
fn dnp3_jsonl_rejects_wrong_transport_sequence_and_drops_session() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        socket.set_read_timeout(Some(RESPONSE_TIMEOUT)).unwrap();
        let request = read_application(&mut socket);
        assert_eq!(request[1], 0x01);
        let response = integrity_response(request[0] & 0x0F);
        let first = {
            let mut value = vec![0x80];
            value.extend_from_slice(&response[..12]);
            peer_link(0x44, 1, 1024, &value)
        };
        let second = {
            let mut value = vec![0x42]; // expected sequence 1, deliberately send 2
            value.extend_from_slice(&response[12..]);
            peer_link(0x44, 1, 1024, &value)
        };
        socket.write_all(&first).unwrap();
        socket.write_all(&second).unwrap();
        socket.flush().unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    sidecar.ok(
        "open-bad",
        "open_dnp3_connection",
        json!({
            "connectionId": "dnp3-bad-sequence",
            "host": "127.0.0.1",
            "port": port,
            "masterAddress": 1,
            "outstationAddress": 1024
        }),
    );
    let failed = sidecar.send(
        "poll-bad",
        "dnp3_integrity_poll",
        json!({ "connectionId": "dnp3-bad-sequence" }),
    );
    assert_eq!(failed["ok"].as_bool(), Some(false));
    assert_eq!(
        failed["error"]["code"].as_str(),
        Some("DNP3_TRANSPORT_SEQUENCE_MISMATCH")
    );
    let gone = sidecar.send(
        "poll-gone",
        "dnp3_integrity_poll",
        json!({ "connectionId": "dnp3-bad-sequence" }),
    );
    assert_eq!(gone["ok"].as_bool(), Some(false));
    assert_eq!(gone["error"]["code"].as_str(), Some("CONNECTION_NOT_FOUND"));
    peer.join().unwrap();
}
