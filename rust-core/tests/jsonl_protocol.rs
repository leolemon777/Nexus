use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::thread;
use std::time::{Duration, Instant};

use serde_json::{Value, json};

use nexus_rust_core::modbus_rtu::crc16_modbus;

const PROTOCOL_VERSION: u64 = 1;
const MAX_LINE_BYTES: usize = 1024 * 1024;
const RESPONSE_TIMEOUT: Duration = Duration::from_secs(5);

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
            .expect("failed to start the Rust sidecar binary");

        let stdin = child.stdin.take().expect("sidecar stdin was not piped");
        let stdout = child.stdout.take().expect("sidecar stdout was not piped");
        let (sender, stdout_lines) = mpsc::channel();

        thread::spawn(move || {
            let reader = BufReader::new(stdout);
            for line in reader.lines() {
                match line {
                    Ok(line) => {
                        if sender.send(line).is_err() {
                            break;
                        }
                    }
                    Err(_) => break,
                }
            }
        });

        Self {
            child,
            stdin: Some(stdin),
            stdout_lines,
        }
    }

    fn send_json(&mut self, request: &Value) -> Value {
        self.send_line(request.to_string().as_bytes());
        self.receive_json()
    }

    fn send_line(&mut self, line: &[u8]) {
        let stdin = self
            .stdin
            .as_mut()
            .expect("sidecar stdin is already closed");
        stdin
            .write_all(line)
            .and_then(|_| stdin.write_all(b"\n"))
            .and_then(|_| stdin.flush())
            .expect("failed to write a JSONL request to the sidecar");
    }

    fn receive_json(&self) -> Value {
        let line = self
            .stdout_lines
            .recv_timeout(RESPONSE_TIMEOUT)
            .expect("sidecar did not produce a response before the timeout");
        serde_json::from_str(&line)
            .unwrap_or_else(|error| panic!("stdout contained non-protocol text {line:?}: {error}"))
    }

    fn wait_for_exit(&mut self) -> std::process::ExitStatus {
        let deadline = Instant::now() + RESPONSE_TIMEOUT;
        loop {
            if let Some(status) = self
                .child
                .try_wait()
                .expect("failed to query sidecar status")
            {
                return status;
            }
            assert!(
                Instant::now() < deadline,
                "sidecar did not exit after shutdown"
            );
            thread::sleep(Duration::from_millis(10));
        }
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
    if let Some(path) = option_env!("CARGO_BIN_EXE_nexus-rust-core") {
        return PathBuf::from(path);
    }
    if let Some(path) = option_env!("CARGO_BIN_EXE_nexus_rust_core") {
        return PathBuf::from(path);
    }
    panic!("Cargo did not expose the nexus-rust-core binary to the integration test");
}

fn request(request_id: &str, command: &str, payload: Value) -> Value {
    json!({
        "protocolVersion": PROTOCOL_VERSION,
        "requestId": request_id,
        "command": command,
        "payload": payload,
    })
}

fn validation_request(request_id: &str, config: Value) -> Value {
    request(
        request_id,
        "validate_serial_config",
        json!({ "config": config }),
    )
}

fn valid_config() -> Value {
    json!({
        "portName": "COM3",
        "baudRate": 9600,
        "dataBits": 8,
        "parity": "none",
        "stopBits": "1",
        "flowControl": "none",
        "readTimeoutMs": 1000,
        "writeTimeoutMs": 1000,
        "dtrMode": "preserve",
        "rtsMode": "preserve",
    })
}

fn assert_response_envelope(response: &Value, request_id: Option<&str>) {
    assert_eq!(response["protocolVersion"], PROTOCOL_VERSION);
    match request_id {
        Some(request_id) => assert_eq!(response["requestId"], request_id),
        None => assert!(response["requestId"].is_null()),
    }
    assert!(
        response.get("ok").is_some(),
        "response is missing ok: {response}"
    );
    assert!(
        response.get("result").is_some(),
        "response is missing result: {response}"
    );
    assert!(
        response.get("error").is_some(),
        "response is missing error: {response}"
    );
}

