//! Allen-Bradley EtherNet/IP encapsulation and unconnected CIP codec.
//!
//! This module is the EtherNet/IP explicit-message codec boundary:
//! RegisterSession/UnregisterSession, SendRRData + CPF, and a read-only CIP
//! Read Tag request.  The session layer owns TCP I/O; this codec does not
//! perform ForwardOpen/connected I/O or claim CompactLogix/ControlLogix L2
//! evidence.

use crate::error::CoreError;

pub const ENIP_HEADER_BYTES: usize = 24;
pub const ENIP_REGISTER_SESSION: u16 = 0x0065;
pub const ENIP_UNREGISTER_SESSION: u16 = 0x0066;
pub const ENIP_SEND_RR_DATA: u16 = 0x006F;
pub const CIP_READ_TAG: u8 = 0x4C;
pub const CIP_READ_TAG_REPLY: u8 = CIP_READ_TAG | 0x80;

const CPF_NULL_ADDRESS: u16 = 0x0000;
const CPF_UNCONNECTED_DATA: u16 = 0x00B2;
const CPF_CONNECTED_DATA: u16 = 0x00B1;
const MAX_ENIP_PAYLOAD: usize = u16::MAX as usize;
const MAX_CIP_PATH_BYTES: usize = 510;

fn enip_err(message: impl Into<String>) -> CoreError {
    CoreError::Modbus {
        code: "ENIP_INVALID",
        message: message.into(),
        details: None,
    }
}

fn param_err(message: impl Into<String>, details: serde_json::Value) -> CoreError {
    CoreError::Modbus {
        code: "ENIP_PARAM_INVALID",
        message: message.into(),
        details: Some(details),
    }
}

fn put_u16_le(out: &mut Vec<u8>, value: u16) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn put_u32_le(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn get_u16_le(bytes: &[u8], offset: usize) -> Result<u16, CoreError> {
    let end = offset.saturating_add(2);
    let pair = bytes
        .get(offset..end)
        .ok_or_else(|| enip_err(format!("ENIP 帧在 {offset} 处缺少 u16 字段")))?;
    Ok(u16::from_le_bytes([pair[0], pair[1]]))
}

fn get_u32_le(bytes: &[u8], offset: usize) -> Result<u32, CoreError> {
    let end = offset.saturating_add(4);
    let word = bytes
        .get(offset..end)
        .ok_or_else(|| enip_err(format!("ENIP 帧在 {offset} 处缺少 u32 字段")))?;
    Ok(u32::from_le_bytes([word[0], word[1], word[2], word[3]]))
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
}

/// A parsed EtherNet/IP encapsulation header plus its exact payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EncapsulationFrame {
    pub command: u16,
    pub length: u16,
    pub session_handle: u32,
    pub status: u32,
    pub sender_context: [u8; 8],
    pub options: u32,
    pub payload: Vec<u8>,
}

/// Parsed RegisterSession reply.  The explicit TCP session layer uses this
/// instead of treating any 24-byte encapsulation frame as a successful login.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RegisterSessionResponse {
    pub session_handle: u32,
    pub sender_context: u64,
    pub protocol_version: u16,
    pub options: u16,
}

/// Parsed CIP response carried by an EtherNet/IP SendRRData CPF.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CipResponse {
    pub service: u8,
    pub reply_path_size: u8,
    pub general_status: u8,
    pub additional_status: Vec<u16>,
    pub data: Vec<u8>,
    pub cpf_item_type: u16,
}

pub fn build_register_session(session_handle: u32, sender_context: u64) -> Vec<u8> {
    build_encapsulation(
        ENIP_REGISTER_SESSION,
        session_handle,
        sender_context,
        &[0x01, 0x00, 0x00, 0x00],
    )
}

pub fn build_unregister_session(session_handle: u32, sender_context: u64) -> Vec<u8> {
    build_encapsulation(ENIP_UNREGISTER_SESSION, session_handle, sender_context, &[])
}

