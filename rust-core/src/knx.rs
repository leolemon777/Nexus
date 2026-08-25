//! KNXnet/IP Tunneling v1 offline read-only codec.
//!
//! This first Rust boundary covers only unsecured KNXnet/IP over UDP/IPv4:
//! Connect Request/Response, a GroupValueRead Tunneling Request, Tunneling ACK,
//! and an inbound GroupValueResponse tunnel request plus its required ACK.
//!
//! The historical Nexus.Knx implementation was audited and rewritten in C# as
//! a bounded non-secure Tunneling v1 subset. That audit found the older public
//! implementation had malformed `10 00` headers, no connection/channel/sequence
//! handling, and confused Tunneling ACK with GroupValueResponse. This Rust port
//! starts from the audited frame semantics and does not expose GroupValueWrite,
//! scene control, Routing, Discovery, Device Management, KNX IP Secure, or an
//! online UDP session.

use serde::Serialize;

use crate::error::CoreError;

pub const HEADER_LENGTH: u8 = 0x06;
pub const PROTOCOL_VERSION: u8 = 0x10;
pub const CONNECT_REQUEST: u16 = 0x0205;
pub const CONNECT_RESPONSE: u16 = 0x0206;
pub const CONNECTION_STATE_REQUEST: u16 = 0x0207;
pub const CONNECTION_STATE_RESPONSE: u16 = 0x0208;
pub const DISCONNECT_REQUEST: u16 = 0x0209;
pub const DISCONNECT_RESPONSE: u16 = 0x020A;
pub const TUNNELING_REQUEST: u16 = 0x0420;
pub const TUNNELING_ACK: u16 = 0x0421;
pub const HPAI_LENGTH: usize = 8;
pub const CRI_LENGTH: usize = 4;
pub const DEFAULT_PORT: u16 = 3671;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct GroupAddress {
    pub main: u8,
    pub middle: u8,
    pub sub: u8,
    pub value: u16,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectRequest {
    pub local_ip: [u8; 4],
    pub local_port: u16,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectResponse {
    pub channel_id: u8,
    pub status: u8,
    pub data_endpoint: [u8; 4],
    pub data_port: u16,
    pub individual_address: u16,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TunnelingRequest {
    pub channel_id: u8,
    pub sequence: u8,
    pub message_code: u8,
    pub message_code_name: &'static str,
    pub source: u16,
    pub destination: GroupAddress,
    pub service: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub normalized_small_value: Option<u8>,
    pub payload: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TunnelingAck {
    pub channel_id: u8,
    pub sequence: u8,
    pub status: u8,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DisconnectResponse {
    pub channel_id: u8,
    pub status: u8,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectionStateResponse {
    pub channel_id: u8,
    pub status: u8,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectionStateResult {
    pub request_frame: Vec<u8>,
    pub response: ConnectionStateResponse,
    pub attempts: u8,
    pub transport: &'static str,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectResult {
    pub request_frame: Vec<u8>,
    pub response: ConnectResponse,
    pub transport: &'static str,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GroupReadResult {
    pub request_frame: Vec<u8>,
    pub acknowledgement_frame: Vec<u8>,
    pub response_frame: Vec<u8>,
    pub response_ack_frame: Vec<u8>,
    pub address: GroupAddress,
    pub source: u16,
    pub payload: Vec<u8>,
    #[serde(skip_serializing_if = "Option::is_some")]
    pub normalized_small_value: Option<u8>,
    pub sequence: u8,
    pub attempts: u8,
    pub read_only: bool,
    pub transport: &'static str,
}

fn knx_error(
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

pub fn parse_group_address(text: &str) -> Result<GroupAddress, CoreError> {
    let parts: Vec<&str> = text.trim().split('/').collect();
    if parts.len() != 3 {
        return Err(knx_error(
            "KNX_GROUP_ADDRESS_INVALID",
            "KNX 三层组地址格式必须是 main/middle/sub，例如 1/2/3",
            Some(serde_json::json!({ "address": text.trim() })),
        ));
    }
    let parse = |value: &str, max: u16| -> Result<u16, CoreError> {
        let parsed = value.parse::<u16>().map_err(|_| {
            knx_error(
                "KNX_GROUP_ADDRESS_INVALID",
                "KNX 组地址各段必须是十进制整数",
                Some(serde_json::json!({ "segment": value })),
            )
        })?;
        if parsed > max {
            return Err(knx_error(
                "KNX_GROUP_ADDRESS_RANGE_INVALID",
                "KNX 组地址段超出 main 0..31 / middle 0..7 / sub 0..255 范围",
                Some(serde_json::json!({ "segment": value, "max": max })),
            ));
        }
        Ok(parsed)
    };
    let main = parse(parts[0], 31)? as u8;
    let middle = parse(parts[1], 7)? as u8;
    let sub = parse(parts[2], 255)? as u8;
    let value = (u16::from(main) << 11) | (u16::from(middle) << 8) | u16::from(sub);
    Ok(GroupAddress {
        main,
        middle,
        sub,
        value,
    })
}

pub fn group_address_from_value(value: u16) -> GroupAddress {
    GroupAddress {
        main: ((value >> 11) & 0x1F) as u8,
        middle: ((value >> 8) & 0x07) as u8,
        sub: (value & 0xFF) as u8,
        value,
    }
}

pub fn format_group_address(address: GroupAddress) -> String {
    format!("{}/{}/{}", address.main, address.middle, address.sub)
}

pub fn frame_hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<Vec<_>>()
        .join(" ")
}

fn parse_ipv4(text: &str) -> Result<[u8; 4], CoreError> {
    let segments: Vec<&str> = text.trim().split('.').collect();
    if segments.len() != 4 {
        return Err(knx_error(
            "KNX_IPV4_INVALID",
            "KNX HPAI 只支持 IPv4 点分十进制地址",
            Some(serde_json::json!({ "address": text.trim() })),
        ));
    }
    let mut result = [0u8; 4];
    for (index, segment) in segments.iter().enumerate() {
        result[index] = segment.parse::<u8>().map_err(|_| {
            knx_error(
                "KNX_IPV4_INVALID",
                "KNX IPv4 地址段必须是 0..255",
                Some(serde_json::json!({ "segment": segment })),
            )
        })?;
    }
    Ok(result)
}

fn validate_port(port: u16) -> Result<(), CoreError> {
    if port == 0 {
        return Err(knx_error(
            "KNX_PORT_INVALID",
            "KNX UDP 端口必须大于 0",
            None,
        ));
    }
    Ok(())
}

fn header(service: u16, total_length: usize) -> Result<Vec<u8>, CoreError> {
    let length = u16::try_from(total_length)
        .map_err(|_| knx_error("KNX_FRAME_TOO_LONG", "KNXnet/IP 帧长度溢出", None))?;
    Ok(vec![
        HEADER_LENGTH,
        PROTOCOL_VERSION,
        (service >> 8) as u8,
        (service & 0xFF) as u8,
        (length >> 8) as u8,
        (length & 0xFF) as u8,
    ])
}

fn parse_header(bytes: &[u8], expected_service: u16) -> Result<(), CoreError> {
    if bytes.len() < 6 {
        return Err(knx_error(
            "KNX_HEADER_TRUNCATED",
            "KNXnet/IP 公共头必须至少 6 字节",
            Some(serde_json::json!({ "actual": bytes.len() })),
        ));
    }
    if bytes[0] != HEADER_LENGTH || bytes[1] != PROTOCOL_VERSION {
        return Err(knx_error(
            "KNX_HEADER_INVALID",
            "KNXnet/IP 公共头必须是 06 10；旧 10 00 头已判定错误",
            Some(serde_json::json!({
                "headerLength": bytes[0],
                "version": bytes[1]
            })),
        ));
    }
    let service = u16::from_be_bytes([bytes[2], bytes[3]]);
    if service != expected_service {
        return Err(knx_error(
            "KNX_SERVICE_MISMATCH",
            "KNXnet/IP 帧服务类型与当前解析边界不符",
            Some(serde_json::json!({
                "expected": format!("{expected_service:04X}"),
                "actual": format!("{service:04X}")
            })),
        ));
    }
    let declared = u16::from_be_bytes([bytes[4], bytes[5]]);
    if usize::from(declared) != bytes.len() {
        return Err(knx_error(
            "KNX_LENGTH_MISMATCH",
            "KNXnet/IP 总长度字段与实际 UDP 载荷不符",
            Some(serde_json::json!({
                "expected": declared,
                "actual": bytes.len()
            })),
        ));
    }
    Ok(())
}

fn push_hpai(bytes: &mut Vec<u8>, ip: [u8; 4], port: u16) {
    bytes.push(HPAI_LENGTH as u8);
    bytes.push(0x01); // UDP/IPv4
    bytes.extend_from_slice(&ip);
    bytes.extend_from_slice(&port.to_be_bytes());
}

pub fn build_connect_request(local_ip: &str, local_port: u16) -> Result<Vec<u8>, CoreError> {
    let ip = parse_ipv4(local_ip)?;
    validate_port(local_port)?;
    let mut frame = header(CONNECT_REQUEST, 26)?;
    push_hpai(&mut frame, ip, local_port);
    push_hpai(&mut frame, ip, local_port);
    frame.extend_from_slice(&[0x04, 0x04, 0x02, 0x00]);
    Ok(frame)
}

pub fn parse_connect_response(bytes: &[u8]) -> Result<ConnectResponse, CoreError> {
    parse_header(bytes, CONNECT_RESPONSE)?;
    if bytes.len() != 20 {
        return Err(knx_error(
            "KNX_CONNECT_RESPONSE_LENGTH_INVALID",
            "KNXnet/IP Connect Response 必须恰好 20 字节",
            Some(serde_json::json!({ "expected": 20, "actual": bytes.len() })),
        ));
    }
    if bytes[8] != HPAI_LENGTH as u8 || bytes[9] != 0x01 {
        return Err(knx_error(
            "KNX_HPAI_INVALID",
            "KNX Connect Response 数据端点必须是 UDP/IPv4 HPAI",
            Some(serde_json::json!({
                "length": bytes[8],
                "protocolCode": bytes[9]
            })),
        ));
    }
    if bytes[16] != CRI_LENGTH as u8 || bytes[17] != 0x04 {
        return Err(knx_error(
            "KNX_CRD_INVALID",
            "KNX Connect Response CRD 长度/类型必须是 04 04",
            Some(serde_json::json!({
                "length": bytes[16],
                "type": bytes[17]
            })),
        ));
    }
    Ok(ConnectResponse {
        channel_id: bytes[6],
        status: bytes[7],
        data_endpoint: [bytes[10], bytes[11], bytes[12], bytes[13]],
        data_port: u16::from_be_bytes([bytes[14], bytes[15]]),
        individual_address: u16::from_be_bytes([bytes[18], bytes[19]]),
    })
}

fn build_cemi_group_read(address: GroupAddress) -> Vec<u8> {
    let mut cemi = Vec::with_capacity(11);
    cemi.extend_from_slice(&[
        0x11, // L_Data.req
        0x00, // no additional information
        0xBC, // standard frame, repeat, system broadcast, low priority
        0xE0, // group destination, hop count 6
        0x00, // source assigned by gateway
        0x00,
    ]);
    cemi.extend_from_slice(&address.value.to_be_bytes());
    cemi.push(0x01); // APDU length - 1 = 1
    cemi.extend_from_slice(&[0x00, 0x00]); // GroupValueRead
    cemi
}

pub fn build_group_read_request(
    channel_id: u8,
    sequence: u8,
    address: GroupAddress,
) -> Result<Vec<u8>, CoreError> {
    let cemi = build_cemi_group_read(address);
    let total_length = 10 + cemi.len();
    let mut frame = header(TUNNELING_REQUEST, total_length)?;
    frame.extend_from_slice(&[0x04, channel_id, sequence, 0x00]);
    frame.extend_from_slice(&cemi);
    Ok(frame)
}

pub fn build_tunneling_ack(channel_id: u8, sequence: u8, status: u8) -> Result<Vec<u8>, CoreError> {
    let mut frame = header(TUNNELING_ACK, 10)?;
    frame.extend_from_slice(&[0x04, channel_id, sequence, status]);
    Ok(frame)
}

pub fn build_disconnect_request(
    channel_id: u8,
    local_ip: [u8; 4],
    local_port: u16,
) -> Result<Vec<u8>, CoreError> {
    validate_port(local_port)?;
    let mut frame = header(DISCONNECT_REQUEST, 16)?;
    frame.push(channel_id);
    frame.push(0x00);
    push_hpai(&mut frame, local_ip, local_port);
    Ok(frame)
}

pub fn build_connection_state_request(
    channel_id: u8,
    local_ip: [u8; 4],
    local_port: u16,
) -> Result<Vec<u8>, CoreError> {
    validate_port(local_port)?;
    let mut frame = header(CONNECTION_STATE_REQUEST, 16)?;
    frame.push(channel_id);
    frame.push(0x00);
    push_hpai(&mut frame, local_ip, local_port);
    Ok(frame)
}

fn message_code_name(code: u8) -> Option<&'static str> {
    match code {
        0x11 => Some("L_Data.req"),
        0x29 => Some("L_Data.ind"),
        0x2E => Some("L_Data.con"),
        _ => None,
    }
}

pub fn parse_tunneling_request(bytes: &[u8]) -> Result<TunnelingRequest, CoreError> {
    parse_header(bytes, TUNNELING_REQUEST)?;
    if bytes.len() < 10 || bytes[6] != 0x04 {
        return Err(knx_error(
            "KNX_TUNNELING_HEADER_INVALID",
            "KNX Tunneling Request 连接头必须从 structure-length 04 开始",
            Some(serde_json::json!({
                "actualLength": bytes.len(),
                "structureLength": bytes.get(6).copied()
            })),
        ));
    }
    let channel_id = bytes[7];
    let sequence = bytes[8];
    if bytes[9] != 0 {
        return Err(knx_error(
            "KNX_TUNNELING_REQUEST_STATUS_INVALID",
            "KNXnet/IP Tunneling Request 的 reserved 字节必须为 00",
            Some(serde_json::json!({ "actual": bytes[9] })),
        ));
    }
    let cemi = bytes.get(10..).ok_or_else(|| {
        knx_error(
            "KNX_CEMI_TRUNCATED",
            "KNX Tunneling Request 缺少 cEMI",
            None,
        )
    })?;
    if cemi.len() < 10 {
        return Err(knx_error(
            "KNX_CEMI_TRUNCATED",
            "KNX cEMI 至少需要 10 字节",
            Some(serde_json::json!({ "actual": cemi.len() })),
        ));
    }
    let message_code = cemi[0];
    let code_name = message_code_name(message_code).ok_or_else(|| {
        knx_error(
            "KNX_CEMI_MESSAGE_CODE_UNSUPPORTED",
            "本轮只接受 L_Data.req 11H、L_Data.ind 29H 或 L_Data.con 2EH",
            Some(serde_json::json!({ "actual": message_code })),
        )
    })?;
    let additional_length = usize::from(cemi[1]);
    let control = 2 + additional_length;
    let apdu = control + 7;
    if cemi.len() < apdu + 2 {
        return Err(knx_error(
            "KNX_CEMI_TRUNCATED",
            "KNX cEMI 控制字段或 APDU 长度不足",
            Some(serde_json::json!({
                "additionalLength": additional_length,
                "required": apdu + 2,
                "actual": cemi.len()
            })),
        ));
    }
    let source = u16::from_be_bytes([cemi[control + 2], cemi[control + 3]]);
    let destination_value = u16::from_be_bytes([cemi[control + 4], cemi[control + 5]]);
    let declared_apdu_length = usize::from(cemi[control + 6]) + 1;
    let actual_apdu_length = cemi.len() - apdu;
    if declared_apdu_length != actual_apdu_length || declared_apdu_length < 2 {
        return Err(knx_error(
            "KNX_APDU_LENGTH_INVALID",
            "KNX cEMI APDU 长度字段与实际载荷不符",
            Some(serde_json::json!({
                "declared": declared_apdu_length,
                "actual": actual_apdu_length
            })),
        ));
    }
    let apci = ((cemi[apdu] & 0x03) << 2) | ((cemi[apdu + 1] >> 6) & 0x03);
    let service = match apci {
        0 => "GroupValueRead",
        1 => "GroupValueResponse",
        2 => "GroupValueWrite",
        _ => {
            return Err(knx_error(
                "KNX_APCI_UNSUPPORTED",
                "本轮 cEMI 只识别 GroupValueRead/Response/Write APCI",
                Some(serde_json::json!({ "apci": apci })),
            ));
        }
    };
    let mut normalized_small_value = None;
    let payload = if declared_apdu_length == 2 {
        normalized_small_value = Some(cemi[apdu + 1] & 0x3F);
        Vec::new()
    } else {
        cemi[apdu + 2..].to_vec()
    };
    Ok(TunnelingRequest {
        channel_id,
        sequence,
        message_code,
        message_code_name: code_name,
        source,
        destination: group_address_from_value(destination_value),
        service,
        normalized_small_value,
        payload,
    })
}

pub fn parse_group_value_response(bytes: &[u8]) -> Result<TunnelingRequest, CoreError> {
    let request = parse_tunneling_request(bytes)?;
    if request.message_code != 0x29 || request.service != "GroupValueResponse" {
        return Err(knx_error(
            "KNX_GROUP_VALUE_RESPONSE_INVALID",
            "GroupValueResponse 必须是 L_Data.ind 29H + APCI Response",
            Some(serde_json::json!({
                "messageCode": request.message_code,
                "service": request.service
            })),
        ));
    }
    Ok(request)
}

pub fn parse_tunneling_ack(bytes: &[u8]) -> Result<TunnelingAck, CoreError> {
    parse_header(bytes, TUNNELING_ACK)?;
    if bytes.len() != 10 || bytes[6] != 0x04 {
        return Err(knx_error(
            "KNX_TUNNELING_ACK_INVALID",
            "KNX Tunneling ACK 必须恰好 10 字节且 structure-length 为 04",
            Some(serde_json::json!({
                "actualLength": bytes.len(),
                "structureLength": bytes.get(6).copied()
            })),
        ));
    }
    Ok(TunnelingAck {
        channel_id: bytes[7],
        sequence: bytes[8],
        status: bytes[9],
    })
}

pub fn parse_disconnect_response(bytes: &[u8]) -> Result<DisconnectResponse, CoreError> {
    parse_header(bytes, DISCONNECT_RESPONSE)?;
    if bytes.len() != 8 {
        return Err(knx_error(
            "KNX_DISCONNECT_RESPONSE_LENGTH_INVALID",
            "KNX Disconnect Response 必须恰好 8 字节",
            Some(serde_json::json!({ "expected": 8, "actual": bytes.len() })),
        ));
    }
    Ok(DisconnectResponse {
        channel_id: bytes[6],
        status: bytes[7],
    })
}

pub fn parse_connection_state_response(bytes: &[u8]) -> Result<ConnectionStateResponse, CoreError> {
    parse_header(bytes, CONNECTION_STATE_RESPONSE)?;
    if bytes.len() != 8 {
        return Err(knx_error(
            "KNX_CONNECTION_STATE_RESPONSE_LENGTH_INVALID",
            "KNX Connection State Response 必须恰好 8 字节",
            Some(serde_json::json!({ "expected": 8, "actual": bytes.len() })),
        ));
    }
    Ok(ConnectionStateResponse {
        channel_id: bytes[6],
        status: bytes[7],
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn group_address_uses_three_level_bounds_and_wire_value() {
        assert_eq!(
            parse_group_address("1/2/3").unwrap(),
            GroupAddress {
                main: 1,
                middle: 2,
                sub: 3,
                value: 0x0A03
            }
        );
        assert_eq!(parse_group_address("31/7/255").unwrap().value, 0xFFFF);
        assert_eq!(
            format_group_address(group_address_from_value(0x0A03)),
            "1/2/3"
        );
        assert!(parse_group_address("32/0/0").is_err());
        assert!(parse_group_address("0/8/0").is_err());
        assert!(parse_group_address("0/0/256").is_err());
        assert!(parse_group_address("1/2").is_err());
    }

    #[test]
    fn builds_exact_connect_and_group_read_vectors() {
        let connect = build_connect_request("127.0.0.1", 50000).unwrap();
        assert_eq!(
            connect,
            [
                0x06, 0x10, 0x02, 0x05, 0x00, 0x1A, 0x08, 0x01, 0x7F, 0x00, 0x00, 0x01, 0xC3, 0x50,
                0x08, 0x01, 0x7F, 0x00, 0x00, 0x01, 0xC3, 0x50, 0x04, 0x04, 0x02, 0x00,
            ]
        );

        let read =
            build_group_read_request(0x15, 0, parse_group_address("1/2/3").unwrap()).unwrap();
        assert_eq!(
            read,
            [
                0x06, 0x10, 0x04, 0x20, 0x00, 0x15, 0x04, 0x15, 0x00, 0x00, 0x11, 0x00, 0xBC, 0xE0,
                0x00, 0x00, 0x0A, 0x03, 0x01, 0x00, 0x00,
            ]
        );
    }

    #[test]
    fn parses_connect_response_and_tunneling_ack() {
        let response = [
            0x06, 0x10, 0x02, 0x06, 0x00, 0x14, 0x15, 0x00, 0x08, 0x01, 0x7F, 0x00, 0x00, 0x01,
            0xC3, 0x50, 0x04, 0x04, 0x11, 0x01,
        ];
        let parsed = parse_connect_response(&response).unwrap();
        assert_eq!(parsed.channel_id, 0x15);
        assert_eq!(parsed.status, 0);
        assert_eq!(parsed.data_endpoint, [127, 0, 0, 1]);
        assert_eq!(parsed.data_port, 50000);
        assert_eq!(parsed.individual_address, 0x1101);

        let ack = build_tunneling_ack(0x15, 0, 0).unwrap();
        assert_eq!(
            ack,
            [0x06, 0x10, 0x04, 0x21, 0x00, 0x0A, 0x04, 0x15, 0x00, 0x00]
        );
        let parsed_ack = parse_tunneling_ack(&ack).unwrap();
        assert_eq!(parsed_ack.sequence, 0);
    }

    #[test]
    fn parses_extended_group_response_and_builds_matching_ack() {
        let mut response = vec![
            0x06, 0x10, 0x04, 0x20, 0x00, 0x17, 0x04, 0x15, 0x00, 0x00, 0x29, 0x00, 0xBC, 0xE0,
            0x11, 0x01, 0x0A, 0x03, 0x03, 0x00, 0x40,
        ];
        response.extend_from_slice(&[0x12, 0x34]);
        let parsed = parse_group_value_response(&response).unwrap();
        assert_eq!(parsed.channel_id, 0x15);
        assert_eq!(parsed.sequence, 0);
        assert_eq!(parsed.message_code_name, "L_Data.ind");
        assert_eq!(parsed.source, 0x1101);
        assert_eq!(format_group_address(parsed.destination), "1/2/3");
        assert_eq!(parsed.service, "GroupValueResponse");
        assert_eq!(parsed.payload, [0x12, 0x34]);
        assert_eq!(
            build_tunneling_ack(parsed.channel_id, parsed.sequence, 0).unwrap(),
            [0x06, 0x10, 0x04, 0x21, 0x00, 0x0A, 0x04, 0x15, 0x00, 0x00]
        );
    }

    #[test]
    fn rejects_old_header_length_and_bad_lengths() {
        let mut old = build_connect_request("127.0.0.1", 50000).unwrap();
        old[0] = 0x10;
        old[1] = 0x00;
        let error = parse_connect_response(&old).unwrap_err();
        assert_eq!(error_code(&error), "KNX_HEADER_INVALID");

        let mut bad_length =
            build_group_read_request(1, 0, parse_group_address("1/2/3").unwrap()).unwrap();
        bad_length[5] += 1;
        let error = parse_tunneling_request(&bad_length).unwrap_err();
        assert_eq!(error_code(&error), "KNX_LENGTH_MISMATCH");

        let bad_ack = [0x06, 0x10, 0x04, 0x21, 0x00, 0x0B, 0x04, 0x15, 0x00, 0x00];
        let error = parse_tunneling_ack(&bad_ack).unwrap_err();
        assert_eq!(error_code(&error), "KNX_LENGTH_MISMATCH");
    }

    fn error_code(error: &CoreError) -> &'static str {
        match error {
            CoreError::Modbus { code, .. } => code,
            _ => "UNEXPECTED_ERROR",
        }
    }
}
