pub mod brand_profiles;
pub mod fins_address;
pub mod fins_frame;
pub mod fins_slave;
pub mod hostlink;
pub mod error;
pub mod frame_parser;
pub mod fx_links;
pub mod fx_programming;
pub mod mc_1e;
pub mod mc_address;
pub mod mc_ascii;
pub mod mc_frame;
pub mod mc_pdu;
pub mod mc_serial;
pub mod mc_slave;
pub mod modbus_ascii;
pub mod modbus_pdu;
pub mod modbus_rtu;
pub mod modbus_slave;
pub mod modbus_tcp;
pub mod protocol;
pub mod s7_address;
pub mod s7_cotp;
pub mod pn_dcp;
pub mod ppi_frame;
pub mod rk512;
pub mod uss_frame;
pub mod ppi_slave;
pub mod s7_fetchwrite;
pub mod s7_pdu;
pub mod s7_slave;
pub mod serial_config;
pub mod session;
pub mod value_codec;

use std::io::{self, BufRead, Write};

use session::Session;

pub const PROTOCOL_VERSION: u16 = 1;
pub const MAX_LINE_BYTES: usize = 1024 * 1024;

pub fn serve<R: BufRead + Send + 'static, W: Write>(reader: R, writer: W) -> io::Result<()> {
    let mut session = Session::new();
    serve_with_session(&mut session, reader, writer)
}

pub fn serve_with_session<R: BufRead + Send + 'static, W: Write>(
    session: &mut Session,
    reader: R,
    mut writer: W,
) -> io::Result<()> {
    // stdin 读取线程:阻塞读行,通过 channel 转发给主循环。
    // 修复:原实现在主循环里直接 fill_buf 阻塞读 stdin,
    // 导致 JS 端无命令时到期的轮询流永远不被触发。
    let (tx, rx) = std::sync::mpsc::channel::<io::Result<ReadLine>>();
    std::thread::spawn(move || {
        let mut reader = reader;
        loop {
            let line = read_bounded_line(&mut reader);
            let is_end = matches!(line, Ok(ReadLine::End));
            if tx.send(line).is_err() {
                break;
            }
            if is_end {
                break;
            }
        }
    });

    loop {
        // === 检查到期轮询流并推送(v2 流式协议)===
        let due_streams = session.due_poll_streams();
        for stream_id in due_streams {
            match session.fire_poll(&stream_id) {
                Ok(result) => {
                    let stream_outcome = protocol::stream_push_outcome(&stream_id, result);
                    write_outcome(&mut writer, stream_outcome)?;
                }
                // Err 不能静默吞掉:连接已关/设备掉线时,不推送错误 UI 就永远看不到,
                // 死流还会每 interval 空转。推送 stream_error 后自动停流。
                Err(e) => {
                    let outcome = protocol::stream_error_outcome(&stream_id, &e);
                    let _ = write_outcome(&mut writer, outcome);
                    session.stop_poll_stream(&stream_id);
                }
            }
        }

        // === 50ms 超时等待 stdin 请求(超时则继续检查轮询流)===
        match rx.recv_timeout(std::time::Duration::from_millis(50)) {
            Ok(Ok(ReadLine::End)) => break,
            Ok(Ok(ReadLine::TooLong)) => {
                write_outcome(&mut writer, protocol::line_too_long())?;
            }
            Ok(Ok(ReadLine::InvalidUtf8)) => {
                write_outcome(&mut writer, protocol::invalid_json())?;
            }
            Ok(Ok(ReadLine::Value(line))) => {
                let outcome = protocol::handle_line(session, &line);
                let shutdown = outcome.shutdown;
                write_outcome(&mut writer, outcome)?;
                if shutdown {
                    break;
                }
            }
            Ok(Err(e)) => return Err(e),
            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => continue,
            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => break,
        }
    }
    Ok(())
}

fn write_outcome<W: Write>(writer: &mut W, outcome: protocol::CommandOutcome) -> io::Result<()> {
    serde_json::to_writer(&mut *writer, &outcome.response).map_err(io::Error::other)?;
    writer.write_all(b"\n")?;
    writer.flush()
}

enum ReadLine {
    End,
    Value(String),
    TooLong,
    InvalidUtf8,
}

fn read_bounded_line<R: BufRead>(reader: &mut R) -> io::Result<ReadLine> {
    let mut bytes = Vec::new();
    let mut too_long = false;
    let mut saw_data = false;

    loop {
        let available = reader.fill_buf()?;
        if available.is_empty() {
            if !saw_data {
                return Ok(ReadLine::End);
            }
            break;
        }

        saw_data = true;
        let newline = available.iter().position(|byte| *byte == b'\n');
        let take = newline.map_or(available.len(), |index| index + 1);
        let content_end = newline.unwrap_or(take);
        let content = &available[..content_end];

        if !too_long {
            if bytes.len() + content.len() > MAX_LINE_BYTES {
                too_long = true;
            } else {
                bytes.extend_from_slice(content);
            }
        }

        reader.consume(take);
        if newline.is_some() {
            break;
        }
    }

    if too_long {
        return Ok(ReadLine::TooLong);
    }
    if bytes.last() == Some(&b'\r') {
        bytes.pop();
    }
    Ok(match String::from_utf8(bytes) {
        Ok(line) => ReadLine::Value(line),
        Err(_) => ReadLine::InvalidUtf8,
    })
}

#[cfg(test)]
mod tests {
    use std::io::Cursor;

    use super::*;

    #[test]
    fn oversized_line_does_not_poison_the_next_request() {
        let oversized = "x".repeat(MAX_LINE_BYTES + 1);
        let hello = r#"{"protocolVersion":1,"requestId":"next","command":"hello","payload":{}}"#;
        let input = format!("{oversized}\n{hello}\n");
        let mut output = Vec::new();
        serve(Cursor::new(input), &mut output).unwrap();
        let output = String::from_utf8(output).unwrap();
        let lines: Vec<_> = output.lines().collect();
        assert_eq!(lines.len(), 2);
        assert!(lines[0].contains("LINE_TOO_LONG"));
        assert!(lines[1].contains("\"requestId\":\"next\""));
    }
}