pub fn build_read_tag(
    session_handle: u32,
    sender_context: u64,
    tag: &str,
    elements: u16,
) -> Result<Vec<u8>, CoreError> {
    if elements == 0 {
        return Err(param_err(
            "CIP Read Tag elements 必须是 1..65535",
            serde_json::json!({ "elements": elements }),
        ));
    }
    let path = encode_tag_path(tag)?;
    let path_words = path.len() / 2;
    if path_words == 0 || path_words > u8::MAX as usize {
        return Err(param_err(
            "CIP Tag 路径字数必须是 1..255",
            serde_json::json!({ "pathBytes": path.len(), "pathWords": path_words }),
        ));
    }

    let mut cip = Vec::with_capacity(2 + path.len() + 2);
    cip.push(CIP_READ_TAG);
    cip.push(path_words as u8);
    cip.extend_from_slice(&path);
    put_u16_le(&mut cip, elements);

    let cpf = build_unconnected_data_cpf(&cip)?;
    Ok(build_encapsulation(
        ENIP_SEND_RR_DATA,
        session_handle,
        sender_context,
        &cpf,
    ))
}

pub fn parse_encapsulation(frame: &[u8]) -> Result<EncapsulationFrame, CoreError> {
    if frame.len() < ENIP_HEADER_BYTES {
        return Err(enip_err(format!(
            "ENIP 帧至少需要 {ENIP_HEADER_BYTES} 字节，实际 {}",
            frame.len()
        )));
    }
    let length = get_u16_le(frame, 2)? as usize;
    let expected = ENIP_HEADER_BYTES + length;
    if frame.len() != expected {
        return Err(enip_err(format!(
            "ENIP 负载长度不匹配:头部声明 {length}B，实际 {}B",
            frame.len().saturating_sub(ENIP_HEADER_BYTES)
        )));
    }
    let mut sender_context = [0u8; 8];
    sender_context.copy_from_slice(&frame[12..20]);
    let options = get_u32_le(frame, 20)?;
    if options != 0 {
        return Err(enip_err(format!(
            "ENIP Options 必须为 0，收到 0x{options:08X}"
        )));
    }
    Ok(EncapsulationFrame {
        command: get_u16_le(frame, 0)?,
        length: length as u16,
        session_handle: get_u32_le(frame, 4)?,
        status: get_u32_le(frame, 8)?,
        sender_context,
        options,
        payload: frame[ENIP_HEADER_BYTES..].to_vec(),
    })
}

pub fn parse_register_session_response(
    frame: &[u8],
    expected_sender_context: Option<u64>,
) -> Result<RegisterSessionResponse, CoreError> {
    let parsed = parse_encapsulation(frame)?;
    if parsed.command != ENIP_REGISTER_SESSION {
        return Err(enip_err(format!(
            "RegisterSession 响应命令必须为 0x{ENIP_REGISTER_SESSION:04X}，收到 0x{:04X}",
            parsed.command
        )));
    }
    if parsed.status != 0 {
        return Err(enip_err(format!(
            "RegisterSession 封装层返回错误状态 0x{:08X}",
            parsed.status
        )));
    }
    if parsed.session_handle == 0 {
        return Err(enip_err("RegisterSession 响应的 Session Handle 不能为 0"));
    }
    if parsed.payload.len() != 4 {
        return Err(enip_err(format!(
            "RegisterSession 响应 payload 必须为 4 字节，实际 {}",
            parsed.payload.len()
        )));
    }
    let protocol_version = get_u16_le(&parsed.payload, 0)?;
    let options = get_u16_le(&parsed.payload, 2)?;
    if protocol_version != 1 || options != 0 {
        return Err(enip_err(format!(
            "RegisterSession 响应协议版本/选项不受支持: version={protocol_version}, options=0x{options:04X}"
        )));
    }
    let sender_context = u64::from_le_bytes(parsed.sender_context);
    if let Some(expected) = expected_sender_context {
        if sender_context != expected {
            return Err(enip_err(format!(
                "RegisterSession Sender Context 不匹配:期望 {expected}，收到 {sender_context}"
            )));
        }
    }
    Ok(RegisterSessionResponse {
        session_handle: parsed.session_handle,
        sender_context,
        protocol_version,
        options,
    })
}

