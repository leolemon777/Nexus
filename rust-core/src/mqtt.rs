//! MQTT 3.1.1 codec for a read-only broker session.
//!
//! The first MQTT boundary is deliberately narrow: CONNECT/CONNACK,
//! SUBSCRIBE/SUBACK, PUBLISH decoding, PINGREQ/PINGRESP and DISCONNECT.  The
//! live session never publishes a value or sends a command topic.  Sparkplug
//! B state machines, retained writes, QoS acknowledgements and TLS/auth
//! policy remain separate follow-up gates.

use std::str;

use crate::error::CoreError;

pub const DEFAULT_PORT: u16 = 1883;
pub const MQTT_PROTOCOL_LEVEL_311: u8 = 4;
pub const MAX_REMAINING_LENGTH: usize = 1024 * 1024;
pub const PACKET_CONNECT: u8 = 1;
pub const PACKET_CONNACK: u8 = 2;
pub const PACKET_PUBLISH: u8 = 3;
pub const PACKET_SUBSCRIBE: u8 = 8;
pub const PACKET_SUBACK: u8 = 9;
pub const PACKET_PINGREQ: u8 = 12;
pub const PACKET_PINGRESP: u8 = 13;
pub const PACKET_DISCONNECT: u8 = 14;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Frame {
    pub packet_type: u8,
    pub flags: u8,
    pub remaining_length: usize,
    pub payload: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConnAck {
    pub session_present: bool,
    pub return_code: u8,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SubAck {
    pub packet_id: u16,
    pub return_code: u8,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Publish {
    pub dup: bool,
    pub qos: u8,
    pub retain: bool,
    pub topic: String,
    pub packet_id: Option<u16>,
    pub payload: Vec<u8>,
}

fn mqtt_error(
    code: &'static str,
    message: impl Into<String>,
    details: Option<serde_json::Value>,
) -> CoreError {
    CoreError::Modbus {
        code,
        message: message.into(),
        details,
    }
}

fn invalid(message: impl Into<String>, details: Option<serde_json::Value>) -> CoreError {
    mqtt_error("MQTT_INVALID", message, details)
}

fn param(message: impl Into<String>, details: serde_json::Value) -> CoreError {
    mqtt_error("MQTT_PARAM_INVALID", message, Some(details))
}

fn push_u16(out: &mut Vec<u8>, value: u16) {
    out.extend_from_slice(&value.to_be_bytes());
}

fn read_u16(bytes: &[u8], offset: usize) -> Result<u16, CoreError> {
    let pair = bytes
        .get(offset..offset.saturating_add(2))
        .ok_or_else(|| invalid(format!("MQTT 帧在 {offset} 处缺少 u16 字段"), None))?;
    Ok(u16::from_be_bytes([pair[0], pair[1]]))
}

fn validate_utf8_field(value: &str, field: &'static str) -> Result<(), CoreError> {
    let bytes = value.as_bytes();
    if bytes.is_empty() {
        return Err(param(
            format!("MQTT {field} 不能为空"),
            serde_json::json!({ "field": field }),
        ));
    }
    if bytes.len() > u16::MAX as usize || bytes.contains(&0) {
        return Err(param(
            format!("MQTT {field} 长度或 NUL 字节无效"),
            serde_json::json!({ "field": field, "bytes": bytes.len(), "maximum": u16::MAX }),
        ));
    }
    if std::str::from_utf8(bytes).is_err() {
        return Err(param(
            format!("MQTT {field} 不是有效 UTF-8"),
            serde_json::json!({ "field": field }),
        ));
    }
    Ok(())
}

fn push_utf8(out: &mut Vec<u8>, value: &str, field: &'static str) -> Result<(), CoreError> {
    validate_utf8_field(value, field)?;
    push_u16(out, value.len() as u16);
    out.extend_from_slice(value.as_bytes());
    Ok(())
}

/// Encode the MQTT variable-length remaining-length field.
pub fn encode_remaining_length(mut value: usize) -> Result<Vec<u8>, CoreError> {
    if value > MAX_REMAINING_LENGTH {
        return Err(param(
            "MQTT remaining length 超出软件上限",
            serde_json::json!({ "value": value, "maximum": MAX_REMAINING_LENGTH }),
        ));
    }
    let mut encoded = Vec::with_capacity(4);
    loop {
        let mut byte = (value % 128) as u8;
        value /= 128;
        if value > 0 {
            byte |= 0x80;
        }
        encoded.push(byte);
        if value == 0 {
            return Ok(encoded);
        }
    }
}

/// Decode remaining length from `bytes[offset..]`, returning `(length, bytes_used)`.
pub fn decode_remaining_length(bytes: &[u8], offset: usize) -> Result<(usize, usize), CoreError> {
    let mut multiplier = 1usize;
    let mut value = 0usize;
    for index in 0..4usize {
        let byte = *bytes
            .get(offset.saturating_add(index))
            .ok_or_else(|| invalid("MQTT remaining length 字段不完整", None))?;
        value = value
            .checked_add(((byte & 0x7F) as usize).saturating_mul(multiplier))
            .ok_or_else(|| invalid("MQTT remaining length 溢出", None))?;
        if value > MAX_REMAINING_LENGTH {
            return Err(invalid(
                "MQTT remaining length 超出软件上限",
                Some(serde_json::json!({ "value": value, "maximum": MAX_REMAINING_LENGTH })),
            ));
        }
        if byte & 0x80 == 0 {
            return Ok((value, index + 1));
        }
        multiplier = multiplier
            .checked_mul(128)
            .ok_or_else(|| invalid("MQTT remaining length multiplier 溢出", None))?;
    }
    Err(invalid("MQTT remaining length 最多允许 4 个字节", None))
}

fn build_packet(packet_type: u8, flags: u8, payload: &[u8]) -> Result<Vec<u8>, CoreError> {
    if !(1..=14).contains(&packet_type) {
        return Err(param(
            "MQTT packet type 无效",
            serde_json::json!({ "packetType": packet_type }),
        ));
    }
    if flags & 0xF0 != 0 {
        return Err(param(
            "MQTT fixed-header flags 超出 4 bit",
            serde_json::json!({ "flags": flags }),
        ));
    }
    let remaining = encode_remaining_length(payload.len())?;
    let mut frame = Vec::with_capacity(1 + remaining.len() + payload.len());
    frame.push((packet_type << 4) | flags);
    frame.extend_from_slice(&remaining);
    frame.extend_from_slice(payload);
    Ok(frame)
}

/// Parse one complete MQTT packet.  TCP framing is owned by Session; this
/// function still validates that the supplied bytes contain exactly one frame.
pub fn parse_frame(bytes: &[u8]) -> Result<Frame, CoreError> {
    if bytes.len() < 2 {
        return Err(invalid("MQTT 帧至少需要固定头和长度字段", None));
    }
    let fixed = bytes[0];
    let packet_type = fixed >> 4;
    let flags = fixed & 0x0F;
    if packet_type == 0 || packet_type == 15 {
        return Err(invalid(
            "MQTT packet type 无效",
            Some(serde_json::json!({ "packetType": packet_type })),
        ));
    }
    let (remaining_length, length_bytes) = decode_remaining_length(bytes, 1)?;
    let payload_start = 1 + length_bytes;
    let payload_end = payload_start
        .checked_add(remaining_length)
        .ok_or_else(|| invalid("MQTT 帧长度溢出", None))?;
    if payload_end != bytes.len() {
        return Err(invalid(
            "MQTT 帧长度与 remaining length 不一致",
            Some(
                serde_json::json!({ "declared": remaining_length, "actual": bytes.len().saturating_sub(payload_start) }),
            ),
        ));
    }
    Ok(Frame {
        packet_type,
        flags,
        remaining_length,
        payload: bytes[payload_start..payload_end].to_vec(),
    })
}

/// Build MQTT 3.1.1 CONNECT without username/password or will messages.
pub fn build_connect(
    client_id: &str,
    keep_alive: u16,
    clean_session: bool,
) -> Result<Vec<u8>, CoreError> {
    validate_utf8_field(client_id, "clientId")?;
    let mut payload = Vec::with_capacity(10 + client_id.len());
    push_utf8(&mut payload, "MQTT", "protocolName")?;
    payload.push(MQTT_PROTOCOL_LEVEL_311);
    payload.push(if clean_session { 0x02 } else { 0x00 });
    push_u16(&mut payload, keep_alive);
    push_utf8(&mut payload, client_id, "clientId")?;
    build_packet(PACKET_CONNECT, 0, &payload)
}

pub fn parse_connack(frame: &[u8]) -> Result<ConnAck, CoreError> {
    let parsed = parse_frame(frame)?;
    if parsed.packet_type != PACKET_CONNACK || parsed.flags != 0 || parsed.payload.len() != 2 {
        return Err(invalid(
            "MQTT CONNACK 固定头或长度无效",
            Some(
                serde_json::json!({ "packetType": parsed.packet_type, "flags": parsed.flags, "length": parsed.payload.len() }),
            ),
        ));
    }
    let ack_flags = parsed.payload[0];
    let return_code = parsed.payload[1];
    if ack_flags & 0xFE != 0 {
        return Err(invalid("MQTT CONNACK acknowledge flags 非法", None));
    }
    if return_code > 5 {
        return Err(invalid(
            "MQTT CONNACK return code 未定义",
            Some(serde_json::json!({ "returnCode": return_code })),
        ));
    }
    Ok(ConnAck {
        session_present: ack_flags & 1 != 0,
        return_code,
    })
}

pub fn build_subscribe(packet_id: u16, topic_filter: &str, qos: u8) -> Result<Vec<u8>, CoreError> {
    if packet_id == 0 {
        return Err(param(
            "MQTT SUBSCRIBE packetId 不能为 0",
            serde_json::json!({ "packetId": packet_id }),
        ));
    }
    if qos > 2 {
        return Err(param(
            "MQTT subscription QoS 必须为 0..2",
            serde_json::json!({ "qos": qos }),
        ));
    }
    validate_utf8_field(topic_filter, "topicFilter")?;
    let mut payload = Vec::with_capacity(3 + topic_filter.len());
    push_u16(&mut payload, packet_id);
    push_utf8(&mut payload, topic_filter, "topicFilter")?;
    payload.push(qos);
    build_packet(PACKET_SUBSCRIBE, 0x02, &payload)
}

pub fn parse_suback(frame: &[u8]) -> Result<SubAck, CoreError> {
    let parsed = parse_frame(frame)?;
    if parsed.packet_type != PACKET_SUBACK || parsed.flags != 0 || parsed.payload.len() != 3 {
        return Err(invalid(
            "MQTT SUBACK 固定头或长度无效",
            Some(
                serde_json::json!({ "packetType": parsed.packet_type, "flags": parsed.flags, "length": parsed.payload.len() }),
            ),
        ));
    }
    let return_code = parsed.payload[2];
    if !matches!(return_code, 0 | 1 | 2 | 0x80) {
        return Err(invalid(
            "MQTT SUBACK return code 未定义",
            Some(serde_json::json!({ "returnCode": return_code })),
        ));
    }
    Ok(SubAck {
        packet_id: read_u16(&parsed.payload, 0)?,
        return_code,
    })
}

pub fn parse_publish(frame: &[u8]) -> Result<Publish, CoreError> {
    let parsed = parse_frame(frame)?;
    if parsed.packet_type != PACKET_PUBLISH {
        return Err(invalid(
            "MQTT 帧不是 PUBLISH",
            Some(serde_json::json!({ "packetType": parsed.packet_type })),
        ));
    }
    let qos = (parsed.flags >> 1) & 0x03;
    if qos == 3 {
        return Err(invalid("MQTT PUBLISH QoS=3 保留值", None));
    }
    let topic_len = usize::from(read_u16(&parsed.payload, 0)?);
    let topic_start = 2usize;
    let topic_end = topic_start.saturating_add(topic_len);
    let topic_bytes = parsed
        .payload
        .get(topic_start..topic_end)
        .ok_or_else(|| invalid("MQTT PUBLISH topic 不完整", None))?;
    let topic = str::from_utf8(topic_bytes)
        .map_err(|_| invalid("MQTT PUBLISH topic 不是 UTF-8", None))?
        .to_string();
    if topic.is_empty() || topic.contains('\0') {
        return Err(invalid("MQTT PUBLISH topic 无效", None));
    }
    let mut cursor = topic_end;
    let packet_id = if qos > 0 {
        let id = read_u16(&parsed.payload, cursor)?;
        if id == 0 {
            return Err(invalid("MQTT PUBLISH packetId 不能为 0", None));
        }
        cursor += 2;
        Some(id)
    } else {
        None
    };
    Ok(Publish {
        dup: parsed.flags & 0x08 != 0,
        qos,
        retain: parsed.flags & 0x01 != 0,
        topic,
        packet_id,
        payload: parsed.payload[cursor..].to_vec(),
    })
}

pub fn build_pingreq() -> Vec<u8> {
    vec![PACKET_PINGREQ << 4, 0]
}

pub fn parse_pingresp(frame: &[u8]) -> Result<(), CoreError> {
    let parsed = parse_frame(frame)?;
    if parsed.packet_type != PACKET_PINGRESP || parsed.flags != 0 || !parsed.payload.is_empty() {
        return Err(invalid("MQTT PINGRESP 帧无效", None));
    }
    Ok(())
}

pub fn build_disconnect() -> Vec<u8> {
    vec![PACKET_DISCONNECT << 4, 0]
}

pub fn frame_hex(frame: &[u8]) -> String {
    frame
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<Vec<_>>()
        .join(" ")
}

pub fn return_code_message(code: u8) -> &'static str {
    match code {
        0 => "Success",
        1 => "Unacceptable protocol version",
        2 => "Identifier rejected",
        3 => "Server unavailable",
        4 => "Bad user name or password",
        5 => "Not authorized",
        0x80 => "Subscription failure",
        _ => "Unknown MQTT return code",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mqtt_connect_subscribe_and_publish_vectors_round_trip() {
        let connect = build_connect("nexus-test", 30, true).expect("connect");
        let connect_frame = parse_frame(&connect).expect("connect frame");
        assert_eq!(connect_frame.packet_type, PACKET_CONNECT);
        assert_eq!(connect_frame.payload[0..6], [0, 4, b'M', b'Q', b'T', b'T']);

        let subscribe = build_subscribe(7, "factory/line1/#", 0).expect("subscribe");
        assert_eq!(
            subscribe,
            vec![
                0x82, 0x14, 0, 7, 0, 15, b'f', b'a', b'c', b't', b'o', b'r', b'y', b'/', b'l',
                b'i', b'n', b'e', b'1', b'/', b'#', 0
            ]
        );

        let publish = vec![
            0x30, 0x0D, 0, 5, b's', b't', b'a', b't', b'e', 0x4F, 0x4B, 0x21, 0x00, 0x01, 0x02,
        ];
        let parsed = parse_publish(&publish).expect("publish");
        assert_eq!(parsed.topic, "state");
        assert_eq!(parsed.payload, vec![0x4F, 0x4B, 0x21, 0x00, 0x01, 0x02]);
    }

    #[test]
    fn mqtt_rejects_malformed_remaining_length_and_packet_flags() {
        assert!(decode_remaining_length(&[0x80, 0x80, 0x80, 0x80, 0x01], 0).is_err());
        assert!(parse_connack(&[0x20, 0x02, 0x02, 0x00]).is_err());
        assert!(build_subscribe(0, "state", 0).is_err());
    }
}
