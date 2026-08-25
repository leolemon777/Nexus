//! IEC 60870-5-104 read-only client/master codec.
//!
//! This first boundary intentionally exposes only APCI I/S/U framing, the
//! STARTDT/STOPDT/TESTFR link functions, general/group interrogation and five
//! monitoring-direction ASDUs.  Control commands, setpoints, clock writes and
//! server/outstation mode are not part of this module's public command surface.

use serde::Serialize;

use crate::error::CoreError;

pub const DEFAULT_PORT: u16 = 2404;
pub const START_BYTE: u8 = 0x68;
pub const MAX_APDU_LENGTH_FIELD: usize = 253;
pub const MAX_ASDU_LENGTH: usize = MAX_APDU_LENGTH_FIELD - 4;
pub const MAX_SEQUENCE: u16 = 0x7FFF;
pub const MAX_INTERROGATION_FRAMES: usize = 1024;

pub const TYPE_M_SP_NA_1: u8 = 1;
pub const TYPE_M_DP_NA_1: u8 = 3;
pub const TYPE_M_ME_NA_1: u8 = 9;
pub const TYPE_M_ME_NC_1: u8 = 13;
pub const TYPE_M_IT_NA_1: u8 = 15;
pub const TYPE_C_IC_NA_1: u8 = 100;