pub fn parse_cip_response(frame: &[u8]) -> Result<CipResponse, CoreError> {
    let enip = parse_encapsulation(frame)?;
    if enip.command != ENIP_SEND_RR_DATA {
        return Err(enip_err(format!(
            "CIP 响应必须来自 SendRRData(0x{ENIP_SEND_RR_DATA:04X})，收到 0x{:04X}",
            enip.command
        )));
    }
    if enip.status != 0 {
        return Err(enip_err(format!(
            "EtherNet/IP 封装层返回错误状态 0x{:08X}",
            enip.status
        )));
    }
    parse_cpf_cip_response(&enip.payload)
}

pub fn parse_cpf_cip_response(payload: &[u8]) -> Result<CipResponse, CoreError> {
    if payload.len() < 8 {
        return Err(enip_err("SendRRData CPF 负载过短"));
    }
    if payload[0..4] != [0, 0, 0, 0] {
        return Err(enip_err("CPF Interface Handle 必须为 0"));
    }
    let item_count = get_u16_le(payload, 6)? as usize;
    if item_count == 0 {
        return Err(enip_err("CPF Item Count 不能为 0"));
    }
    let mut offset = 8usize;
    let mut cip_reply: Option<(u16, Vec<u8>)> = None;
    for _ in 0..item_count {
        if offset.saturating_add(4) > payload.len() {
            return Err(enip_err("CPF 项目头被截断"));
        }
        let item_type = get_u16_le(payload, offset)?;
        let item_length = get_u16_le(payload, offset + 2)? as usize;
        offset += 4;
        let end = offset.saturating_add(item_length);
        if end > payload.len() {
            return Err(enip_err("CPF 项目数据被截断"));
        }
        match item_type {
            CPF_UNCONNECTED_DATA => {
                cip_reply = Some((item_type, payload[offset..end].to_vec()));
            }
            CPF_CONNECTED_DATA => {
                if item_length < 2 {
                    return Err(enip_err("Connected Data CPF 项目缺少序列号"));
                }
                cip_reply = Some((item_type, payload[offset + 2..end].to_vec()));
            }
            CPF_NULL_ADDRESS => {}
            _ => {}
        }
        offset = end;
    }
    if offset != payload.len() {
        return Err(enip_err("CPF 项目长度与 SendRRData 负载不一致"));
    }
    let (cpf_item_type, reply) = cip_reply.ok_or_else(|| enip_err("CPF 缺少 CIP 数据项目"))?;
    if reply.len() < 4 {
        return Err(enip_err(
            "CIP Reply 至少需要 service/path/status/additional 字段",
        ));
    }
    let service = reply[0];
    let reply_path_size = reply[1];
    let general_status = reply[2];
    let additional_words = reply[3] as usize;
    let additional_bytes = additional_words
        .checked_mul(2)
        .ok_or_else(|| enip_err("CIP Additional Status 长度溢出"))?;
    let data_offset = 4usize
        .checked_add(additional_bytes)
        .ok_or_else(|| enip_err("CIP 数据偏移溢出"))?;
    if data_offset > reply.len() {
        return Err(enip_err("CIP Additional Status 长度超过响应边界"));
    }
    let mut additional_status = Vec::with_capacity(additional_words);
    for index in 0..additional_words {
        additional_status.push(get_u16_le(&reply, 4 + index * 2)?);
    }
    Ok(CipResponse {
        service,
        reply_path_size,
        general_status,
        additional_status,
        data: reply[data_offset..].to_vec(),
        cpf_item_type,
    })
}

pub fn cip_status_message(status: u8) -> &'static str {
    match status {
        0x00 => "成功",
        0x01 => "连接失败",
        0x02 => "资源不可用",
        0x03 => "无效参数值",
        0x04 => "路径段错误",
        0x05 => "路径目的地未知",
        0x06 => "部分转移",
        0x07 => "连接丢失",
        0x08 => "服务不支持",
        0x09 => "无效属性值",
        0x0A => "属性列表错误",
        0x0B => "数据太多",
        0x0C => "对象不支持此属性",
        0x0D => "属性列表获取失败",
        0x0E => "属性列表设置失败",
        0x0F => "属性不可设置",
        0x10 => "属性不可获取",
        0x13 => "提供的数据量不足",
        0x14 => "属性列表中没有此属性",
        0x15 => "数据类型不匹配",
        0x16 => "数据超出范围",
        _ => "未知 CIP 状态",
    }
}