fn assert_success(response: &Value, request_id: &str) {
    assert_response_envelope(response, Some(request_id));
    assert_eq!(response["ok"], true, "unexpected failure: {response}");
    assert!(
        !response["result"].is_null(),
        "success result must be non-null"
    );
    assert!(response["error"].is_null(), "success error must be null");
}

fn assert_error(response: &Value, request_id: Option<&str>, code: &str) {
    assert_response_envelope(response, request_id);
    assert_eq!(response["ok"], false, "unexpected success: {response}");
    assert!(response["result"].is_null(), "failure result must be null");
    assert_eq!(response["error"]["code"], code, "wrong error: {response}");
    assert!(
        response["error"]["message"]
            .as_str()
            .is_some_and(|message| !message.trim().is_empty()),
        "error message must be a non-empty string: {response}"
    );
}

#[test]
fn hello_returns_a_protocol_response_and_echoes_request_id() {
    let mut sidecar = Sidecar::spawn();
    let response = sidecar.send_json(&request("hello-回显-001", "hello", json!({})));

    assert_success(&response, "hello-回显-001");
    assert!(response["result"].is_object());
}

#[test]
fn serial_config_accepts_boundaries_and_defaults_line_modes_to_preserve() {
    let mut sidecar = Sidecar::spawn();

    let minimum = json!({
        "portName": "  com1  ",
        "baudRate": 1,
        "dataBits": 5,
        "parity": "odd",
        "stopBits": "1",
        "flowControl": "none",
        "readTimeoutMs": 1,
        "writeTimeoutMs": 1,
    });
    let minimum_response = sidecar.send_json(&validation_request("config-minimum", minimum));
    assert_success(&minimum_response, "config-minimum");
    assert_eq!(minimum_response["result"]["portName"], "com1");
    assert_eq!(minimum_response["result"]["dtrMode"], "preserve");
    assert_eq!(minimum_response["result"]["rtsMode"], "preserve");

    let maximum = json!({
        "portName": "COM999",
        "baudRate": 12_000_000,
        "dataBits": 8,
        "parity": "even",
        "stopBits": "2",
        "flowControl": "xon-xoff",
        "readTimeoutMs": 600_000,
        "writeTimeoutMs": 600_000,
        "dtrMode": "high",
        "rtsMode": "low",
    });
    let maximum_response = sidecar.send_json(&validation_request("config-maximum", maximum));
    assert_success(&maximum_response, "config-maximum");

    let mut driver_owned_rts = valid_config();
    driver_owned_rts["flowControl"] = json!("rts-cts");
    let driver_owned_response = sidecar.send_json(&validation_request(
        "config-driver-owned-rts",
        driver_owned_rts,
    ));
    assert_success(&driver_owned_response, "config-driver-owned-rts");
}

#[test]
fn serial_config_matches_electron_validation_rules() {
    let mut sidecar = Sidecar::spawn();
    let cases = [
        ("bad-port-zero", "portName", json!("COM0")),
        ("bad-port-path", "portName", json!(r"\\.\PhysicalDrive0")),
        ("bad-baud-low", "baudRate", json!(0)),
        ("bad-baud-high", "baudRate", json!(12_000_001)),
        ("bad-baud-fraction", "baudRate", json!(9_600.5)),
        ("bad-data-bits", "dataBits", json!(9)),
        ("bad-parity", "parity", json!("mark")),
        ("bad-stop-bits", "stopBits", json!("1.5")),
        ("bad-flow", "flowControl", json!("hardware")),
        ("bad-read-timeout", "readTimeoutMs", json!(0)),
        ("bad-write-timeout", "writeTimeoutMs", json!(600_001)),
        ("bad-dtr", "dtrMode", json!("toggle")),
        ("bad-rts", "rtsMode", json!("toggle")),
    ];

    for (request_id, field, invalid_value) in cases {
        let mut config = valid_config();
        config[field] = invalid_value;
        let response = sidecar.send_json(&validation_request(request_id, config));
        assert_error(&response, Some(request_id), "INVALID_SERIAL_CONFIG");
    }

    let mut driver_owned_rts = valid_config();
    driver_owned_rts["flowControl"] = json!("rts-cts");
    driver_owned_rts["rtsMode"] = json!("high");
    let response = sidecar.send_json(&validation_request("rts-cts-manual-rts", driver_owned_rts));
    assert_error(
        &response,
        Some("rts-cts-manual-rts"),
        "INVALID_SERIAL_CONFIG",
    );
}

