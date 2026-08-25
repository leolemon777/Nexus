//! BACnet/IP Who-Is and I-Am offline codec.
//!
//! This first boundary intentionally handles only local BACnet/IP frames:
//! BVLC `Original-Unicast-NPDU` (0x0A) or `Original-Broadcast-NPDU`
//! (0x0B), a non-routed NPDU (`01 00` plus priority bits), and an
//! Unconfirmed-Request-PDU for Who-Is (service 08H) or I-Am (service 00H).
//!
//! No UDP socket, ReadProperty, WriteProperty, segmentation, COV, routing,
//! BBMD/Foreign Device, BACnet/SC, or MS/TP path is exposed. The historical
//! Nexus.Bacnet implementation was removed because its BVLC function, LVT
//! lengths, I-Am object tag, and transaction semantics were non-standard;
//! none of that implementation is reused here.

use serde::Serialize;

use crate::error::CoreError;

pub const BVLL_TYPE_BACNET_IP: u8 = 0x81;
pub const BVLC_ORIGINAL_UNICAST_NPDU: u8 = 0x0A;
pub const BVLC_ORIGINAL_BROADCAST_NPDU: u8 = 0x0B;
pub const NPDU_PROTOCOL_VERSION: u8 = 0x01;
pub const PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST: u8 = 0x10;
pub const PDU_TYPE_CONFIRMED_SERVICE_REQUEST: u8 = 0x00;
pub const PDU_TYPE_COMPLEX_ACK: u8 = 0x30;
pub const SERVICE_UNCONFIRMED_I_AM: u8 = 0x00;
pub const SERVICE_UNCONFIRMED_WHO_IS: u8 = 0x08;
pub const SERVICE_CONFIRMED_READ_PROPERTY: u8 = 0x0C;
pub const MAX_INSTANCE: u32 = 0x003F_FFFF;
pub const OBJECT_DEVICE: u16 = 8;
pub const APPLICATION_TAG_UNSIGNED: u8 = 2;
pub const APPLICATION_TAG_OBJECT_ID: u8 = 12;
pub const APPLICATION_TAG_ENUMERATED: u8 = 9;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum Segmentation {
    #[serde(rename = "both")]
    Both,
    #[serde(rename = "transmit")]
    Transmit,
    #[serde(rename = "receive")]
    Receive,
    #[serde(rename = "none")]
    None,
}

impl Segmentation {
    pub const fn code(self) -> u8 {
        match self {
            Self::Both => 0,
            Self::Transmit => 1,
            Self::Receive => 2,
            Self::None => 3,
        }
    }