pub fn encode_tag_path(tag: &str) -> Result<Vec<u8>, CoreError> {
    let tag = tag.trim();
    if tag.is_empty() || tag.len() > 510 || tag.chars().any(char::is_control) {
        return Err(param_err(
            "CIP Tag 名称不能为空、不能含控制字符，且长度不能超过 510 字节",
            serde_json::json!({ "tag": tag }),
        ));
    }

    let (program_prefix, actual_tag) = if let Some(rest) = tag
        .strip_prefix("Program:")
        .or_else(|| tag.strip_prefix("program:"))
    {
        let (program, remainder) = rest.split_once('.').unwrap_or((rest, ""));
        if program.is_empty() {
            return Err(param_err(
                "Program: 前缀后必须有程序名",
                serde_json::json!({ "tag": tag }),
            ));
        }
        (Some(format!("Program:{program}")), remainder)
    } else {
        (None, tag)
    };

    let mut path = Vec::new();
    if let Some(program) = program_prefix {
        push_symbol_segment(&mut path, &program)?;
    }
    if actual_tag.is_empty() {
        return Err(param_err(
            "CIP Tag 路径缺少实际标签名",
            serde_json::json!({ "tag": tag }),
        ));
    }
    for part in actual_tag.split('.') {
        if part.is_empty() {
            return Err(param_err(
                "CIP Tag 成员路径不能包含空段",
                serde_json::json!({ "tag": tag }),
            ));
        }
        push_tag_part(&mut path, part)?;
    }
    if path.len() % 2 != 0 || path.len() > MAX_CIP_PATH_BYTES {
        return Err(enip_err("CIP Tag 路径必须是偶数字节且不能超过 510 字节"));
    }
    Ok(path)
}

fn push_tag_part(path: &mut Vec<u8>, part: &str) -> Result<(), CoreError> {
    let first_index = part.find('[');
    let name = first_index.map_or(part, |index| &part[..index]);
    if !name.is_empty() {
        push_symbol_segment(path, name)?;
    }
    let mut cursor = first_index;
    while let Some(start) = cursor {
        let tail = &part[start + 1..];
        let close = tail
            .find(']')
            .ok_or_else(|| param_err("CIP 数组索引缺少 ]", serde_json::json!({ "part": part })))?;
        let index_text = &tail[..close];
        let index = index_text.parse::<u32>().map_err(|_| {
            param_err(
                "CIP 数组索引必须是非负整数",
                serde_json::json!({ "index": index_text }),
            )
        })?;
        push_array_index(path, index)?;
        let next = start + 1 + close + 1;
        if next == part.len() {
            cursor = None;
        } else if part.as_bytes().get(next) == Some(&b'[') {
            cursor = Some(next);
        } else {
            return Err(param_err(
                "CIP 数组索引后只能继续使用 [index]",
                serde_json::json!({ "part": part }),
            ));
        }
    }
    Ok(())
}

fn push_symbol_segment(path: &mut Vec<u8>, name: &str) -> Result<(), CoreError> {
    let bytes = name.as_bytes();
    if bytes.is_empty() || bytes.len() > u8::MAX as usize {
        return Err(param_err(
            "CIP 符号段长度必须是 1..255 字节",
            serde_json::json!({ "name": name, "bytes": bytes.len() }),
        ));
    }
    path.extend_from_slice(&[0x91, bytes.len() as u8]);
    path.extend_from_slice(bytes);
    if bytes.len() % 2 == 1 {
        path.push(0);
    }
    Ok(())
}

fn push_array_index(path: &mut Vec<u8>, index: u32) -> Result<(), CoreError> {
    const MAX_U16: u32 = u16::MAX as u32;
    match index {
        0..=255 => path.extend_from_slice(&[0x28, index as u8]),
        256..=MAX_U16 => {
            path.extend_from_slice(&[0x29, 0x00]);
            path.extend_from_slice(&(index as u16).to_le_bytes());
        }
        _ => {
            path.extend_from_slice(&[0x2A, 0x00]);
            path.extend_from_slice(&index.to_le_bytes());
        }
    }
    Ok(())
}

