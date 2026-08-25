//! IEC 60870-5-104 JSONL and TCP integration evidence.
//!
//! The TCP peer is an independent scripted outstation double. It validates
//! wire bytes, fragmented reads, STARTDT/TESTFR/STOPDT, sequence tracking,
//! ACT_CON -> monitoring ASDUs -> ACT_TERM ordering and S acknowledgements.
//! It is not evidence from a production RTU/IED or IEC conformance laboratory.

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

fn read_frame(socket: &mut TcpStream) -> Vec<u8> {
    let mut header = [0u8; 2];
    socket.read_exact(&mut header).unwrap();
    assert_eq!(header[0], 0x68);
    let mut frame = Vec::with_capacity(header[1] as usize + 2);
    frame.extend_from_slice(&header);
    let mut body = vec![0u8; header[1] as usize];
    socket.read_exact(&mut body).unwrap();
    frame.extend_from_slice(&body);
    frame
}

fn i_frame(send_sequence: u16, receive_sequence: u16, asdu: &[u8]) -> Vec<u8> {
    let mut frame = vec![0x68, (4 + asdu.len()) as u8];
    frame.extend_from_slice(&(send_sequence << 1).to_le_bytes());
    frame.extend_from_slice(&(receive_sequence << 1).to_le_bytes());
    frame.extend_from_slice(asdu);
    frame
}

fn expect_s_ack(socket: &mut TcpStream, receive_sequence: u16) {
    let mut expected = vec![0x68, 0x04, 0x01, 0x00];
    expected.extend_from_slice(&(receive_sequence << 1).to_le_bytes());
    assert_eq!(read_frame(socket), expected);
}

fn send_fragmented(socket: &mut TcpStream, frame: &[u8]) {
    socket.write_all(&frame[..2]).unwrap();
    socket.flush().unwrap();
    thread::sleep(Duration::from_millis(10));
    socket.write_all(&frame[2..]).unwrap();
    socket.flush().unwrap();
}

#[test]
fn iec104_jsonl_builds_and_parses_first_read_only_vectors() {
    let mut sidecar = Sidecar::spawn();
    let interrogation = sidecar.ok(
        "gi",
        "iec104_build_general_interrogation",
        json!({
            "commonAddress": 1,
            "group": 0,
            "originatorAddress": 0,
            "sendSequence": 0,
            "receiveSequence": 0
        }),
    );
    assert_eq!(
        interrogation["frameHex"].as_str(),
        Some("68 0E 00 00 00 00 64 01 06 00 01 00 00 00 00 14")
    );
    assert_eq!(interrogation["qualifier"].as_u64(), Some(20));
    assert_eq!(interrogation["readOnly"].as_bool(), Some(true));

    let s_frame = sidecar.ok("s", "iec104_build_s_frame", json!({ "receiveSequence": 3 }));
    assert_eq!(s_frame["frameHex"].as_str(), Some("68 04 01 00 06 00"));

    let u_frame = sidecar.ok(
        "u",
        "iec104_build_u_frame",
        json!({ "function": "TESTFR_ACT" }),
    );
    assert_eq!(u_frame["frameHex"].as_str(), Some("68 04 43 00 00 00"));

    let parsed = sidecar.ok(
        "parse",
        "iec104_parse_asdu",
        json!({ "asdu": [1, 1, 20, 0, 1, 0, 42, 0, 0, 129] }),
    );
    assert_eq!(parsed["asdu"]["typeName"].as_str(), Some("M_SP_NA_1"));
    assert_eq!(
        parsed["points"][0]["informationObjectAddress"].as_u64(),
        Some(42)
    );
    assert_eq!(parsed["points"][0]["value"]["value"].as_bool(), Some(true));
    assert_eq!(
        parsed["points"][0]["quality"]["invalid"].as_bool(),
        Some(true)
    );
}

#[test]
fn iec104_jsonl_rejects_malformed_frames_and_group_17() {
    let mut sidecar = Sidecar::spawn();
    let bad_frame = sidecar.send(
        "bad-frame",
        "iec104_parse_apdu",
        json!({ "frame": [0x68, 0x04, 0x03, 0, 0, 0] }),
    );
    assert_eq!(bad_frame["ok"].as_bool(), Some(false));
    assert_eq!(bad_frame["error"]["code"].as_str(), Some("IEC104_INVALID"));

    let bad_group = sidecar.send(
        "bad-group",
        "iec104_build_general_interrogation",
        json!({ "commonAddress": 1, "group": 17 }),
    );
    assert_eq!(bad_group["ok"].as_bool(), Some(false));
    assert_eq!(
        bad_group["error"]["code"].as_str(),
        Some("IEC104_PARAM_INVALID")
    );
}

