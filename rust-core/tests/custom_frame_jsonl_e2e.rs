//! 自定义帧解析 JSONL 边界证据(spec-plan-serial-plot-parse-replay.md 批次 2)。
//!
//! 覆盖: hello 能力清单、binary 模式 CRC 正/反例、显式错误码、
//! ascii-delimited 模式、validate 的问题列表。这些是软件向量(S3),
//! 不等于真实设备 L2 验收。

use std::io::{BufRead, BufReader, Write};
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

fn temp_definition() -> Value {
    json!({
        "schemaVersion": 1,
        "name": "RS485 温度模块",
        "mode": "binary",
        "head": "01 03",
        "length": 9,
        "checksum": { "type": "crc16-modbus" },
        "fields": [
            { "name": "temp1", "offset": 3, "fieldType": "i16", "byteOrder": "be", "scale": 0.1, "unit": "℃" },
            { "name": "temp2", "offset": 5, "fieldType": "i16", "byteOrder": "be", "scale": 0.1, "unit": "℃" }
        ]
    })
}

#[test]
fn hello_advertises_the_custom_frame_commands() {
    let mut sidecar = Sidecar::spawn();
    let hello = sidecar.ok("hello", "hello", json!({}));
    let capabilities = hello["capabilities"].as_array().unwrap();
    for command in ["custom_frame_parse", "custom_frame_validate"] {
        assert!(
            capabilities.iter().any(|entry| entry == command),
            "{command}"
        );
    }
}

#[test]
fn jsonl_parses_a_valid_crc_frame_and_reports_fields() {
    let mut sidecar = Sidecar::spawn();
    // 01 03 02 01 F4 01 90 + CRC16(LE) = Modbus 温度双通道示例
    let body = [0x01u8, 0x03, 0x02, 0x01, 0xF4, 0x01, 0x90];
    let crc = crc16_modbus_test_helper(&body);
    let mut frame: Vec<u8> = body.to_vec();
    frame.push((crc & 0xff) as u8);
    frame.push((crc >> 8) as u8);

    let result = sidecar.ok(
        "parse",
        "custom_frame_parse",
        json!({ "definition": temp_definition(), "bytes": frame }),
    );
    assert_eq!(result["status"], "ok");
    let fields = result["fields"].as_array().unwrap();
    assert_eq!(fields.len(), 2);
    assert_eq!(fields[0]["name"], "temp1");
    assert_eq!(fields[0]["value"], json!(50.0)); // 0x01F4=500 ×0.1
    assert_eq!(fields[0]["unit"], "℃");
    assert_eq!(fields[1]["value"], json!(40.0)); // 0x0190=400 ×0.1
}

#[test]
fn jsonl_reports_explicit_errors_for_bad_frames() {
    let mut sidecar = Sidecar::spawn();

    // CRC 坏
    let mut bad: Vec<u8> = vec![0x01, 0x03, 0x02, 0x01, 0xF4, 0x01, 0x90, 0x00, 0x00];
    bad[7] = 0xFF;
    let result = sidecar.ok(
        "crc",
        "custom_frame_parse",
        json!({ "definition": temp_definition(), "bytes": bad }),
    );
    assert_eq!(result["status"], "error");
    assert_eq!(result["error"]["code"], "CHECKSUM_MISMATCH");

    // 帧头不匹配
    let result = sidecar.ok(
        "head",
        "custom_frame_parse",
        json!({ "definition": temp_definition(), "bytes": [0x02, 0x03, 0x02, 0x01, 0xF4, 0x01, 0x90, 0x00, 0x00] }),
    );
    assert_eq!(result["error"]["code"], "HEAD_MISMATCH");

    // 定长不符(帧头 01 03 匹配,撞的是定长)
    let result = sidecar.ok(
        "len",
        "custom_frame_parse",
        json!({ "definition": temp_definition(), "bytes": [0x01, 0x03, 0x02] }),
    );
    assert_eq!(result["error"]["code"], "LENGTH_MISMATCH");
}