fn build_unconnected_data_cpf(cip: &[u8]) -> Result<Vec<u8>, CoreError> {
    // CPF 固定字段占 16 字节，外层 ENIP Length 是 u16。
    if cip.is_empty() || cip.len() > (u16::MAX as usize).saturating_sub(16) {
        return Err(param_err(
            "CIP 数据长度必须是 1..65519 字节",
            serde_json::json!({ "bytes": cip.len() }),
        ));
    }
    let mut cpf = Vec::with_capacity(16 + cip.len());
    put_u32_le(&mut cpf, 0); // Interface Handle
    put_u16_le(&mut cpf, 0); // Timeout
    put_u16_le(&mut cpf, 2); // Item Count
    put_u16_le(&mut cpf, CPF_NULL_ADDRESS);
    put_u16_le(&mut cpf, 0);
    put_u16_le(&mut cpf, CPF_UNCONNECTED_DATA);
    put_u16_le(&mut cpf, cip.len() as u16);
    cpf.extend_from_slice(cip);
    Ok(cpf)
}

fn build_encapsulation(
    command: u16,
    session_handle: u32,
    sender_context: u64,
    payload: &[u8],
) -> Vec<u8> {
    debug_assert!(payload.len() <= MAX_ENIP_PAYLOAD);
    let mut frame = Vec::with_capacity(ENIP_HEADER_BYTES + payload.len());
    put_u16_le(&mut frame, command);
    put_u16_le(&mut frame, payload.len() as u16);
    put_u32_le(&mut frame, session_handle);
    put_u32_le(&mut frame, 0); // status
    frame.extend_from_slice(&sender_context.to_le_bytes());
    put_u32_le(&mut frame, 0); // options
    frame.extend_from_slice(payload);
    frame
}

pub fn frame_hex(frame: &[u8]) -> String {
    hex(frame)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn register_session_has_standard_header_and_payload() {
        let frame = build_register_session(0, 1);
        assert_eq!(frame.len(), 28);
        assert_eq!(&frame[0..4], &[0x65, 0x00, 0x04, 0x00]);
        assert_eq!(&frame[12..20], &[1, 0, 0, 0, 0, 0, 0, 0]);
        assert_eq!(&frame[24..], &[1, 0, 0, 0]);
        let parsed = parse_encapsulation(&frame).unwrap();
        assert_eq!(parsed.command, ENIP_REGISTER_SESSION);
        assert_eq!(parsed.payload, vec![1, 0, 0, 0]);
    }

    #[test]
    fn read_tag_encodes_symbolic_path_and_cpf() {
        let frame = build_read_tag(0x1122_3344, 7, "MyTag[3]", 2).unwrap();
        let parsed = parse_encapsulation(&frame).unwrap();
        assert_eq!(parsed.command, ENIP_SEND_RR_DATA);
        assert_eq!(parsed.session_handle, 0x1122_3344);
        assert_eq!(&parsed.payload[0..8], &[0, 0, 0, 0, 0, 0, 2, 0]);
        assert_eq!(
            &parsed.payload[16..],
            &[
                0x4C, 0x05, 0x91, 0x05, b'M', b'y', b'T', b'a', b'g', 0, 0x28, 3, 2, 0
            ]
        );
    }

    #[test]
    fn parse_cip_error_keeps_general_and_extended_status() {
        let cip = [CIP_READ_TAG_REPLY, 0, 0x04, 1, 0x20, 0x00, 0xAA];
        let cpf = build_unconnected_data_cpf(&cip).unwrap();
        let frame = build_encapsulation(ENIP_SEND_RR_DATA, 5, 9, &cpf);
        let parsed = parse_cip_response(&frame).unwrap();
        assert_eq!(parsed.service, CIP_READ_TAG_REPLY);
        assert_eq!(parsed.general_status, 0x04);
        assert_eq!(parsed.additional_status, vec![0x0020]);
        assert_eq!(parsed.data, vec![0xAA]);
    }

    #[test]
    fn malformed_lengths_and_tag_indices_fail_closed() {
        let mut frame = build_register_session(0, 0);
        frame[2] = 5;
        assert!(parse_encapsulation(&frame).is_err());
        assert!(encode_tag_path("Tag[-1]").is_err());
        assert!(encode_tag_path("Tag[1").is_err());
        assert!(encode_tag_path("Program:.Tag").is_err());
    }
}