#[test]
fn serial_config_requires_every_non_defaulted_field() {
    let mut sidecar = Sidecar::spawn();
    for field in [
        "portName",
        "baudRate",
        "dataBits",
        "parity",
        "stopBits",
        "flowControl",
        "readTimeoutMs",
        "writeTimeoutMs",
    ] {
        let mut config = valid_config();
        config.as_object_mut().unwrap().remove(field);
        let request_id = format!("missing-{field}");
        let response = sidecar.send_json(&validation_request(&request_id, config));
        assert_error(&response, Some(&request_id), "INVALID_SERIAL_CONFIG");
    }
}

#[test]
fn malformed_json_and_invalid_envelopes_have_stable_error_codes() {
    let mut sidecar = Sidecar::spawn();

    sidecar.send_line(br#"{"protocolVersion":1,"requestId":"broken""#);
    let malformed = sidecar.receive_json();
    assert_error(&malformed, None, "INVALID_JSON");

    let missing_command = json!({
        "protocolVersion": PROTOCOL_VERSION,
        "requestId": "missing-command",
        "payload": {},
    });
    let response = sidecar.send_json(&missing_command);
    assert_error(&response, Some("missing-command"), "INVALID_ENVELOPE");

    let missing_payload = json!({
        "protocolVersion": PROTOCOL_VERSION,
        "requestId": "missing-payload",
        "command": "hello",
    });
    let response = sidecar.send_json(&missing_payload);
    assert_error(&response, Some("missing-payload"), "INVALID_ENVELOPE");

    let missing_request_id = json!({
        "protocolVersion": PROTOCOL_VERSION,
        "command": "hello",
        "payload": {},
    });
    let response = sidecar.send_json(&missing_request_id);
    assert_error(&response, None, "INVALID_ENVELOPE");

    let response = sidecar.send_json(&json!([]));
    assert_error(&response, None, "INVALID_ENVELOPE");
}

#[test]
fn unsupported_versions_and_unknown_commands_echo_request_id() {
    let mut sidecar = Sidecar::spawn();

    let response = sidecar.send_json(&json!({
        "protocolVersion": 2,
        "requestId": "future-version",
        "command": "hello",
        "payload": {},
    }));
    assert_error(
        &response,
        Some("future-version"),
        "UNSUPPORTED_PROTOCOL_VERSION",
    );

    for (request_id, command) in [
        ("unknown-command", "does_not_exist"),
        ("crc-is-out-of-scope", "crc16"),
        ("modbus-is-out-of-scope", "modbus_read"),
    ] {
        let response = sidecar.send_json(&request(request_id, command, json!({})));
        assert_error(&response, Some(request_id), "UNKNOWN_COMMAND");
    }
}

#[test]
fn fc03_build_and_parse_commands_preserve_the_jsonl_contract() {
    let mut sidecar = Sidecar::spawn();

    let built = sidecar.send_json(&request(
        "fc03-build",
        "build_read_holding_registers",
        json!({ "unitId": 1, "startAddress": 0, "quantity": 2 }),
    ));
    assert_success(&built, "fc03-build");
    assert_eq!(built["result"]["adu"], json!([1, 3, 0, 0, 0, 2, 196, 11]));
    assert_eq!(built["result"]["expectedResponseLength"], 9);

    let mut response = vec![1, 3, 4, 0x12, 0x34, 0xAB, 0xCD];
    let crc = crc16_modbus(&response);
    response.extend_from_slice(&crc.to_le_bytes());
    let parsed = sidecar.send_json(&request(
        "fc03-parse",
        "parse_read_holding_registers",
        json!({ "response": response, "unitId": 1, "quantity": 2 }),
    ));
    assert_success(&parsed, "fc03-parse");
    assert_eq!(parsed["result"]["status"], "ok");
    assert_eq!(parsed["result"]["registers"], json!([0x1234, 0xABCD]));

    let mut exception = vec![1, 0x83, 0x02];
    let crc = crc16_modbus(&exception);
    exception.extend_from_slice(&crc.to_le_bytes());
    let parsed = sidecar.send_json(&request(
        "fc03-exception",
        "parse_read_holding_registers",
        json!({ "response": exception, "unitId": 1, "quantity": 125 }),
    ));
    assert_success(&parsed, "fc03-exception");
    assert_eq!(parsed["result"]["status"], "exception");
    assert_eq!(parsed["result"]["exceptionCode"], 2);
}

#[test]
fn fc04_build_parse_and_exception_preserve_the_jsonl_contract() {
    let mut sidecar = Sidecar::spawn();

    let built = sidecar.send_json(&request(
        "fc04-build",
        "build_read_input_registers",
        json!({ "unitId": 1, "startAddress": 0, "quantity": 2 }),
    ));
    assert_success(&built, "fc04-build");
    assert_eq!(built["result"]["adu"], json!([1, 4, 0, 0, 0, 2, 113, 203]));
    assert_eq!(built["result"]["expectedResponseLength"], 9);

    let mut response = vec![1, 4, 4, 0x00, 0x2A, 0xFF, 0xFE];
    let crc = crc16_modbus(&response);
    response.extend_from_slice(&crc.to_le_bytes());
    let parsed = sidecar.send_json(&request(
        "fc04-parse",
        "parse_read_input_registers",
        json!({ "response": response, "unitId": 1, "quantity": 2 }),
    ));
    assert_success(&parsed, "fc04-parse");
    assert_eq!(parsed["result"]["status"], "ok");
    assert_eq!(parsed["result"]["registers"], json!([42, 65_534]));

    let mut exception = vec![1, 0x84, 0x02];
    let crc = crc16_modbus(&exception);
    exception.extend_from_slice(&crc.to_le_bytes());
    let parsed = sidecar.send_json(&request(
        "fc04-exception",
        "parse_read_input_registers",
        json!({ "response": exception, "unitId": 1, "quantity": 125 }),
    ));
    assert_success(&parsed, "fc04-exception");
    assert_eq!(parsed["result"]["status"], "exception");
    assert_eq!(parsed["result"]["exceptionCode"], 2);
}

#[test]
fn line_limit_accepts_one_mib_and_rejects_larger_lines_without_losing_sync() {
    let mut sidecar = Sidecar::spawn();

    let prefix = r#"{"protocolVersion":1,"requestId":"exact-limit","command":"unknown-at-limit","payload":{"padding":""#;
    let suffix = r#""}}"#;
    let padding = "x".repeat(MAX_LINE_BYTES - prefix.len() - suffix.len());
    let exact_limit = format!("{prefix}{padding}{suffix}");
    assert_eq!(exact_limit.len(), MAX_LINE_BYTES);
    sidecar.send_line(exact_limit.as_bytes());
    let response = sidecar.receive_json();
    assert_error(&response, Some("exact-limit"), "UNKNOWN_COMMAND");

    sidecar.send_line(&vec![b'x'; MAX_LINE_BYTES + 1]);
    let response = sidecar.receive_json();
    assert_error(&response, None, "LINE_TOO_LONG");

    let recovery = sidecar.send_json(&request("after-long-line", "hello", json!({})));
    assert_success(&recovery, "after-long-line");
}

#[test]
fn shutdown_responds_before_exiting_cleanly() {
    let mut sidecar = Sidecar::spawn();
    let response = sidecar.send_json(&request("shutdown-001", "shutdown", json!({})));
    assert_success(&response, "shutdown-001");

    let status = sidecar.wait_for_exit();
    assert!(status.success(), "shutdown exited with {status}");
}

// ====================================================================
// 阶段 2-6 新增命令的集成测试
// ====================================================================

#[test]
fn compute_crc16_returns_canonical_vector() {
    let mut sidecar = Sidecar::spawn();
    let response = sidecar.send_json(&request(
        "crc-1",
        "compute_crc16",
        json!({ "bytes": [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A] }),
    ));
    assert_success(&response, "crc-1");
    let crc = response["result"]["crc"].as_u64().unwrap();
    // CRC16/MODBUS of [01 03 00 00 00 0A] = 0xCDC5
    assert_eq!(crc, 0xCDC5);
}

#[test]
fn compute_lrc_returns_correct_value() {
    let mut sidecar = Sidecar::spawn();
    let response = sidecar.send_json(&request(
        "lrc-1",
        "compute_lrc",
        json!({ "bytes": [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A] }),
    ));
    assert_success(&response, "lrc-1");
    let lrc = response["result"]["lrc"].as_u64().unwrap();
    // LRC of [01 03 00 00 00 0A] = -(0x0E) mod 256 = 0xF2
    assert_eq!(lrc, 0xF2);
}

#[test]
fn decode_values_decodes_float32() {
    let mut sidecar = Sidecar::spawn();
    let response = sidecar.send_json(&request(
        "decode-1",
        "decode_values",
        json!({
            "registers": [0x4049, 0x0FDB],
            "dataType": "FLOAT_BE",
            "count": 1
        }),
    ));
    assert_success(&response, "decode-1");
    let values = response["result"]["values"].as_array().unwrap();
    assert_eq!(values.len(), 1);
    // PI ≈ 3.14159...
    let f = values[0].as_f64().unwrap_or_else(|| {
        values[0]["F64"]
            .as_f64()
            .or_else(|| values[0]["f64"].as_f64())
            .unwrap_or(0.0)
    });
    assert!((f - 3.14159).abs() < 0.001, "expected PI, got {f}");
}

#[test]
fn parse_frame_offline_parses_rtu_request() {
    let mut sidecar = Sidecar::spawn();
    let response = sidecar.send_json(&request(
        "parse-1",
        "parse_frame_offline",
        json!({
            "hex": "01 03 00 00 00 0A C5 CD",
            "transport": "rtu"
        }),
    ));
    assert_success(&response, "parse-1");
    assert_eq!(response["result"]["isValid"], true);
    assert_eq!(response["result"]["baseFunctionCode"], 3);
    assert_eq!(response["result"]["address"], 0);
    assert_eq!(response["result"]["quantity"], 10);
}

#[test]
fn parse_frame_offline_rejects_invalid_hex() {
    let mut sidecar = Sidecar::spawn();
    let response = sidecar.send_json(&request(
        "parse-2",
        "parse_frame_offline",
        json!({ "hex": "xyz", "transport": "rtu" }),
    ));
    assert_error(&response, Some("parse-2"), "INVALID_SERIAL_CONFIG");
}

#[test]
fn start_and_stop_tcp_slave() {
    let mut sidecar = Sidecar::spawn();
    // 启动从站(用一个不常见的端口避免冲突)
    let port = find_free_port();
    let start_resp = sidecar.send_json(&request(
        "slave-start",
        "start_tcp_slave",
        json!({ "slaveId": "test-slave", "port": port, "allowedStationIds": [] }),
    ));
    assert_success(&start_resp, "slave-start");
    assert_eq!(start_resp["result"]["running"], true);

    // 停止从站
    let stop_resp = sidecar.send_json(&request(
        "slave-stop",
        "stop_slave",
        json!({ "slaveId": "test-slave" }),
    ));
    assert_success(&stop_resp, "slave-stop");
    assert_eq!(stop_resp["result"]["stopped"], true);
}

#[test]
fn slave_set_value_and_get_memory() {
    let mut sidecar = Sidecar::spawn();
    let port = find_free_port();
    sidecar.send_json(&request(
        "slave-init",
        "start_tcp_slave",
        json!({ "slaveId": "mem-test", "port": port }),
    ));

    // 写入值
    let set_resp = sidecar.send_json(&request(
        "slave-set",
        "slave_set_value",
        json!({ "slaveId": "mem-test", "area": "holding", "address": 5, "values": [100, 200] }),
    ));
    assert_success(&set_resp, "slave-set");

    // 读取验证
    let get_resp = sidecar.send_json(&request(
        "slave-get",
        "slave_get_memory",
        json!({ "slaveId": "mem-test", "area": "holding", "address": 5, "count": 2 }),
    ));
    assert_success(&get_resp, "slave-get");
    let values = get_resp["result"]["values"].as_array().unwrap();
    assert_eq!(values[0], 100);
    assert_eq!(values[1], 200);

    sidecar.send_json(&request("slave-stop2", "stop_slave", json!({ "slaveId": "mem-test" })));
}

#[test]
fn serial_slave_handle_bytes_responds_to_fc03() {
    let mut sidecar = Sidecar::spawn();
    // 启动串口从站
    sidecar.send_json(&request(
        "ss-start",
        "start_serial_slave",
        json!({ "slaveId": "ss1" }),
    ));

    // 预置保持寄存器 [0]=0x1234
    sidecar.send_json(&request(
        "ss-set",
        "serial_slave_set_value",
        json!({ "slaveId": "ss1", "area": "holding", "address": 0, "values": [0x1234] }),
    ));

    // 构建 FC03 读请求 RTU 帧:unit=1, fc=03, addr=0000, qty=0001
    let request_content = [0x01, 0x03, 0x00, 0x00, 0x00, 0x01];
    let crc = crc16_modbus(&request_content);
    let mut request_frame = request_content.to_vec();
    request_frame.extend_from_slice(&crc.to_le_bytes());

    let resp = sidecar.send_json(&request(
        "ss-handle",
        "slave_handle_serial_bytes",
        json!({ "slaveId": "ss1", "bytes": request_frame }),
    ));
    assert_success(&resp, "ss-handle");
    assert_eq!(resp["result"]["shouldRespond"], true);

    // 响应帧应该是:unit=1, fc=03, byte_count=02, value=12 34, CRC
    let response_bytes = resp["result"]["responseBytes"].as_array().unwrap();
    assert_eq!(response_bytes[0], 1); // unit_id
    assert_eq!(response_bytes[1], 3); // fc
    assert_eq!(response_bytes[2], 2); // byte_count
    assert_eq!(response_bytes[3], 0x12);
    assert_eq!(response_bytes[4], 0x34);

    sidecar.send_json(&request("ss-stop", "stop_serial_slave", json!({ "slaveId": "ss1" })));
}

#[test]
fn serial_slave_does_not_respond_to_broadcast() {
    let mut sidecar = Sidecar::spawn();
    sidecar.send_json(&request(
        "ss-bc-start",
        "start_serial_slave",
        json!({ "slaveId": "ss-bc" }),
    ));

    // 广播请求:unit=0, fc=06, addr=0001, value=0064
    let request_content = [0x00, 0x06, 0x00, 0x01, 0x00, 0x64];
    let crc = crc16_modbus(&request_content);
    let mut request_frame = request_content.to_vec();
    request_frame.extend_from_slice(&crc.to_le_bytes());

    let resp = sidecar.send_json(&request(
        "ss-bc-handle",
        "slave_handle_serial_bytes",
        json!({ "slaveId": "ss-bc", "bytes": request_frame }),
    ));
    assert_success(&resp, "ss-bc-handle");
    // 广播不应该响应
    assert_eq!(resp["result"]["shouldRespond"], false);

    sidecar.send_json(&request("ss-bc-stop", "stop_serial_slave", json!({ "slaveId": "ss-bc" })));
}

#[test]
fn hello_reports_v2_features_and_extended_capabilities() {
    let mut sidecar = Sidecar::spawn();
    let response = sidecar.send_json(&request(
        "hello-v2",
        "hello",
        json!({ "clientVersion": 2 }),
    ));
    assert_success(&response, "hello-v2");
    assert!(response["result"]["supportedVersions"].is_array());
    assert!(response["result"]["features"].is_array());
    let capabilities = response["result"]["capabilities"].as_array().unwrap();
    // 验证阶段 1-3 命令
    assert!(capabilities.contains(&json!("start_tcp_slave")));
    assert!(capabilities.contains(&json!("start_serial_slave")));
    assert!(capabilities.contains(&json!("decode_values")));
    assert!(capabilities.contains(&json!("parse_frame_offline")));
    assert!(capabilities.contains(&json!("start_poll_stream")));
    assert!(capabilities.contains(&json!("slave_handle_serial_bytes")));
    // 验证高级 FC 命令(FC22/23/43/08)
    assert!(capabilities.contains(&json!("tcp_mask_write_register")));
    assert!(capabilities.contains(&json!("tcp_read_write_multiple")));
    assert!(capabilities.contains(&json!("tcp_read_device_id")));
    assert!(capabilities.contains(&json!("tcp_diagnostics")));
}

#[test]
fn advanced_fc_commands_require_active_connection() {
    let mut sidecar = Sidecar::spawn();
    // FC22 屏蔽写 —— 无连接应报 CONNECTION_NOT_FOUND
    let resp = sidecar.send_json(&request(
        "fc22-no-conn",
        "tcp_mask_write_register",
        json!({ "connectionId": "nonexistent", "address": 0, "andMask": 0xFFFF, "orMask": 0x0000 }),
    ));
    assert!(!resp["ok"].as_bool().unwrap_or(true));
    assert_eq!(resp["error"]["code"], "CONNECTION_NOT_FOUND");

    // FC23 原子读写 —— 同理
    let resp = sidecar.send_json(&request(
        "fc23-no-conn",
        "tcp_read_write_multiple",
        json!({ "connectionId": "nonexistent", "readAddress": 0, "readQuantity": 1, "writeAddress": 0, "writeValues": [1] }),
    ));
    assert!(!resp["ok"].as_bool().unwrap_or(true));
    assert_eq!(resp["error"]["code"], "CONNECTION_NOT_FOUND");

    // FC43 读设备标识
    let resp = sidecar.send_json(&request(
        "fc43-no-conn",
        "tcp_read_device_id",
        json!({ "connectionId": "nonexistent" }),
    ));
    assert!(!resp["ok"].as_bool().unwrap_or(true));
    assert_eq!(resp["error"]["code"], "CONNECTION_NOT_FOUND");

    // FC08 诊断
    let resp = sidecar.send_json(&request(
        "fc08-no-conn",
        "tcp_diagnostics",
        json!({ "connectionId": "nonexistent", "subFunction": 0 }),
    ));
    assert!(!resp["ok"].as_bool().unwrap_or(true));
    assert_eq!(resp["error"]["code"], "CONNECTION_NOT_FOUND");
}

/// 找一个空闲端口用于测试。
fn find_free_port() -> u16 {
    std::net::TcpListener::bind("127.0.0.1:0")
        .map(|l| l.local_addr().unwrap().port())
        .unwrap_or(5050)
}