    pub const fn label(self) -> &'static str {
        match self {
            Self::Both => "segmented-both",
            Self::Transmit => "segmented-transmit",
            Self::Receive => "segmented-receive",
            Self::None => "no-segmentation",
        }
    }

    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            0 => Some(Self::Both),
            1 => Some(Self::Transmit),
            2 => Some(Self::Receive),
            3 => Some(Self::None),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Npdu {
    pub protocol_version: u8,
    pub control: u8,
    pub network_layer_message: bool,
    pub destination_present: bool,
    pub source_present: bool,
    pub data_expecting_reply: bool,
    pub priority: &'static str,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WhoIsRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub low_limit: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub high_limit: Option<u32>,
    pub global: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct IAmRequest {
    pub object_type: u16,
    pub device_instance: u32,
    pub max_apdu: u32,
    pub segmentation: Segmentation,
    pub segmentation_code: u8,
    pub vendor_id: u16,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ParsedFrame {
    pub bvlc_type: u8,
    pub bvlc_function: u8,
    pub bvlc_function_name: &'static str,
    pub bvlc_length: u16,
    pub npdu: Npdu,
    pub apdu_type: u8,
    pub service_choice: u8,
    pub service_name: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub who_is: Option<WhoIsRequest>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub i_am: Option<IAmRequest>,
    pub read_only: bool,
    pub transport: &'static str,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReadPropertyRequest {
    pub object_type: u16,
    pub object_instance: u32,
    pub property_identifier: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub property_array_index: Option<u32>,
    pub invoke_id: u8,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PropertyValue {
    pub application_tag: u8,
    pub kind: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unsigned: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub real: Option<f32>,
    pub raw: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReadPropertyAck {
    pub object_type: u16,
    pub object_instance: u32,
    pub property_identifier: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub property_array_index: Option<u32>,
    pub invoke_id: u8,
    pub value: PropertyValue,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WhoisResult {
    pub request_frame: Vec<u8>,
    pub response_frames: Vec<Vec<u8>>,
    pub responses: Vec<ParsedFrame>,
    pub response_count: usize,
    pub read_only: bool,
    pub transport: &'static str,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReadPropertyResult {
    pub request: ReadPropertyRequest,
    pub request_frame: Vec<u8>,
    pub response_frame: Vec<u8>,
    pub ack: ReadPropertyAck,
    pub read_only: bool,
    pub transport: &'static str,
}

fn bacnet_error(
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

pub fn frame_hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<Vec<_>>()
        .join(" ")
}

fn unsigned_wire_length(value: u32) -> usize {
    let length = if value <= u8::MAX as u32 {
        1
    } else if value <= u16::MAX as u32 {
        2
    } else if value <= 0x00FF_FFFF {
        3
    } else {
        4
    };
    length
}

fn push_unsigned(bytes: &mut Vec<u8>, value: u32) {
    let length = unsigned_wire_length(value);
    bytes.extend_from_slice(&value.to_be_bytes()[4 - length..]);
}

fn application_tag_byte(tag_number: u8, content_length: usize) -> Result<u8, CoreError> {
    let lvt = u8::try_from(content_length)
        .map_err(|_| bacnet_error("BACNET_TAG_LVT_INVALID", "BACnet 应用标签长度溢出", None))?;
    if lvt > 4 {
        return Err(bacnet_error(
            "BACNET_TAG_LVT_UNSUPPORTED",
            "本轮 BACnet 只支持 1..4 字节短格式应用标签",
            Some(serde_json::json!({ "lvt": lvt })),
        ));
    }
    Ok((tag_number << 4) | lvt)
}

fn context_tag_byte(tag_number: u8, content_length: usize) -> Result<u8, CoreError> {
    let lvt = u8::try_from(content_length)
        .map_err(|_| bacnet_error("BACNET_TAG_LVT_INVALID", "BACnet 上下文标签长度溢出", None))?;
    if lvt > 4 {
        return Err(bacnet_error(
            "BACNET_TAG_LVT_UNSUPPORTED",
            "本轮 BACnet 只支持 1..4 字节短格式上下文标签",
            Some(serde_json::json!({ "lvt": lvt })),
        ));
    }
    Ok(0x08 | (tag_number << 4) | lvt)
}

fn tag_parts(byte: u8) -> (u8, bool, u8) {
    (byte >> 4, byte & 0x08 != 0, byte & 0x07)
}

fn read_unsigned_content(bytes: &[u8], expected_length: usize) -> Result<u32, CoreError> {
    if bytes.len() != expected_length || expected_length == 0 || expected_length > 4 {
        return Err(bacnet_error(
            "BACNET_VALUE_LENGTH_INVALID",
            "BACnet Unsigned/Enumerated 内容长度必须是 1..4 字节",
            Some(serde_json::json!({
                "expected": expected_length,
                "actual": bytes.len()
            })),
        ));
    }
    let mut raw = [0u8; 4];
    raw[4 - bytes.len()..].copy_from_slice(bytes);
    Ok(u32::from_be_bytes(raw))
}

fn parse_application_unsigned(
    bytes: &[u8],
    expected_tag: u8,
    field: &'static str,
) -> Result<(u32, usize), CoreError> {
    let first = *bytes.first().ok_or_else(|| {
        bacnet_error(
            "BACNET_TAG_TRUNCATED",
            format!("BACnet {field} 缺少标签"),
            None,
        )
    })?;
    let (tag, context_specific, lvt) = tag_parts(first);
    if tag != expected_tag || context_specific || lvt == 0 || lvt > 4 {
        return Err(bacnet_error(
            "BACNET_APPLICATION_TAG_INVALID",
            format!("BACnet {field} 不是合法短格式应用标签"),
            Some(serde_json::json!({
                "expectedTag": expected_tag,
                "actualTag": tag,
                "contextSpecific": context_specific,
                "lvt": lvt
            })),
        ));
    }
    let content_length = usize::from(lvt);
    let content = bytes.get(1..1 + content_length).ok_or_else(|| {
        bacnet_error(
            "BACNET_TAG_TRUNCATED",
            format!("BACnet {field} 内容长度不足"),
            None,
        )
    })?;
    Ok((
        read_unsigned_content(content, content_length)?,
        1 + content_length,
    ))
}

fn parse_context_unsigned(
    bytes: &[u8],
    expected_tag: u8,
    field: &'static str,
) -> Result<(u32, usize), CoreError> {
    let first = *bytes.first().ok_or_else(|| {
        bacnet_error(
            "BACNET_TAG_TRUNCATED",
            format!("BACnet {field} 缺少标签"),
            None,
        )
    })?;
    let (tag, context_specific, lvt) = tag_parts(first);
    if tag != expected_tag || !context_specific || lvt == 0 || lvt > 4 {
        return Err(bacnet_error(
            "BACNET_CONTEXT_TAG_INVALID",
            format!("BACnet {field} 不是合法短格式上下文标签"),
            Some(serde_json::json!({
                "expectedTag": expected_tag,
                "actualTag": tag,
                "contextSpecific": context_specific,
                "lvt": lvt
            })),
        ));
    }
    let content_length = usize::from(lvt);
    let content = bytes.get(1..1 + content_length).ok_or_else(|| {
        bacnet_error(
            "BACNET_TAG_TRUNCATED",
            format!("BACnet {field} 内容长度不足"),
            None,
        )
    })?;
    Ok((
        read_unsigned_content(content, content_length)?,
        1 + content_length,
    ))
}

fn object_identifier(device_instance: u32) -> Result<[u8; 4], CoreError> {
    if device_instance > MAX_INSTANCE {
        return Err(bacnet_error(
            "BACNET_INSTANCE_INVALID",
            "BACnet Device Instance 必须在 0..4194303 范围内",
            Some(serde_json::json!({ "instance": device_instance })),
        ));
    }
    let value = (u32::from(OBJECT_DEVICE) << 22) | device_instance;
    Ok(value.to_be_bytes())
}

fn parse_object_identifier(bytes: &[u8]) -> Result<(u16, u32, usize), CoreError> {
    let first = *bytes
        .first()
        .ok_or_else(|| bacnet_error("BACNET_TAG_TRUNCATED", "BACnet I-Am 缺少对象标识", None))?;
    let (tag, context_specific, lvt) = tag_parts(first);
    if tag != APPLICATION_TAG_OBJECT_ID || context_specific || lvt != 4 {
        return Err(bacnet_error(
            "BACNET_OBJECT_ID_TAG_INVALID",
            "BACnet I-Am 第一个字段必须是 Object Identifier 应用标签且 LVT=4",
            Some(
                serde_json::json!({ "tag": tag, "lvt": lvt, "contextSpecific": context_specific }),
            ),
        ));
    }
    let content = bytes.get(1..5).ok_or_else(|| {
        bacnet_error("BACNET_TAG_TRUNCATED", "BACnet I-Am 对象标识内容不足", None)
    })?;
    let raw = u32::from_be_bytes(content.try_into().expect("four-byte object id"));
    let object_type = u16::try_from(raw >> 22).expect("10-bit object type");
    let instance = raw & MAX_INSTANCE;
    Ok((object_type, instance, 5))
}

fn parse_context_object_identifier(
    bytes: &[u8],
    expected_tag: u8,
    field: &'static str,
) -> Result<(u16, u32, usize), CoreError> {
    let first = *bytes.first().ok_or_else(|| {
        bacnet_error(
            "BACNET_TAG_TRUNCATED",
            format!("BACnet {field} 缺少标签"),
            None,
        )
    })?;
    let (tag, context_specific, lvt) = tag_parts(first);
    if tag != expected_tag || !context_specific || lvt != 4 {
        return Err(bacnet_error(
            "BACNET_OBJECT_ID_CONTEXT_TAG_INVALID",
            format!("BACnet {field} 必须是 context Object Identifier 且 LVT=4"),
            Some(serde_json::json!({
                "expectedTag": expected_tag,
                "actualTag": tag,
                "contextSpecific": context_specific,
                "lvt": lvt
            })),
        ));
    }
    let content = bytes.get(1..5).ok_or_else(|| {
        bacnet_error(
            "BACNET_TAG_TRUNCATED",
            format!("BACnet {field} 内容不足"),
            None,
        )
    })?;
    let raw = u32::from_be_bytes(content.try_into().expect("four-byte object id"));
    Ok((
        u16::try_from(raw >> 22).map_err(|_| {
            bacnet_error(
                "BACNET_OBJECT_TYPE_INVALID",
                "BACnet Object Type 溢出",
                None,
            )
        })?,
        raw & MAX_INSTANCE,
        5,
    ))
}

fn local_npdu_control(priority: u8) -> u8 {
    priority & 0x03
}

fn npdu(control: u8) -> Npdu {
    let priority = match control & 0x03 {
        0 => "normal",
        1 => "urgent",
        2 => "criticalEquipment",
        _ => "lifeSafety",
    };
    Npdu {
        protocol_version: NPDU_PROTOCOL_VERSION,
        control,
        network_layer_message: control & 0x80 != 0,
        destination_present: control & 0x20 != 0,
        source_present: control & 0x08 != 0,
        data_expecting_reply: control & 0x04 != 0,
        priority,
    }
}

fn build_frame(function: u8, apdu: &[u8], priority: u8) -> Result<Vec<u8>, CoreError> {
    if function != BVLC_ORIGINAL_UNICAST_NPDU && function != BVLC_ORIGINAL_BROADCAST_NPDU {
        return Err(bacnet_error(
            "BACNET_BVLC_FUNCTION_INVALID",
            "本轮 BACnet/IP 只能生成 0A Original-Unicast 或 0B Original-Broadcast",
            Some(serde_json::json!({ "function": function })),
        ));
    }
    let npdu = [NPDU_PROTOCOL_VERSION, local_npdu_control(priority)];
    let total_length = u16::try_from(4 + npdu.len() + apdu.len())
        .map_err(|_| bacnet_error("BACNET_FRAME_TOO_LONG", "BACnet/IP BVLC 长度溢出", None))?;
    let mut frame = Vec::with_capacity(usize::from(total_length));
    frame.extend_from_slice(&[
        BVLL_TYPE_BACNET_IP,
        function,
        u8::try_from(total_length >> 8).expect("high length byte"),
        u8::try_from(total_length & 0xFF).expect("low length byte"),
    ]);
    frame.extend_from_slice(&npdu);
    frame.extend_from_slice(apdu);
    Ok(frame)
}

fn build_local_frame_with_control(
    function: u8,
    apdu: &[u8],
    control: u8,
) -> Result<Vec<u8>, CoreError> {
    if function != BVLC_ORIGINAL_UNICAST_NPDU && function != BVLC_ORIGINAL_BROADCAST_NPDU {
        return Err(bacnet_error(
            "BACNET_BVLC_FUNCTION_INVALID",
            "本轮 BACnet/IP 只能生成 0A Original-Unicast 或 0B Original-Broadcast",
            Some(serde_json::json!({ "function": function })),
        ));
    }
    let npdu = [NPDU_PROTOCOL_VERSION, control];
    let total_length = u16::try_from(4 + npdu.len() + apdu.len())
        .map_err(|_| bacnet_error("BACNET_FRAME_TOO_LONG", "BACnet/IP BVLC 长度溢出", None))?;
    let mut frame = Vec::with_capacity(usize::from(total_length));
    frame.extend_from_slice(&[
        BVLL_TYPE_BACNET_IP,
        function,
        u8::try_from(total_length >> 8).expect("high length byte"),
        u8::try_from(total_length & 0xFF).expect("low length byte"),
    ]);
    frame.extend_from_slice(&npdu);
    frame.extend_from_slice(apdu);
    Ok(frame)
}

fn validate_instance(value: u32, field: &'static str) -> Result<(), CoreError> {
    if value > MAX_INSTANCE {
        return Err(bacnet_error(
            "BACNET_INSTANCE_INVALID",
            format!("BACnet {field} 必须在 0..4194303 范围内"),
            Some(serde_json::json!({ field: value })),
        ));
    }
    Ok(())
}

pub fn build_whois_request(
    low_limit: Option<u32>,
    high_limit: Option<u32>,
    broadcast: bool,
) -> Result<Vec<u8>, CoreError> {
    match (low_limit, high_limit) {
        (None, None) => {}
        (Some(low), Some(high)) => {
            validate_instance(low, "Device Instance Low Limit")?;
            validate_instance(high, "Device Instance High Limit")?;
            if low > high {
                return Err(bacnet_error(
                    "BACNET_WHOIS_RANGE_INVALID",
                    "BACnet Who-Is 低范围不能大于高范围",
                    Some(serde_json::json!({ "low": low, "high": high })),
                ));
            }
        }
        _ => {
            return Err(bacnet_error(
                "BACNET_WHOIS_LIMIT_PAIR_INVALID",
                "BACnet Who-Is 上下范围必须成对出现或同时省略",
                Some(serde_json::json!({
                    "lowPresent": low_limit.is_some(),
                    "highPresent": high_limit.is_some()
                })),
            ));
        }
    }

    let mut apdu = vec![
        PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST,
        SERVICE_UNCONFIRMED_WHO_IS,
    ];
    if let (Some(low), Some(high)) = (low_limit, high_limit) {
        let low_length = unsigned_wire_length(low);
        let high_length = unsigned_wire_length(high);
        apdu.push(context_tag_byte(0, low_length)?);
        push_unsigned(&mut apdu, low);
        apdu.push(context_tag_byte(1, high_length)?);
        push_unsigned(&mut apdu, high);
    }
    build_frame(
        if broadcast {
            BVLC_ORIGINAL_BROADCAST_NPDU
        } else {
            BVLC_ORIGINAL_UNICAST_NPDU
        },
        &apdu,
        0,
    )
}

pub fn build_iam_request(
    device_instance: u32,
    max_apdu: u32,
    segmentation: Segmentation,
    vendor_id: u16,
    broadcast: bool,
) -> Result<Vec<u8>, CoreError> {
    validate_instance(device_instance, "Device Instance")?;
    if max_apdu == 0 {
        return Err(bacnet_error(
            "BACNET_MAX_APDU_INVALID",
            "BACnet I-Am Max-APDU 不能为 0",
            Some(serde_json::json!({ "maxApdu": max_apdu })),
        ));
    }

    let mut apdu = vec![
        PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST,
        SERVICE_UNCONFIRMED_I_AM,
    ];
    let object_id = object_identifier(device_instance)?;
    apdu.push(application_tag_byte(
        APPLICATION_TAG_OBJECT_ID,
        object_id.len(),
    )?);
    apdu.extend_from_slice(&object_id);

    let max_apdu_length = unsigned_wire_length(max_apdu);
    apdu.push(application_tag_byte(
        APPLICATION_TAG_UNSIGNED,
        max_apdu_length,
    )?);
    push_unsigned(&mut apdu, max_apdu);

    let segmentation_code = segmentation.code();
    apdu.push(application_tag_byte(
        APPLICATION_TAG_ENUMERATED,
        unsigned_wire_length(u32::from(segmentation_code)),
    )?);
    apdu.push(segmentation_code);

    apdu.push(application_tag_byte(
        APPLICATION_TAG_UNSIGNED,
        unsigned_wire_length(u32::from(vendor_id)),
    )?);
    push_unsigned(&mut apdu, u32::from(vendor_id));

    build_frame(
        if broadcast {
            BVLC_ORIGINAL_BROADCAST_NPDU
        } else {
            BVLC_ORIGINAL_UNICAST_NPDU
        },
        &apdu,
        0,
    )
}

pub fn build_read_property_request(
    object_type: u16,
    object_instance: u32,
    property_identifier: u32,
    property_array_index: Option<u32>,
    invoke_id: u8,
) -> Result<Vec<u8>, CoreError> {
    if object_type > 0x03FF {
        return Err(bacnet_error(
            "BACNET_OBJECT_TYPE_INVALID",
            "BACnet Object Type 必须在 0..1023 范围内",
            Some(serde_json::json!({ "objectType": object_type })),
        ));
    }
    validate_instance(object_instance, "Object Instance")?;
    if property_identifier > MAX_INSTANCE {
        return Err(bacnet_error(
            "BACNET_PROPERTY_INVALID",
            "BACnet Property Identifier 必须在 0..4194303 范围内",
            Some(serde_json::json!({ "property": property_identifier })),
        ));
    }
    if let Some(index) = property_array_index {
        if index > MAX_INSTANCE {
            return Err(bacnet_error(
                "BACNET_ARRAY_INDEX_INVALID",
                "BACnet Property Array Index 必须在 0..4194303 范围内",
                Some(serde_json::json!({ "index": index })),
            ));
        }
    }

    let mut apdu = vec![
        PDU_TYPE_CONFIRMED_SERVICE_REQUEST,
        0x03, // unsegmented sender declaration, Max-APDU 480
        invoke_id,
        SERVICE_CONFIRMED_READ_PROPERTY,
    ];
    let object_id = ((u32::from(object_type) << 22) | object_instance).to_be_bytes();
    apdu.push(context_tag_byte(0, object_id.len())?);
    apdu.extend_from_slice(&object_id);

    let property_wire_length = unsigned_wire_length(property_identifier);
    apdu.push(context_tag_byte(1, property_wire_length)?);
    push_unsigned(&mut apdu, property_identifier);
    if let Some(index) = property_array_index {
        let index_wire_length = unsigned_wire_length(index);
        apdu.push(context_tag_byte(2, index_wire_length)?);
        push_unsigned(&mut apdu, index);
    }

    // A confirmed request expects a reply, so DER=1 while keeping local routing absent.
    build_local_frame_with_control(BVLC_ORIGINAL_UNICAST_NPDU, &apdu, 0x04)
}

fn parse_whois_service(service: &[u8]) -> Result<Option<WhoIsRequest>, CoreError> {
    if service.is_empty() {
        return Ok(Some(WhoIsRequest {
            low_limit: None,
            high_limit: None,
            global: true,
        }));
    }
    let (low, low_size) = parse_context_unsigned(service, 0, "Who-Is low limit")?;
    validate_instance(low, "Device Instance Low Limit")?;
    let (high, high_size) = parse_context_unsigned(&service[low_size..], 1, "Who-Is high limit")?;
    validate_instance(high, "Device Instance High Limit")?;
    if low > high {
        return Err(bacnet_error(
            "BACNET_WHOIS_RANGE_INVALID",
            "BACnet Who-Is 低范围不能大于高范围",
            Some(serde_json::json!({ "low": low, "high": high })),
        ));
    }
    if low_size + high_size != service.len() {
        return Err(bacnet_error(
            "BACNET_SERVICE_TRAILING_DATA",
            "BACnet Who-Is 范围后存在尾随数据",
            Some(serde_json::json!({
                "expected": low_size + high_size,
                "actual": service.len()
            })),
        ));
    }
    Ok(Some(WhoIsRequest {
        low_limit: Some(low),
        high_limit: Some(high),
        global: false,
    }))
}

fn parse_iam_service(service: &[u8]) -> Result<Option<IAmRequest>, CoreError> {
    let (object_type, device_instance, first_size) = parse_object_identifier(service)?;
    if object_type != OBJECT_DEVICE {
        return Err(bacnet_error(
            "BACNET_OBJECT_TYPE_INVALID",
            "BACnet I-Am 对象类型必须是 Device (8)",
            Some(serde_json::json!({ "actual": object_type })),
        ));
    }
    let (max_apdu, second_size) =
        parse_application_unsigned(&service[first_size..], APPLICATION_TAG_UNSIGNED, "Max-APDU")?;
    if max_apdu == 0 {
        return Err(bacnet_error(
            "BACNET_MAX_APDU_INVALID",
            "BACnet I-Am Max-APDU 不能为 0",
            None,
        ));
    }
    let (segmentation_code, third_size) = parse_application_unsigned(
        &service[first_size + second_size..],
        APPLICATION_TAG_ENUMERATED,
        "Segmentation",
    )?;
    let segmentation = Segmentation::from_code(u8::try_from(segmentation_code).unwrap_or(u8::MAX))
        .ok_or_else(|| {
            bacnet_error(
                "BACNET_SEGMENTATION_INVALID",
                "BACnet Segmentation 只能是 0..3",
                Some(serde_json::json!({ "actual": segmentation_code })),
            )
        })?;
    let (vendor_value, fourth_size) = parse_application_unsigned(
        &service[first_size + second_size + third_size..],
        APPLICATION_TAG_UNSIGNED,
        "Vendor ID",
    )?;
    let vendor_id = u16::try_from(vendor_value).map_err(|_| {
        bacnet_error(
            "BACNET_VENDOR_ID_INVALID",
            "BACnet Vendor ID 必须在 0..65535 范围内",
            Some(serde_json::json!({ "actual": vendor_value })),
        )
    })?;
    let consumed = first_size + second_size + third_size + fourth_size;
    if consumed != service.len() {
        return Err(bacnet_error(
            "BACNET_SERVICE_TRAILING_DATA",
            "BACnet I-Am 四个字段后存在尾随数据",
            Some(serde_json::json!({ "expected": consumed, "actual": service.len() })),
        ));
    }
    Ok(Some(IAmRequest {
        object_type,
        device_instance,
        max_apdu,
        segmentation,
        segmentation_code: segmentation.code(),
        vendor_id,
    }))
}

pub fn parse_frame(bytes: &[u8]) -> Result<ParsedFrame, CoreError> {
    if bytes.len() < 4 {
        return Err(bacnet_error(
            "BACNET_BVLC_TRUNCATED",
            "BACnet/IP BVLC 头必须至少 4 字节",
            Some(serde_json::json!({ "actual": bytes.len() })),
        ));
    }
    if bytes[0] != BVLL_TYPE_BACNET_IP {
        return Err(bacnet_error(
            "BACNET_BVLC_TYPE_INVALID",
            "BACnet/IPv4 BVLC Type 必须是 81H",
            Some(serde_json::json!({ "actual": bytes[0] })),
        ));
    }
    let function = bytes[1];
    if function != BVLC_ORIGINAL_UNICAST_NPDU && function != BVLC_ORIGINAL_BROADCAST_NPDU {
        return Err(bacnet_error(
            "BACNET_BVLC_FUNCTION_UNSUPPORTED",
            "本轮 BACnet/IP 离线解析只接受 0A Original-Unicast 或 0B Original-Broadcast",
            Some(serde_json::json!({ "actual": function })),
        ));
    }
    let bvlc_length = u16::from_be_bytes([bytes[2], bytes[3]]);
    if usize::from(bvlc_length) != bytes.len() {
        return Err(bacnet_error(
            "BACNET_BVLC_LENGTH_MISMATCH",
            "BACnet/IP BVLC Length 与实际 UDP 载荷长度不符",
            Some(serde_json::json!({
                "expected": bvlc_length,
                "actual": bytes.len()
            })),
        ));
    }
    if bytes.len() < 6 {
        return Err(bacnet_error(
            "BACNET_NPDU_TRUNCATED",
            "BACnet 本地 NPDU 至少需要 2 字节",
            Some(serde_json::json!({ "actual": bytes.len() - 4 })),
        ));
    }
    let npdu_start = 4;
    if bytes[npdu_start] != NPDU_PROTOCOL_VERSION {
        return Err(bacnet_error(
            "BACNET_NPDU_VERSION_INVALID",
            "本轮 BACnet NPDU Protocol Version 只支持 01H",
            Some(serde_json::json!({ "actual": bytes[npdu_start] })),
        ));
    }
    let control = bytes[npdu_start + 1];
    if control & 0x80 != 0
        || control & 0x40 != 0
        || control & 0x20 != 0
        || control & 0x10 != 0
        || control & 0x08 != 0
        || control & 0x04 != 0
    {
        return Err(bacnet_error(
            "BACNET_NPDU_CONTROL_UNSUPPORTED",
            "本轮只接受本地非路由、非网络消息、不期待回复的 NPDU；DNET/SNET/DER 必须不存在",
            Some(serde_json::json!({ "control": control })),
        ));
    }
    let apdu_start = npdu_start + 2;
    if bytes.len() <= apdu_start {
        return Err(bacnet_error(
            "BACNET_APDU_TRUNCATED",
            "BACnet 帧 NPDU 后缺少 APDU",
            None,
        ));
    }
    if bytes[apdu_start] != PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST {
        return Err(bacnet_error(
            "BACNET_APDU_TYPE_UNSUPPORTED",
            "本轮 BACnet 只接受 10H Unconfirmed-Request-PDU",
            Some(serde_json::json!({ "actual": bytes[apdu_start] })),
        ));
    }
    let service_choice = bytes[apdu_start + 1];
    let service = &bytes[apdu_start + 2..];
    let (service_name, who_is, i_am) = match service_choice {
        SERVICE_UNCONFIRMED_WHO_IS => {
            let request = parse_whois_service(service)?;
            ("who-is", request, None)
        }
        SERVICE_UNCONFIRMED_I_AM => {
            let request = parse_iam_service(service)?;
            ("i-am", None, request)
        }
        other => {
            return Err(bacnet_error(
                "BACNET_SERVICE_UNSUPPORTED",
                "本轮 BACnet 只解析 Who-Is 或 I-Am，不支持其他服务",
                Some(serde_json::json!({ "serviceChoice": other })),
            ));
        }
    };
    Ok(ParsedFrame {
        bvlc_type: bytes[0],
        bvlc_function: function,
        bvlc_function_name: if function == BVLC_ORIGINAL_BROADCAST_NPDU {
            "Original-Broadcast-NPDU"
        } else {
            "Original-Unicast-NPDU"
        },
        bvlc_length,
        npdu: npdu(control),
        apdu_type: bytes[apdu_start],
        service_choice,
        service_name,
        who_is,
        i_am,
        read_only: true,
        transport: "udp-offline",
    })
}

fn split_local_bacnet_ip_frame(
    bytes: &[u8],
    expected_control: u8,
) -> Result<(u8, &[u8]), CoreError> {
    if bytes.len() < 4 {
        return Err(bacnet_error(
            "BACNET_BVLC_TRUNCATED",
            "BACnet/IP BVLC 头必须至少 4 字节",
            Some(serde_json::json!({ "actual": bytes.len() })),
        ));
    }
    if bytes[0] != BVLL_TYPE_BACNET_IP || bytes[1] != BVLC_ORIGINAL_UNICAST_NPDU {
        return Err(bacnet_error(
            "BACNET_BVLC_FUNCTION_UNSUPPORTED",
            "ReadProperty 离线边界只接受 0A Original-Unicast-NPDU",
            Some(serde_json::json!({ "type": bytes[0], "function": bytes[1] })),
        ));
    }
    let length = u16::from_be_bytes([bytes[2], bytes[3]]);
    if usize::from(length) != bytes.len() {
        return Err(bacnet_error(
            "BACNET_BVLC_LENGTH_MISMATCH",
            "BACnet/IP BVLC Length 与实际 UDP 载荷长度不符",
            Some(serde_json::json!({ "expected": length, "actual": bytes.len() })),
        ));
    }
    if bytes.len() < 6 {
        return Err(bacnet_error(
            "BACNET_NPDU_TRUNCATED",
            "BACnet 本地 NPDU 至少需要 2 字节",
            None,
        ));
    }
    if bytes[4] != NPDU_PROTOCOL_VERSION {
        return Err(bacnet_error(
            "BACNET_NPDU_VERSION_INVALID",
            "本轮 BACnet NPDU Protocol Version 只支持 01H",
            Some(serde_json::json!({ "actual": bytes[4] })),
        ));
    }
    if bytes[5] != expected_control {
        return Err(bacnet_error(
            "BACNET_NPDU_CONTROL_UNSUPPORTED",
            "ReadProperty 离线边界只接受指定本地控制字节",
            Some(serde_json::json!({ "expected": expected_control, "actual": bytes[5] })),
        ));
    }
    Ok((bytes[5], &bytes[6..]))
}

pub fn parse_read_property_request(bytes: &[u8]) -> Result<ReadPropertyRequest, CoreError> {
    let (_, apdu) = split_local_bacnet_ip_frame(bytes, 0x04)?;
    if apdu.len() < 4 || apdu[0] != PDU_TYPE_CONFIRMED_SERVICE_REQUEST {
        return Err(bacnet_error(
            "BACNET_APDU_TYPE_UNSUPPORTED",
            "ReadProperty 请求必须是 00H Confirmed-Request-PDU",
            Some(serde_json::json!({ "actual": apdu.first().copied() })),
        ));
    }
    if apdu[1] != 0x03 {
        return Err(bacnet_error(
            "BACNET_MAX_APDU_DECLARATION_UNSUPPORTED",
            "本轮 ReadProperty 请求只支持 03H：不分段、Max-APDU 480",
            Some(serde_json::json!({ "actual": apdu[1] })),
        ));
    }
    let invoke_id = apdu[2];
    if apdu[3] != SERVICE_CONFIRMED_READ_PROPERTY {
        return Err(bacnet_error(
            "BACNET_SERVICE_UNSUPPORTED",
            "APDU service choice 必须是 0CH ReadProperty",
            Some(serde_json::json!({ "actual": apdu[3] })),
        ));
    }
    let service = &apdu[4..];
    let (object_type, object_instance, first_size) =
        parse_context_object_identifier(service, 0, "ReadProperty object identifier")?;
    let (property_identifier, second_size) = parse_context_unsigned(
        &service[first_size..],
        1,
        "ReadProperty property identifier",
    )?;
    let mut offset = first_size + second_size;
    let mut property_array_index = None;
    if let Some(byte) = service.get(offset) {
        let (tag, context_specific, _) = tag_parts(*byte);
        if tag == 2 && context_specific {
            let (index, size) =
                parse_context_unsigned(&service[offset..], 2, "ReadProperty array index")?;
            property_array_index = Some(index);
            offset += size;
        }
    }
    if offset != service.len() {
        return Err(bacnet_error(
            "BACNET_SERVICE_TRAILING_DATA",
            "ReadProperty 请求参数后存在尾随数据",
            Some(serde_json::json!({ "expected": offset, "actual": service.len() })),
        ));
    }
    validate_instance(object_instance, "Object Instance")?;
    if property_identifier > MAX_INSTANCE {
        return Err(bacnet_error(
            "BACNET_PROPERTY_INVALID",
            "BACnet Property Identifier 必须在 0..4194303 范围内",
            Some(serde_json::json!({ "property": property_identifier })),
        ));
    }
    if property_array_index.is_some_and(|index| index > MAX_INSTANCE) {
        return Err(bacnet_error(
            "BACNET_ARRAY_INDEX_INVALID",
            "BACnet Property Array Index 必须在 0..4194303 范围内",
            None,
        ));
    }
    Ok(ReadPropertyRequest {
        object_type,
        object_instance,
        property_identifier,
        property_array_index,
        invoke_id,
    })
}

fn parse_property_value(bytes: &[u8]) -> Result<PropertyValue, CoreError> {
    let first = *bytes.first().ok_or_else(|| {
        bacnet_error(
            "BACNET_PROPERTY_VALUE_EMPTY",
            "本轮 ReadProperty-ACK 只支持 [3] 内恰好一个短格式应用值",
            None,
        )
    })?;
    let (tag, context_specific, lvt) = tag_parts(first);
    if context_specific || lvt == 0 || lvt > 4 {
        return Err(bacnet_error(
            "BACNET_APPLICATION_TAG_UNSUPPORTED",
            "本轮只支持 [3] 内一个 1..4 字节短格式应用标签",
            Some(serde_json::json!({
                "tag": tag,
                "contextSpecific": context_specific,
                "lvt": lvt
            })),
        ));
    }
    let content_length = usize::from(lvt);
    let content = bytes
        .get(1..1 + content_length)
        .ok_or_else(|| bacnet_error("BACNET_TAG_TRUNCATED", "BACnet 属性值内容长度不足", None))?;
    if 1 + content_length != bytes.len() {
        return Err(bacnet_error(
            "BACNET_PROPERTY_VALUE_MULTIPLE",
            "本轮 ReadProperty-ACK 的 [3] 内只能有一个应用值",
            Some(serde_json::json!({ "expected": 1 + content_length, "actual": bytes.len() })),
        ));
    }
    let mut value = PropertyValue {
        application_tag: tag,
        kind: "raw",
        unsigned: None,
        real: None,
        raw: bytes.to_vec(),
    };
    match tag {
        APPLICATION_TAG_UNSIGNED => {
            value.kind = "unsigned";
            value.unsigned = Some(read_unsigned_content(content, content_length)?);
        }
        4 => {
            if content_length != 4 {
                return Err(bacnet_error(
                    "BACNET_REAL_LENGTH_INVALID",
                    "BACnet Real 应用值必须恰好 4 字节",
                    Some(serde_json::json!({ "actual": content_length })),
                ));
            }
            let real = f32::from_be_bytes(content.try_into().expect("four-byte real"));
            if !real.is_finite() {
                return Err(bacnet_error(
                    "BACNET_REAL_UNSUPPORTED",
                    "本轮 JSONL 边界不接受 NaN/Infinity Real；设备语义需另行确认",
                    None,
                ));
            }
            value.kind = "real";
            value.real = Some(real);
        }
        _ => {}
    }
    Ok(value)
}

pub fn parse_read_property_ack(
    bytes: &[u8],
    expected_request: Option<&ReadPropertyRequest>,
) -> Result<ReadPropertyAck, CoreError> {
    let (_, apdu) = split_local_bacnet_ip_frame(bytes, 0x00)?;
    if apdu.len() < 3 || apdu[0] != PDU_TYPE_COMPLEX_ACK {
        return Err(bacnet_error(
            "BACNET_APDU_TYPE_UNSUPPORTED",
            "ReadProperty 响应必须是 30H ComplexACK-PDU",
            Some(serde_json::json!({ "actual": apdu.first().copied() })),
        ));
    }
    let invoke_id = apdu[1];
    if apdu[2] != SERVICE_CONFIRMED_READ_PROPERTY {
        return Err(bacnet_error(
            "BACNET_SERVICE_UNSUPPORTED",
            "ComplexACK service choice 必须是 0CH ReadProperty",
            Some(serde_json::json!({ "actual": apdu[2] })),
        ));
    }
    let service = &apdu[3..];
    let (object_type, object_instance, first_size) =
        parse_context_object_identifier(service, 0, "ReadProperty-ACK object identifier")?;
    let (property_identifier, second_size) = parse_context_unsigned(
        &service[first_size..],
        1,
        "ReadProperty-ACK property identifier",
    )?;
    let mut offset = first_size + second_size;
    let mut property_array_index = None;
    if let Some(byte) = service.get(offset) {
        let (tag, context_specific, _) = tag_parts(*byte);
        if tag == 2 && context_specific {
            let (index, size) =
                parse_context_unsigned(&service[offset..], 2, "ReadProperty-ACK array index")?;
            property_array_index = Some(index);
            offset += size;
        }
    }
    if service.get(offset) != Some(&0x3E) {
        return Err(bacnet_error(
            "BACNET_OPENING_TAG_INVALID",
            "ReadProperty-ACK 必须包含 context [3] opening tag 3EH",
            Some(serde_json::json!({ "actual": service.get(offset).copied() })),
        ));
    }
    offset += 1;
    let closing_position = service[offset..]
        .iter()
        .position(|byte| *byte == 0x3F)
        .ok_or_else(|| {
            bacnet_error(
                "BACNET_CLOSING_TAG_MISSING",
                "ReadProperty-ACK 缺少 context [3] closing tag 3FH",
                None,
            )
        })?
        + offset;
    let value = parse_property_value(&service[offset..closing_position])?;
    offset = closing_position + 1;
    if offset != service.len() {
        return Err(bacnet_error(
            "BACNET_SERVICE_TRAILING_DATA",
            "ReadProperty-ACK closing tag 后存在尾随数据",
            Some(serde_json::json!({ "expected": offset, "actual": service.len() })),
        ));
    }
    let ack = ReadPropertyAck {
        object_type,
        object_instance,
        property_identifier,
        property_array_index,
        invoke_id,
        value,
    };
    if let Some(expected) = expected_request {
        if ack.invoke_id != expected.invoke_id
            || ack.object_type != expected.object_type
            || ack.object_instance != expected.object_instance
            || ack.property_identifier != expected.property_identifier
            || ack.property_array_index != expected.property_array_index
        {
            return Err(bacnet_error(
                "BACNET_READ_PROPERTY_MISMATCH",
                "ReadProperty-ACK 的 Invoke ID / 对象 / 属性 / 数组索引与请求不一致",
                Some(serde_json::json!({
                    "request": expected,
                    "actual": ack
                })),
            ));
        }
    }
    Ok(ack)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_exact_global_whois_vector() {
        let frame = build_whois_request(None, None, true).unwrap();
        assert_eq!(frame, [0x81, 0x0B, 0x00, 0x08, 0x01, 0x00, 0x10, 0x08]);
        let unicast = build_whois_request(None, None, false).unwrap();
        assert_eq!(&unicast[..2], &[0x81, 0x0A]);
    }

    #[test]
    fn builds_exact_ranged_whois_and_iam_vectors() {
        let whois = build_whois_request(Some(1000), Some(2000), true).unwrap();
        assert_eq!(
            whois,
            [
                0x81, 0x0B, 0x00, 0x0E, 0x01, 0x00, 0x10, 0x08, 0x0A, 0x03, 0xE8, 0x1A, 0x07, 0xD0
            ]
        );

        let i_am = build_iam_request(1001, 480, Segmentation::None, 42, true).unwrap();
        assert_eq!(
            i_am,
            [
                0x81, 0x0B, 0x00, 0x14, 0x01, 0x00, 0x10, 0x00, 0xC4, 0x02, 0x00, 0x03, 0xE9, 0x22,
                0x01, 0xE0, 0x91, 0x03, 0x21, 0x2A
            ]
        );
    }

    #[test]
    fn parses_both_vectors_and_reports_layers() {
        let parsed = parse_frame(&[0x81, 0x0B, 0x00, 0x08, 0x01, 0x00, 0x10, 0x08]).unwrap();
        assert_eq!(parsed.bvlc_function, 0x0B);
        assert_eq!(parsed.service_name, "who-is");
        assert_eq!(parsed.who_is.unwrap().global, true);

        let parsed = parse_frame(&[
            0x81, 0x0B, 0x00, 0x0E, 0x01, 0x00, 0x10, 0x08, 0x0A, 0x03, 0xE8, 0x1A, 0x07, 0xD0,
        ])
        .unwrap();
        let request = parsed.who_is.unwrap();
        assert_eq!(request.low_limit, Some(1000));
        assert_eq!(request.high_limit, Some(2000));
        assert_eq!(request.global, false);

        let parsed = parse_frame(&[
            0x81, 0x0A, 0x00, 0x14, 0x01, 0x00, 0x10, 0x00, 0xC4, 0x02, 0x00, 0x03, 0xE9, 0x22,
            0x01, 0xE0, 0x91, 0x03, 0x21, 0x2A,
        ])
        .unwrap();
        let request = parsed.i_am.unwrap();
        assert_eq!(request.object_type, OBJECT_DEVICE);
        assert_eq!(request.device_instance, 1001);
        assert_eq!(request.max_apdu, 480);
        assert_eq!(request.segmentation, Segmentation::None);
        assert_eq!(request.vendor_id, 42);
    }

    #[test]
    fn rejects_old_nexus_and_out_of_boundary_frames() {
        let old_bvlc = parse_frame(&[0x81, 0x00, 0x00, 0x08, 0x01, 0x00, 0x10, 0x08]).unwrap_err();
        assert_eq!(error_code(&old_bvlc), "BACNET_BVLC_FUNCTION_UNSUPPORTED");

        let bad_length =
            parse_frame(&[0x81, 0x0B, 0x00, 0x09, 0x01, 0x00, 0x10, 0x08]).unwrap_err();
        assert_eq!(error_code(&bad_length), "BACNET_BVLC_LENGTH_MISMATCH");

        let routed = parse_frame(&[0x81, 0x0B, 0x00, 0x08, 0x01, 0x20, 0x10, 0x08]).unwrap_err();
        assert_eq!(error_code(&routed), "BACNET_NPDU_CONTROL_UNSUPPORTED");

        let other_service =
            parse_frame(&[0x81, 0x0B, 0x00, 0x08, 0x01, 0x00, 0x10, 0x0C]).unwrap_err();
        assert_eq!(error_code(&other_service), "BACNET_SERVICE_UNSUPPORTED");

        let partial_limits = build_whois_request(Some(1), None, true).unwrap_err();
        assert_eq!(
            error_code(&partial_limits),
            "BACNET_WHOIS_LIMIT_PAIR_INVALID"
        );
        let reversed = build_whois_request(Some(2), Some(1), true).unwrap_err();
        assert_eq!(error_code(&reversed), "BACNET_WHOIS_RANGE_INVALID");
        let bad_instance =
            build_iam_request(MAX_INSTANCE + 1, 480, Segmentation::None, 1, true).unwrap_err();
        assert_eq!(error_code(&bad_instance), "BACNET_INSTANCE_INVALID");
    }

    #[test]
    fn rejects_wrong_iam_object_tag_and_trailing_service_data() {
        // Replace the object identifier tag 64H with an unsigned tag 22H.
        let mut bad_tag = build_iam_request(1001, 480, Segmentation::None, 42, true).unwrap();
        bad_tag[8] = 0x22;
        let error = parse_frame(&bad_tag).unwrap_err();
        assert_eq!(error_code(&error), "BACNET_OBJECT_ID_TAG_INVALID");

        let mut trailing = build_whois_request(Some(1000), Some(2000), true).unwrap();
        trailing.push(0x00);
        let updated_length = u16::try_from(trailing.len()).unwrap();
        trailing[2..4].copy_from_slice(&updated_length.to_be_bytes());
        let error = parse_frame(&trailing).unwrap_err();
        assert_eq!(error_code(&error), "BACNET_SERVICE_TRAILING_DATA");
    }

    #[test]
    fn builds_exact_read_property_request_with_and_without_array_index() {
        let request = build_read_property_request(0, 1001, 85, None, 1).unwrap();
        assert_eq!(
            request,
            [
                0x81, 0x0A, 0x00, 0x11, 0x01, 0x04, 0x00, 0x03, 0x01, 0x0C, 0x0C, 0x00, 0x00, 0x03,
                0xE9, 0x19, 0x55
            ]
        );
        let parsed = parse_read_property_request(&request).unwrap();
        assert_eq!(parsed.object_type, 0);
        assert_eq!(parsed.object_instance, 1001);
        assert_eq!(parsed.property_identifier, 85);
        assert_eq!(parsed.property_array_index, None);
        assert_eq!(parsed.invoke_id, 1);

        let indexed = build_read_property_request(8, 1001, 76, Some(2), 9).unwrap();
        let parsed = parse_read_property_request(&indexed).unwrap();
        assert_eq!(parsed.object_type, OBJECT_DEVICE);
        assert_eq!(parsed.property_identifier, 76);
        assert_eq!(parsed.property_array_index, Some(2));
        assert_eq!(parsed.invoke_id, 9);
    }

    #[test]
    fn parses_read_property_ack_real_and_rejects_mismatch() {
        let request = build_read_property_request(0, 1001, 85, None, 1).unwrap();
        let response = [
            0x81, 0x0A, 0x00, 0x17, 0x01, 0x00, 0x30, 0x01, 0x0C, 0x0C, 0x00, 0x00, 0x03, 0xE9,
            0x19, 0x55, 0x3E, 0x44, 0x42, 0xF6, 0xE6, 0x66, 0x3F,
        ];
        let parsed = parse_read_property_ack(&response, None).unwrap();
        assert_eq!(parsed.invoke_id, 1);
        assert_eq!(parsed.object_type, 0);
        assert_eq!(parsed.object_instance, 1001);
        assert_eq!(parsed.property_identifier, 85);
        assert_eq!(parsed.value.kind, "real");
        assert_eq!(parsed.value.real, Some(123.45));
        let expected = parse_read_property_request(&request).unwrap();
        assert!(parse_read_property_ack(&response, Some(&expected)).is_ok());

        let wrong_invoke = parse_read_property_ack(
            &{
                let mut frame = response.to_vec();
                frame[7] = 0x02;
                frame
            },
            Some(&expected),
        )
        .unwrap_err();
        assert_eq!(error_code(&wrong_invoke), "BACNET_READ_PROPERTY_MISMATCH");
    }

    #[test]
    fn rejects_read_property_bad_envelope_and_value_boundaries() {
        let broadcast = build_read_property_request(0, 1001, 85, None, 1).unwrap();
        let mut bad_function = broadcast.clone();
        bad_function[1] = 0x0B;
        let error = parse_read_property_request(&bad_function).unwrap_err();
        assert_eq!(error_code(&error), "BACNET_BVLC_FUNCTION_UNSUPPORTED");

        let mut no_der = broadcast.clone();
        no_der[5] = 0x00;
        let error = parse_read_property_request(&no_der).unwrap_err();
        assert_eq!(error_code(&error), "BACNET_NPDU_CONTROL_UNSUPPORTED");

        let mut bad_ack = [
            0x81, 0x0A, 0x00, 0x17, 0x01, 0x00, 0x30, 0x01, 0x0C, 0x0C, 0x00, 0x00, 0x03, 0xE9,
            0x19, 0x55, 0x3E, 0x44, 0x42, 0xF6, 0xE6, 0x66, 0x3F,
        ]
        .to_vec();
        bad_ack[16] = 0x3F;
        let error = parse_read_property_ack(&bad_ack, None).unwrap_err();
        assert_eq!(error_code(&error), "BACNET_OPENING_TAG_INVALID");

        bad_ack[16] = 0x3E;
        bad_ack.push(0x00);
        let old_length = u16::try_from(bad_ack.len()).unwrap();
        bad_ack[2..4].copy_from_slice(&old_length.to_be_bytes());
        let error = parse_read_property_ack(&bad_ack, None).unwrap_err();
        assert_eq!(error_code(&error), "BACNET_SERVICE_TRAILING_DATA");

        let invalid_object = build_read_property_request(1024, 1, 85, None, 1).unwrap_err();
        assert_eq!(error_code(&invalid_object), "BACNET_OBJECT_TYPE_INVALID");
        let invalid_property =
            build_read_property_request(0, 1, MAX_INSTANCE + 1, None, 1).unwrap_err();
        assert_eq!(error_code(&invalid_property), "BACNET_PROPERTY_INVALID");
    }

    fn error_code(error: &CoreError) -> &'static str {
        match error {
            CoreError::Modbus { code, .. } => code,
            _ => "UNEXPECTED_ERROR",
        }
    }
}