pub const COT_PERIODIC: u8 = 1;
pub const COT_BACKGROUND: u8 = 2;
pub const COT_SPONTANEOUS: u8 = 3;
pub const COT_REQUEST: u8 = 5;
pub const COT_ACTIVATION: u8 = 6;
pub const COT_ACTIVATION_CONFIRMATION: u8 = 7;
pub const COT_ACTIVATION_TERMINATION: u8 = 10;
pub const COT_INTERROGATED_BY_STATION: u8 = 20;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum FrameFormat {
    I,
    S,
    U,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum UFunction {
    StartDataTransferActivation,
    StartDataTransferConfirmation,
    StopDataTransferActivation,
    StopDataTransferConfirmation,
    TestFrameActivation,
    TestFrameConfirmation,
}

impl UFunction {
    pub fn control(self) -> u8 {
        match self {
            Self::StartDataTransferActivation => 0x07,
            Self::StartDataTransferConfirmation => 0x0B,
            Self::StopDataTransferActivation => 0x13,
            Self::StopDataTransferConfirmation => 0x23,
            Self::TestFrameActivation => 0x43,
            Self::TestFrameConfirmation => 0x83,
        }
    }

    fn from_control(value: u8) -> Option<Self> {
        match value {
            0x07 => Some(Self::StartDataTransferActivation),
            0x0B => Some(Self::StartDataTransferConfirmation),
            0x13 => Some(Self::StopDataTransferActivation),
            0x23 => Some(Self::StopDataTransferConfirmation),
            0x43 => Some(Self::TestFrameActivation),
            0x83 => Some(Self::TestFrameConfirmation),
            _ => None,
        }
    }

    pub fn name(self) -> &'static str {
        match self {
            Self::StartDataTransferActivation => "STARTDT_ACT",
            Self::StartDataTransferConfirmation => "STARTDT_CON",
            Self::StopDataTransferActivation => "STOPDT_ACT",
            Self::StopDataTransferConfirmation => "STOPDT_CON",
            Self::TestFrameActivation => "TESTFR_ACT",
            Self::TestFrameConfirmation => "TESTFR_CON",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    tag = "format"
)]
pub enum Apdu {
    I {
        send_sequence: u16,
        receive_sequence: u16,
        asdu: Vec<u8>,
    },
    S {
        receive_sequence: u16,
    },
    U {
        function: UFunction,
    },
}

impl Apdu {
    pub fn format(&self) -> FrameFormat {
        match self {
            Self::I { .. } => FrameFormat::I,
            Self::S { .. } => FrameFormat::S,
            Self::U { .. } => FrameFormat::U,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InformationObject {
    pub address: u32,
    pub data: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Asdu {
    pub type_id: u8,
    pub type_name: &'static str,
    pub cause: u8,
    pub cause_name: &'static str,
    pub is_negative: bool,
    pub is_test: bool,
    pub originator_address: u8,
    pub common_address: u16,
    pub is_sequence: bool,
    pub objects: Vec<InformationObject>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QualityDescriptor {
    pub raw: u8,
    pub overflow: bool,
    pub blocked: bool,
    pub substituted: bool,
    pub not_topical: bool,
    pub invalid: bool,
    pub is_good: bool,
    pub labels: Vec<&'static str>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    tag = "kind"
)]
pub enum TelemetryValue {
    SinglePoint {
        value: bool,
    },
    DoublePoint {
        state: u8,
        state_name: &'static str,
    },
    NormalizedMeasured {
        raw: i16,
        value: f64,
    },
    ShortFloatMeasured {
        value: f32,
    },
    IntegratedTotal {
        value: i32,
        sequence_number: u8,
        carry: bool,
        adjusted: bool,
    },
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TelemetryPoint {
    pub common_address: u16,
    pub information_object_address: u32,
    pub type_id: u8,
    pub type_name: &'static str,
    pub cause: u8,
    pub cause_name: &'static str,
    pub originator_address: u8,
    pub quality: QualityDescriptor,
    pub value: TelemetryValue,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InterrogationResult {
    pub group: u8,
    pub qualifier: u8,
    pub request_frame: Vec<u8>,
    pub received_frames: Vec<Vec<u8>>,
    pub acknowledgement_frames: Vec<Vec<u8>>,
    pub points: Vec<TelemetryPoint>,
    pub activation_confirmed: bool,
    pub activation_terminated: bool,
    pub final_send_sequence: u16,
    pub final_receive_sequence: u16,
}

fn iec104_error(
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
    iec104_error("IEC104_INVALID", message, details)
}

fn param(message: impl Into<String>, details: serde_json::Value) -> CoreError {
    iec104_error("IEC104_PARAM_INVALID", message, Some(details))
}

fn validate_sequence(value: u16, field: &'static str) -> Result<(), CoreError> {
    if value > MAX_SEQUENCE {
        return Err(param(
            format!("IEC104 {field} 必须在 0..32767"),
            serde_json::json!({ "field": field, "value": value, "maximum": MAX_SEQUENCE }),
        ));
    }
    Ok(())
}

fn write_sequence(frame: &mut [u8], offset: usize, sequence: u16) {
    let encoded = sequence << 1;
    frame[offset] = encoded as u8;
    frame[offset + 1] = (encoded >> 8) as u8;
}

fn read_sequence(low: u8, high: u8) -> u16 {
    u16::from_le_bytes([low, high]) >> 1
}

pub fn next_sequence(value: u16) -> u16 {
    (value + 1) & MAX_SEQUENCE
}

pub fn build_i_frame(
    send_sequence: u16,
    receive_sequence: u16,
    asdu: &[u8],
) -> Result<Vec<u8>, CoreError> {
    validate_sequence(send_sequence, "sendSequence")?;
    validate_sequence(receive_sequence, "receiveSequence")?;
    if asdu.is_empty() || asdu.len() > MAX_ASDU_LENGTH {
        return Err(param(
            format!("IEC104 ASDU 长度必须在 1..{MAX_ASDU_LENGTH}"),
            serde_json::json!({ "length": asdu.len(), "maximum": MAX_ASDU_LENGTH }),
        ));
    }
    let mut frame = vec![0u8; 6 + asdu.len()];
    frame[0] = START_BYTE;
    frame[1] = (4 + asdu.len()) as u8;
    write_sequence(&mut frame, 2, send_sequence);
    write_sequence(&mut frame, 4, receive_sequence);
    frame[6..].copy_from_slice(asdu);
    Ok(frame)
}

pub fn build_s_frame(receive_sequence: u16) -> Result<Vec<u8>, CoreError> {
    validate_sequence(receive_sequence, "receiveSequence")?;
    let mut frame = vec![START_BYTE, 0x04, 0x01, 0x00, 0x00, 0x00];
    write_sequence(&mut frame, 4, receive_sequence);
    Ok(frame)
}

pub fn build_u_frame(function: UFunction) -> Vec<u8> {
    vec![START_BYTE, 0x04, function.control(), 0x00, 0x00, 0x00]
}

pub fn parse_apdu(frame: &[u8]) -> Result<Apdu, CoreError> {
    if frame.len() < 6 {
        return Err(invalid(
            "IEC104 APDU 少于 6 字节",
            Some(serde_json::json!({ "length": frame.len() })),
        ));
    }
    if frame[0] != START_BYTE {
        return Err(invalid(
            "IEC104 APDU 起始字节必须为 0x68",
            Some(serde_json::json!({ "actual": frame[0] })),
        ));
    }
    let declared = frame[1] as usize;
    if !(4..=MAX_APDU_LENGTH_FIELD).contains(&declared) {
        return Err(invalid(
            "IEC104 APDU 长度字段必须在 4..253",
            Some(serde_json::json!({ "declared": declared })),
        ));
    }
    if frame.len() != declared + 2 {
        return Err(invalid(
            "IEC104 APDU 长度字段与实际帧长度不一致",
            Some(
                serde_json::json!({ "declared": declared, "actual": frame.len().saturating_sub(2) }),
            ),
        ));
    }

    let (c0, c1, c2, c3) = (frame[2], frame[3], frame[4], frame[5]);
    if c0 & 0x01 == 0 {
        if c2 & 0x01 != 0 || declared == 4 {
            return Err(invalid("IEC104 I 帧控制域或 ASDU 无效", None));
        }
        return Ok(Apdu::I {
            send_sequence: read_sequence(c0, c1),
            receive_sequence: read_sequence(c2, c3),
            asdu: frame[6..].to_vec(),
        });
    }

    if c0 & 0x03 == 0x01 {
        if declared != 4 || c0 != 0x01 || c1 != 0 || c2 & 0x01 != 0 {
            return Err(invalid("IEC104 S 帧控制域无效", None));
        }
        return Ok(Apdu::S {
            receive_sequence: read_sequence(c2, c3),
        });
    }

    let function = UFunction::from_control(c0);
    if declared != 4 || c1 != 0 || c2 != 0 || c3 != 0 || function.is_none() {
        return Err(invalid(
            "IEC104 U 帧控制域未知或无效",
            Some(serde_json::json!({ "control": c0 })),
        ));
    }
    Ok(Apdu::U {
        function: function.expect("checked above"),
    })
}

pub fn build_general_interrogation_asdu(
    common_address: u16,
    group: u8,
    originator_address: u8,
) -> Result<Vec<u8>, CoreError> {
    if group > 16 {
        return Err(param(
            "IEC104 总召组号必须在 0..16（0=站总召）",
            serde_json::json!({ "group": group }),
        ));
    }
    let qualifier = 20 + group;
    Ok(vec![
        TYPE_C_IC_NA_1,
        0x01,
        COT_ACTIVATION,
        originator_address,
        common_address as u8,
        (common_address >> 8) as u8,
        0x00,
        0x00,
        0x00,
        qualifier,
    ])
}

fn object_data_length(type_id: u8) -> Result<usize, CoreError> {
    match type_id {
        TYPE_M_SP_NA_1 | TYPE_M_DP_NA_1 | TYPE_C_IC_NA_1 => Ok(1),
        TYPE_M_ME_NA_1 => Ok(3),
        TYPE_M_ME_NC_1 | TYPE_M_IT_NA_1 => Ok(5),
        _ => Err(iec104_error(
            "IEC104_ASDU_UNSUPPORTED",
            format!("首轮 IEC104 只读编解码不支持 ASDU type {type_id}"),
            Some(serde_json::json!({ "typeId": type_id })),
        )),
    }
}

fn read_ioa(bytes: &[u8], cursor: &mut usize) -> Result<u32, CoreError> {
    let ioa = bytes
        .get(*cursor..cursor.saturating_add(3))
        .ok_or_else(|| invalid("IEC104 ASDU 缺少 3 字节 IOA", None))?;
    *cursor += 3;
    Ok(ioa[0] as u32 | ((ioa[1] as u32) << 8) | ((ioa[2] as u32) << 16))
}

pub fn parse_asdu(bytes: &[u8]) -> Result<Asdu, CoreError> {
    if bytes.len() < 6 {
        return Err(invalid(
            "IEC104 ASDU 少于 6 字节头",
            Some(serde_json::json!({ "length": bytes.len() })),
        ));
    }
    let type_id = bytes[0];
    let data_length = object_data_length(type_id)?;
    let vsq = bytes[1];
    let count = (vsq & 0x7F) as usize;
    let is_sequence = vsq & 0x80 != 0;
    if count == 0 {
        return Err(invalid("IEC104 ASDU VSQ 对象数量不能为 0", None));
    }
    let expected = 6 + if is_sequence { 3 } else { count * 3 } + count * data_length;
    if bytes.len() != expected {
        return Err(invalid(
            "IEC104 ASDU 长度与 type/VSQ 不一致",
            Some(serde_json::json!({
                "typeId": type_id,
                "vsq": vsq,
                "expected": expected,
                "actual": bytes.len()
            })),
        ));
    }

    let encoded_cause = bytes[2];
    let cause = encoded_cause & 0x3F;
    let common_address = u16::from_le_bytes([bytes[4], bytes[5]]);
    let mut cursor = 6;
    let mut objects = Vec::with_capacity(count);
    if is_sequence {
        let base = read_ioa(bytes, &mut cursor)?;
        if base + count as u32 - 1 > 0xFF_FFFF {
            return Err(invalid("IEC104 顺序 ASDU 的 IOA 超出 24 位", None));
        }
        for index in 0..count {
            let end = cursor + data_length;
            objects.push(InformationObject {
                address: base + index as u32,
                data: bytes[cursor..end].to_vec(),
            });
            cursor = end;
        }
    } else {
        for _ in 0..count {
            let address = read_ioa(bytes, &mut cursor)?;
            let end = cursor + data_length;
            objects.push(InformationObject {
                address,
                data: bytes[cursor..end].to_vec(),
            });
            cursor = end;
        }
    }

    Ok(Asdu {
        type_id,
        type_name: type_name(type_id),
        cause,
        cause_name: cause_name(cause),
        is_negative: encoded_cause & 0x40 != 0,
        is_test: encoded_cause & 0x80 != 0,
        originator_address: bytes[3],
        common_address,
        is_sequence,
        objects,
    })
}

fn decode_quality(raw: u8, allow_overflow: bool) -> QualityDescriptor {
    let applicable = raw & if allow_overflow { 0xF1 } else { 0xF0 };
    let overflow = allow_overflow && applicable & 0x01 != 0;
    let blocked = applicable & 0x10 != 0;
    let substituted = applicable & 0x20 != 0;
    let not_topical = applicable & 0x40 != 0;
    let invalid = applicable & 0x80 != 0;
    let mut labels = Vec::new();
    if overflow {
        labels.push("overflow");
    }
    if blocked {
        labels.push("blocked");
    }
    if substituted {
        labels.push("substituted");
    }
    if not_topical {
        labels.push("notTopical");
    }
    if invalid {
        labels.push("invalid");
    }
    QualityDescriptor {
        raw: applicable,
        overflow,
        blocked,
        substituted,
        not_topical,
        invalid,
        is_good: applicable == 0,
        labels,
    }
}

pub fn decode_telemetry(asdu: &Asdu) -> Result<Vec<TelemetryPoint>, CoreError> {
    if !matches!(
        asdu.type_id,
        TYPE_M_SP_NA_1 | TYPE_M_DP_NA_1 | TYPE_M_ME_NA_1 | TYPE_M_ME_NC_1 | TYPE_M_IT_NA_1
    ) {
        return Err(iec104_error(
            "IEC104_ASDU_NOT_MONITORING",
            format!("ASDU {} 不是首轮支持的监视方向类型", asdu.type_id),
            Some(serde_json::json!({ "typeId": asdu.type_id })),
        ));
    }

    let mut points = Vec::with_capacity(asdu.objects.len());
    for object in &asdu.objects {
        let (quality, value) = match asdu.type_id {
            TYPE_M_SP_NA_1 => {
                let raw = object.data[0];
                (
                    decode_quality(raw, false),
                    TelemetryValue::SinglePoint {
                        value: raw & 0x01 != 0,
                    },
                )
            }
            TYPE_M_DP_NA_1 => {
                let raw = object.data[0];
                let state = raw & 0x03;
                (
                    decode_quality(raw, false),
                    TelemetryValue::DoublePoint {
                        state,
                        state_name: double_point_state_name(state),
                    },
                )
            }
            TYPE_M_ME_NA_1 => {
                let raw = i16::from_le_bytes([object.data[0], object.data[1]]);
                (
                    decode_quality(object.data[2], true),
                    TelemetryValue::NormalizedMeasured {
                        raw,
                        value: raw as f64 / 32768.0,
                    },
                )
            }
            TYPE_M_ME_NC_1 => {
                let value = f32::from_le_bytes([
                    object.data[0],
                    object.data[1],
                    object.data[2],
                    object.data[3],
                ]);
                if !value.is_finite() {
                    return Err(iec104_error(
                        "IEC104_VALUE_INVALID",
                        "IEC104 短浮点遥测值不是有限数",
                        Some(serde_json::json!({
                            "commonAddress": asdu.common_address,
                            "informationObjectAddress": object.address
                        })),
                    ));
                }
                (
                    decode_quality(object.data[4], true),
                    TelemetryValue::ShortFloatMeasured { value },
                )
            }
            TYPE_M_IT_NA_1 => {
                let value = i32::from_le_bytes([
                    object.data[0],
                    object.data[1],
                    object.data[2],
                    object.data[3],
                ]);
                let bcr = object.data[4];
                (
                    decode_quality(bcr & 0x80, false),
                    TelemetryValue::IntegratedTotal {
                        value,
                        sequence_number: bcr & 0x1F,
                        carry: bcr & 0x20 != 0,
                        adjusted: bcr & 0x40 != 0,
                    },
                )
            }
            _ => unreachable!("type checked above"),
        };
        points.push(TelemetryPoint {
            common_address: asdu.common_address,
            information_object_address: object.address,
            type_id: asdu.type_id,
            type_name: asdu.type_name,
            cause: asdu.cause,
            cause_name: asdu.cause_name,
            originator_address: asdu.originator_address,
            quality,
            value,
        });
    }
    Ok(points)
}

pub fn is_monitoring_type(type_id: u8) -> bool {
    matches!(
        type_id,
        TYPE_M_SP_NA_1 | TYPE_M_DP_NA_1 | TYPE_M_ME_NA_1 | TYPE_M_ME_NC_1 | TYPE_M_IT_NA_1
    )
}

pub fn expected_interrogation_cause(group: u8) -> Result<u8, CoreError> {
    if group > 16 {
        return Err(param(
            "IEC104 总召组号必须在 0..16",
            serde_json::json!({ "group": group }),
        ));
    }
    Ok(COT_INTERROGATED_BY_STATION + group)
}

pub fn type_name(type_id: u8) -> &'static str {
    match type_id {
        TYPE_M_SP_NA_1 => "M_SP_NA_1",
        TYPE_M_DP_NA_1 => "M_DP_NA_1",
        TYPE_M_ME_NA_1 => "M_ME_NA_1",
        TYPE_M_ME_NC_1 => "M_ME_NC_1",
        TYPE_M_IT_NA_1 => "M_IT_NA_1",
        TYPE_C_IC_NA_1 => "C_IC_NA_1",
        _ => "UNKNOWN",
    }
}

pub fn cause_name(cause: u8) -> &'static str {
    match cause {
        COT_PERIODIC => "periodic",
        COT_BACKGROUND => "background",
        COT_SPONTANEOUS => "spontaneous",
        4 => "initialized",
        COT_REQUEST => "request",
        COT_ACTIVATION => "activation",
        COT_ACTIVATION_CONFIRMATION => "activationConfirmation",
        8 => "deactivation",
        9 => "deactivationConfirmation",
        COT_ACTIVATION_TERMINATION => "activationTermination",
        20 => "interrogatedByStation",
        21 => "interrogatedByGroup1",
        22 => "interrogatedByGroup2",
        23 => "interrogatedByGroup3",
        24 => "interrogatedByGroup4",
        25 => "interrogatedByGroup5",
        26 => "interrogatedByGroup6",
        27 => "interrogatedByGroup7",
        28 => "interrogatedByGroup8",
        29 => "interrogatedByGroup9",
        30 => "interrogatedByGroup10",
        31 => "interrogatedByGroup11",
        32 => "interrogatedByGroup12",
        33 => "interrogatedByGroup13",
        34 => "interrogatedByGroup14",
        35 => "interrogatedByGroup15",
        36 => "interrogatedByGroup16",
        44 => "unknownTypeId",
        45 => "unknownCause",
        46 => "unknownCommonAddress",
        47 => "unknownInformationObjectAddress",
        _ => "unknown",
    }
}

pub fn double_point_state_name(state: u8) -> &'static str {
    match state & 0x03 {
        0 => "intermediate",
        1 => "off",
        2 => "on",
        _ => "indeterminate",
    }
}

pub fn frame_hex(frame: &[u8]) -> String {
    frame
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<Vec<_>>()
        .join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn general_interrogation_matches_golden_frame() {
        let asdu = build_general_interrogation_asdu(1, 0, 0).unwrap();
        assert_eq!(frame_hex(&asdu), "64 01 06 00 01 00 00 00 00 14");
        let frame = build_i_frame(0, 0, &asdu).unwrap();
        assert_eq!(
            frame_hex(&frame),
            "68 0E 00 00 00 00 64 01 06 00 01 00 00 00 00 14"
        );
        assert_eq!(
            parse_apdu(&frame).unwrap(),
            Apdu::I {
                send_sequence: 0,
                receive_sequence: 0,
                asdu,
            }
        );
    }

    #[test]
    fn strict_i_s_u_frames_round_trip() {
        assert_eq!(
            parse_apdu(&build_s_frame(3).unwrap()).unwrap(),
            Apdu::S {
                receive_sequence: 3
            }
        );
        for function in [
            UFunction::StartDataTransferActivation,
            UFunction::StartDataTransferConfirmation,
            UFunction::StopDataTransferActivation,
            UFunction::StopDataTransferConfirmation,
            UFunction::TestFrameActivation,
            UFunction::TestFrameConfirmation,
        ] {
            assert_eq!(
                parse_apdu(&build_u_frame(function)).unwrap(),
                Apdu::U { function }
            );
        }
    }

    #[test]
    fn single_point_preserves_value_and_high_quality_bits() {
        let asdu =
            parse_asdu(&[0x01, 0x01, 0x14, 0x00, 0x01, 0x00, 0x2A, 0x00, 0x00, 0xF1]).unwrap();
        let points = decode_telemetry(&asdu).unwrap();
        assert_eq!(points[0].information_object_address, 42);
        assert_eq!(points[0].value, TelemetryValue::SinglePoint { value: true });
        assert_eq!(points[0].quality.raw, 0xF0);
        assert!(!points[0].quality.overflow);
        assert!(points[0].quality.blocked);
        assert!(points[0].quality.invalid);
    }

    #[test]
    fn sequence_normalized_values_use_contiguous_ioas() {
        let asdu = parse_asdu(&[
            0x09, 0x82, 0x14, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 0x00, 0xC0, 0xF1, 0x00, 0x40,
            0x00,
        ])
        .unwrap();
        assert_eq!(asdu.objects[0].address, 10);
        assert_eq!(asdu.objects[1].address, 11);
        let points = decode_telemetry(&asdu).unwrap();
        assert_eq!(
            points[0].value,
            TelemetryValue::NormalizedMeasured {
                raw: -16384,
                value: -0.5,
            }
        );
        assert_eq!(points[0].quality.raw, 0xF1);
    }

    #[test]
    fn decodes_short_float_and_integrated_total() {
        let mut float_asdu = vec![0x0D, 0x01, 0x03, 0x00, 0x01, 0x00, 0x07, 0x00, 0x00];
        float_asdu.extend_from_slice(&12.5f32.to_le_bytes());
        float_asdu.push(0x00);
        let point = decode_telemetry(&parse_asdu(&float_asdu).unwrap()).unwrap();
        assert_eq!(
            point[0].value,
            TelemetryValue::ShortFloatMeasured { value: 12.5 }
        );

        let total = parse_asdu(&[
            0x0F, 0x01, 0x14, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x00, 0xE3,
        ])
        .unwrap();
        let point = decode_telemetry(&total).unwrap();
        assert_eq!(
            point[0].value,
            TelemetryValue::IntegratedTotal {
                value: 42,
                sequence_number: 3,
                carry: true,
                adjusted: true,
            }
        );
        assert!(point[0].quality.invalid);
    }

    #[test]
    fn rejects_truncated_trailing_unknown_and_zero_count() {
        for frame in [
            vec![0x68, 0x04],
            vec![0x68, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00],
            vec![0x68, 0x04, 0x03, 0x00, 0x00, 0x00],
        ] {
            assert!(parse_apdu(&frame).is_err());
        }
        for asdu in [
            vec![0x01, 0x00, 0x03, 0x00, 0x01, 0x00],
            vec![0xFE, 0x01, 0x03, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00],
            vec![0x01, 0x01, 0x03, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00],
            vec![
                0x01, 0x01, 0x03, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            ],
        ] {
            assert!(parse_asdu(&asdu).is_err());
        }
    }

    #[test]
    fn rejects_group_17_and_sequence_above_15_bits() {
        assert!(build_general_interrogation_asdu(1, 17, 0).is_err());
        assert!(build_s_frame(0x8000).is_err());
        assert_eq!(next_sequence(MAX_SEQUENCE), 0);
    }
}