#[test]
fn iec104_jsonl_runs_startdt_interrogation_testfr_and_stopdt_over_tcp() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        socket.set_read_timeout(Some(RESPONSE_TIMEOUT)).unwrap();
        assert_eq!(read_frame(&mut socket), vec![0x68, 0x04, 0x07, 0, 0, 0]);
        send_fragmented(&mut socket, &[0x68, 0x04, 0x0B, 0, 0, 0]);

        assert_eq!(
            read_frame(&mut socket),
            vec![
                0x68, 0x0E, 0, 0, 0, 0, 0x64, 0x01, 0x06, 0, 0x01, 0, 0, 0, 0, 0x14,
            ]
        );

        let responses = [
            vec![0x64, 0x01, 0x07, 0, 0x01, 0, 0, 0, 0, 0x14],
            vec![0x01, 0x01, 0x14, 0, 0x01, 0, 42, 0, 0, 0x81],
            vec![0x03, 0x01, 0x14, 0, 0x01, 0, 43, 0, 0, 0x12],
            vec![0x09, 0x01, 0x14, 0, 0x01, 0, 44, 0, 0, 0x00, 0xC0, 0xF1],
            {
                let mut asdu = vec![0x0D, 0x01, 0x14, 0, 0x01, 0, 45, 0, 0];
                asdu.extend_from_slice(&12.5f32.to_le_bytes());
                asdu.push(0);
                asdu
            },
            vec![0x0F, 0x01, 0x14, 0, 0x01, 0, 46, 0, 0, 42, 0, 0, 0, 0xE3],
            vec![0x64, 0x01, 0x0A, 0, 0x01, 0, 0, 0, 0, 0x14],
        ];
        for (index, asdu) in responses.iter().enumerate() {
            let frame = i_frame(index as u16, 1, asdu);
            if index == 2 {
                send_fragmented(&mut socket, &frame);
            } else {
                socket.write_all(&frame).unwrap();
                socket.flush().unwrap();
            }
            expect_s_ack(&mut socket, index as u16 + 1);
        }

        assert_eq!(read_frame(&mut socket), vec![0x68, 0x04, 0x43, 0, 0, 0]);
        socket.write_all(&[0x68, 0x04, 0x83, 0, 0, 0]).unwrap();
        socket.flush().unwrap();

        assert_eq!(read_frame(&mut socket), vec![0x68, 0x04, 0x13, 0, 0, 0]);
        socket.write_all(&[0x68, 0x04, 0x23, 0, 0, 0]).unwrap();
    });

    let mut sidecar = Sidecar::spawn();
    let opened = sidecar.ok(
        "open",
        "open_iec104_connection",
        json!({
            "connectionId": "iec104-test",
            "host": "127.0.0.1",
            "port": port,
            "commonAddress": 1,
            "originatorAddress": 0
        }),
    );
    assert_eq!(
        opened["handshake"].as_str(),
        Some("STARTDT_ACT/STARTDT_CON")
    );
    assert_eq!(opened["readOnly"].as_bool(), Some(true));
    assert_eq!(opened["controlsEnabled"].as_bool(), Some(false));

    let interrogation = sidecar.ok(
        "interrogate",
        "iec104_general_interrogation",
        json!({ "connectionId": "iec104-test", "group": 0 }),
    );
    assert_eq!(interrogation["activationConfirmed"].as_bool(), Some(true));
    assert_eq!(interrogation["activationTerminated"].as_bool(), Some(true));
    assert_eq!(interrogation["pointCount"].as_u64(), Some(5));
    assert_eq!(interrogation["finalReceiveSequence"].as_u64(), Some(7));
    assert_eq!(
        interrogation["acknowledgementFrames"]
            .as_array()
            .unwrap()
            .len(),
        7
    );
    assert_eq!(
        interrogation["points"][0]["value"]["kind"].as_str(),
        Some("singlePoint")
    );
    assert_eq!(
        interrogation["points"][1]["value"]["stateName"].as_str(),
        Some("on")
    );
    assert_eq!(
        interrogation["points"][2]["value"]["value"].as_f64(),
        Some(-0.5)
    );
    assert_eq!(
        interrogation["points"][3]["value"]["value"].as_f64(),
        Some(12.5)
    );
    assert_eq!(
        interrogation["points"][4]["value"]["value"].as_i64(),
        Some(42)
    );

    let tested = sidecar.ok(
        "test",
        "iec104_test_frame",
        json!({ "connectionId": "iec104-test" }),
    );
    assert_eq!(tested["confirmed"].as_bool(), Some(true));

    let closed = sidecar.ok(
        "close",
        "close_connection",
        json!({ "connectionId": "iec104-test" }),
    );
    assert_eq!(closed["closed"].as_bool(), Some(true));
    peer.join().expect("IEC104 TCP test peer failed");
}

#[test]
fn iec104_jsonl_rejects_out_of_order_peer_sequence() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let peer = thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        socket.set_read_timeout(Some(RESPONSE_TIMEOUT)).unwrap();
        assert_eq!(read_frame(&mut socket), vec![0x68, 0x04, 0x07, 0, 0, 0]);
        socket.write_all(&[0x68, 0x04, 0x0B, 0, 0, 0]).unwrap();
        socket.flush().unwrap();
        let _interrogation = read_frame(&mut socket);
        let wrong_sequence = i_frame(1, 1, &[0x64, 0x01, 0x07, 0, 0x01, 0, 0, 0, 0, 0x14]);
        socket.write_all(&wrong_sequence).unwrap();
        socket.flush().unwrap();
        let _stop = read_frame(&mut socket);
    });

    let mut sidecar = Sidecar::spawn();
    sidecar.ok(
        "open-seq",
        "open_iec104_connection",
        json!({
            "connectionId": "iec104-seq",
            "host": "127.0.0.1",
            "port": port,
            "commonAddress": 1
        }),
    );
    let failed = sidecar.send(
        "bad-seq",
        "iec104_general_interrogation",
        json!({ "connectionId": "iec104-seq", "group": 0 }),
    );
    assert_eq!(failed["ok"].as_bool(), Some(false));
    assert_eq!(
        failed["error"]["code"].as_str(),
        Some("IEC104_RECEIVE_SEQUENCE_MISMATCH")
    );
    sidecar.ok(
        "close-seq",
        "close_connection",
        json!({ "connectionId": "iec104-seq" }),
    );
    peer.join().expect("IEC104 sequence peer failed");
}