#[test]
fn jsonl_parses_ascii_delimited_and_validates_definitions() {
    let mut sidecar = Sidecar::spawn();
    let ascii_def = json!({
        "name": "电子秤",
        "mode": "ascii-delimited",
        "separator": ",",
        "fields": [ { "name": "weight", "index": 1, "fieldType": "f32", "unit": "kg" } ]
    });
    let result = sidecar.ok(
        "ascii",
        "custom_frame_parse",
        json!({ "definition": ascii_def, "bytes": "ST,25.5,760\n".as_bytes() }),
    );
    assert_eq!(result["status"], "ok");
    assert_eq!(result["fields"][0]["name"], "weight");
    assert_eq!(result["fields"][0]["value"], json!(25.5));

    // validate: 合法定义
    let result = sidecar.ok(
        "v1",
        "custom_frame_validate",
        json!({ "definition": temp_definition() }),
    );
    assert_eq!(result["valid"], true);
    assert_eq!(result["issues"].as_array().unwrap().len(), 0);

    // validate: 字段越界 + 缺序号
    let bad_def = json!({
        "name": "坏",
        "mode": "binary",
        "length": 4,
        "fields": [ { "name": "a", "offset": 5, "fieldType": "u16" } ]
    });
    let result = sidecar.ok(
        "v2",
        "custom_frame_validate",
        json!({ "definition": bad_def }),
    );
    assert_eq!(result["valid"], false);
    let issues = result["issues"].as_array().unwrap();
    assert!(
        issues
            .iter()
            .any(|i| i.as_str().unwrap().contains("超出定长"))
    );
}

#[test]
fn jsonl_parses_dynamic_length_and_tail_delimiter_frames() {
    let mut sidecar = Sidecar::spawn();
    // 帧型: AA | len(u8, raw=3) | 01 F4 | 0D 0A ;raw+adjust(2)=5? → 本例 raw=4+adjust 1=5 字节帧体
    // 布局: AA(头) 04(len raw) 01 F4 2A(数据) | 0D 0A(tail) → 帧体 5 字节 + tail 2 字节
    let def = json!({
        "schemaVersion": 1,
        "name": "动态长度模块",
        "mode": "binary",
        "head": "AA",
        "lengthField": { "offset": 1, "fieldType": "u8", "byteOrder": "be", "adjust": 1 },
        "tail": "0D 0A",
        "fields": [ { "name": "value", "offset": 2, "fieldType": "u16", "byteOrder": "be", "scale": 0.1, "unit": "kg" } ]
    });
    let frame: Vec<u8> = vec![0xAA, 0x04, 0x01, 0xF4, 0x2A, 0x0D, 0x0A];
    let result = sidecar.ok(
        "dyn-ok",
        "custom_frame_parse",
        json!({ "definition": def, "bytes": frame }),
    );
    assert_eq!(result["status"], "ok");
    assert_eq!(result["fields"][0]["name"], "value");
    assert_eq!(result["fields"][0]["value"], json!(50.0)); // 0x01F4=500 ×0.1

    // 尾部定界不匹配
    let bad_tail: Vec<u8> = vec![0xAA, 0x04, 0x01, 0xF4, 0x2A, 0x0D, 0x0B];
    let result = sidecar.ok(
        "dyn-tail",
        "custom_frame_parse",
        json!({ "definition": def, "bytes": bad_tail }),
    );
    assert_eq!(result["status"], "error");
    assert_eq!(result["error"]["code"], "TAIL_MISMATCH");

    // 动态长度不符:raw=4+1=5,帧体只有 4 字节
    let bad_len: Vec<u8> = vec![0xAA, 0x04, 0x01, 0xF4, 0x0D, 0x0A];
    let result = sidecar.ok(
        "dyn-len",
        "custom_frame_parse",
        json!({ "definition": def, "bytes": bad_len }),
    );
    assert_eq!(result["error"]["code"], "LENGTH_MISMATCH");

    // validate: 定长与长度字段互斥
    let conflicting = json!({
        "name": "冲突",
        "mode": "binary",
        "length": 5,
        "lengthField": { "offset": 1, "fieldType": "u8" },
        "fields": [ { "name": "a", "offset": 3, "fieldType": "u8" } ]
    });
    let result = sidecar.ok(
        "dyn-conflict",
        "custom_frame_validate",
        json!({ "definition": conflicting }),
    );
    assert_eq!(result["valid"], false);
    assert!(
        result["issues"]
            .as_array()
            .unwrap()
            .iter()
            .any(|i| i.as_str().unwrap().contains("不能同时使用"))
    );
}

#[test]
fn jsonl_script_mode_accept_verify_and_expr_errors() {
    let mut sidecar = Sidecar::spawn();
    // 帧: 55 | BCD 25 | 12 | sum8;与 docs/custom-frame-golden-vectors.md 脚本向量一致
    let def = json!({
        "schemaVersion": 1,
        "name": "BCD 温湿度",
        "mode": "script",
        "accept": "frame[0] == 0x55",
        "verify": "(sum(0, len - 2) & 0xFF) == frame[len - 1]",
        "fields": [
            { "name": "temp", "expr": "bcd(frame[1])", "scale": 0.01, "unit": "℃" },
            { "name": "flag", "expr": "bit(frame[2], 4)" }
        ]
    });
    let frame: Vec<u8> = vec![0x55, 0x25, 0x12, (0x55 + 0x25 + 0x12) & 0xFF];
    let result = sidecar.ok(
        "script-ok",
        "custom_frame_parse",
        json!({ "definition": def, "bytes": frame }),
    );
    assert_eq!(result["status"], "ok");
    let fields = result["fields"].as_array().unwrap();
    assert_eq!(fields[0]["name"], "temp");
    assert_eq!(fields[0]["value"], json!(0.25)); // bcd(0x25)=25 ×0.01
    assert_eq!(fields[0]["unit"], "℃");
    assert_eq!(fields[1]["value"], json!(1.0)); // 0x12 bit4 = 1

    // accept 不满足 → FRAME_REJECTED
    let bad_head: Vec<u8> = vec![0x54, 0x25, 0x12, (0x54 + 0x25 + 0x12) & 0xFF];
    let result = sidecar.ok(
        "script-reject",
        "custom_frame_parse",
        json!({ "definition": def, "bytes": bad_head }),
    );
    assert_eq!(result["status"], "error");
    assert_eq!(result["error"]["code"], "FRAME_REJECTED");

    // verify 校验失败 → SCRIPT_VERIFY_FAILED
    let mut bad_sum = frame.clone();
    bad_sum[3] ^= 0xFF;
    let result = sidecar.ok(
        "script-verify",
        "custom_frame_parse",
        json!({ "definition": def, "bytes": bad_sum }),
    );
    assert_eq!(result["error"]["code"], "SCRIPT_VERIFY_FAILED");

    // 表达式索引越界 → EXPR_EVAL;语法错 → EXPR_SYNTAX
    let mut oob = def.clone();
    oob["fields"][0]["expr"] = json!("frame[9]");
    let result = sidecar.ok(
        "script-oob",
        "custom_frame_parse",
        json!({ "definition": oob, "bytes": frame }),
    );
    assert_eq!(result["error"]["code"], "EXPR_EVAL");
    let mut syn = def.clone();
    syn["fields"][0]["expr"] = json!("frame[0] ++");
    let result = sidecar.ok(
        "script-syn",
        "custom_frame_parse",
        json!({ "definition": syn, "bytes": frame }),
    );
    assert_eq!(result["error"]["code"], "EXPR_SYNTAX");

    // validate: 缺表达式 + 混入表单字段 → 显式问题列表
    let bad_def = json!({
        "name": "坏脚本",
        "mode": "script",
        "length": 4,
        "fields": [ { "name": "a", "fieldType": "u8" } ]
    });
    let result = sidecar.ok(
        "script-validate",
        "custom_frame_validate",
        json!({ "definition": bad_def }),
    );
    assert_eq!(result["valid"], false);
    let issues = result["issues"].as_array().unwrap();
    assert!(
        issues
            .iter()
            .any(|i| i.as_str().unwrap().contains("缺少表达式"))
    );
    assert!(
        issues
            .iter()
            .any(|i| i.as_str().unwrap().contains("脚本模式不支持"))
    );
}

/// 与 modbus_rtu::crc16_modbus 相同的多项式实现,测试内自证(不引 crate 私有模块)。
fn crc16_modbus_test_helper(bytes: &[u8]) -> u16 {
    let mut crc: u16 = 0xFFFF;
    for &b in bytes {
        crc ^= b as u16;
        for _ in 0..8 {
            if crc & 0x0001 != 0 {
                crc = (crc >> 1) ^ 0xA001;
            } else {
                crc >>= 1;
            }
        }
    }
    crc
}
