//! DNP3/TCP read-only master codec.
//!
//! The first Rust boundary implements strict data-link CRC/framing, transport
//! segmentation, solicited/unsolicited application responses, Class 0/1/2/3
//! READ requests and a bounded set of static/event measurement variations.
//! Control, time-write, restart, freeze, file transfer, Secure Authentication,
//! serial transport and outstation mode are intentionally absent.

use serde::Serialize;

use crate::error::CoreError;

pub const DEFAULT_PORT: u16 = 20_000;
pub const DEFAULT_MASTER_ADDRESS: u16 = 1;
pub const DEFAULT_OUTSTATION_ADDRESS: u16 = 1024;
pub const START_BYTE_1: u8 = 0x05;
pub const START_BYTE_2: u8 = 0x64;
pub const LINK_HEADER_LENGTH: usize = 10;
pub const MAX_LINK_USER_DATA_LENGTH: usize = 250;
pub const MAX_APPLICATION_BYTES_PER_LINK_FRAME: usize = 249;
pub const MAX_APPLICATION_FRAGMENT_SIZE: usize = 65_535;
pub const MAX_RESPONSE_LINK_FRAMES: usize = 2048;
pub const MAX_RESPONSE_POINTS: usize = 65_536;

pub const MASTER_UNCONFIRMED_USER_DATA: u8 = 0xC4;
pub const OUTSTATION_UNCONFIRMED_USER_DATA: u8 = 0x44;

pub const FUNCTION_CONFIRM: u8 = 0x00;
pub const FUNCTION_READ: u8 = 0x01;
pub const FUNCTION_RESPONSE: u8 = 0x81;
pub const FUNCTION_UNSOLICITED_RESPONSE: u8 = 0x82;

fn dnp3_error(
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

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LinkFrame {
    pub control: u8,
    pub destination: u16,
    pub source: u16,
    pub user_data: Vec<u8>,
    pub is_primary: bool,
    pub is_master: bool,
    pub function: u8,
    pub is_user_data: bool,
}

/// DNP3 CRC-16 using the reflected 0xA6BC polynomial and final complement.
pub fn calculate_crc(data: &[u8]) -> u16 {
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

fn append_u16_le(target: &mut Vec<u8>, value: u16) {
    target.extend_from_slice(&value.to_le_bytes());
}

fn read_u16_le(data: &[u8], offset: usize) -> Result<u16, CoreError> {
    let bytes = data.get(offset..offset + 2).ok_or_else(|| {
        dnp3_error(
            "DNP3_TRUNCATED",
            "DNP3 字段缺少两个字节",
            Some(serde_json::json!({ "offset": offset })),
        )
    })?;
    Ok(u16::from_le_bytes([bytes[0], bytes[1]]))
}

pub fn wire_body_length(length_field: u8) -> Result<usize, CoreError> {
    if length_field < 5 {
        return Err(dnp3_error(
            "DNP3_LINK_LENGTH_INVALID",
            "DNP3 数据链路 length 字段不能小于 5",
            Some(serde_json::json!({ "lengthField": length_field })),
        ));
    }
    let user_length = usize::from(length_field - 5);
    let crc_blocks = user_length.div_ceil(16);
    Ok(user_length + crc_blocks * 2)
}

pub fn wire_length_from_length_field(length_field: u8) -> Result<usize, CoreError> {
    Ok(LINK_HEADER_LENGTH + wire_body_length(length_field)?)
}

pub fn build_link_frame(
    control: u8,
    destination: u16,
    source: u16,
    user_data: &[u8],
) -> Result<Vec<u8>, CoreError> {
    if user_data.len() > MAX_LINK_USER_DATA_LENGTH {
        return Err(dnp3_error(
            "DNP3_LINK_USER_DATA_TOO_LARGE",
            format!("DNP3 数据链路 user data 不能超过 {MAX_LINK_USER_DATA_LENGTH} 字节"),
            Some(serde_json::json!({ "length": user_data.len() })),
        ));
    }
    let length_field = u8::try_from(5 + user_data.len()).map_err(|_| {
        dnp3_error(
            "DNP3_LINK_LENGTH_INVALID",
            "DNP3 数据链路 length 字段溢出",
            None,
        )
    })?;
    let mut frame =
        Vec::with_capacity(LINK_HEADER_LENGTH + user_data.len() + user_data.len().div_ceil(16) * 2);
    frame.extend_from_slice(&[
        START_BYTE_1,
        START_BYTE_2,
        length_field,
        control,
        destination as u8,
        (destination >> 8) as u8,
        source as u8,
        (source >> 8) as u8,
    ]);
    let header_crc = calculate_crc(&frame);
    append_u16_le(&mut frame, header_crc);
    for block in user_data.chunks(16) {
        frame.extend_from_slice(block);
        append_u16_le(&mut frame, calculate_crc(block));
    }
    Ok(frame)
}

pub fn parse_link_frame(frame: &[u8]) -> Result<LinkFrame, CoreError> {
    if frame.len() < LINK_HEADER_LENGTH {
        return Err(dnp3_error(
            "DNP3_LINK_TRUNCATED",
            "DNP3 数据链路帧短于 10 字节",
            Some(serde_json::json!({ "length": frame.len() })),
        ));
    }
    if frame[0] != START_BYTE_1 || frame[1] != START_BYTE_2 {
        return Err(dnp3_error(
            "DNP3_LINK_SYNC_INVALID",
            "DNP3 数据链路同步字必须为 05 64",
            Some(serde_json::json!({ "actual": [frame[0], frame[1]] })),
        ));
    }
    let expected_wire_length = wire_length_from_length_field(frame[2])?;
    if frame.len() != expected_wire_length {
        return Err(dnp3_error(
            "DNP3_LINK_LENGTH_MISMATCH",
            format!(
                "DNP3 数据链路帧长度不符，期望 {expected_wire_length}，实际 {}",
                frame.len()
            ),
            Some(serde_json::json!({
                "lengthField": frame[2],
                "expected": expected_wire_length,
                "actual": frame.len()
            })),
        ));
    }
    let expected_header_crc = calculate_crc(&frame[..8]);
    let actual_header_crc = read_u16_le(frame, 8)?;
    if actual_header_crc != expected_header_crc {
        return Err(dnp3_error(
            "DNP3_LINK_HEADER_CRC_MISMATCH",
            "DNP3 数据链路头 CRC 校验失败",
            Some(serde_json::json!({
                "expected": expected_header_crc,
                "actual": actual_header_crc
            })),
        ));
    }

    let user_length = usize::from(frame[2] - 5);
    let mut user_data = Vec::with_capacity(user_length);
    let mut wire_offset = LINK_HEADER_LENGTH;
    while user_data.len() < user_length {
        let block_length = (user_length - user_data.len()).min(16);
        let block = &frame[wire_offset..wire_offset + block_length];
        let expected_crc = calculate_crc(block);
        let actual_crc = read_u16_le(frame, wire_offset + block_length)?;
        if actual_crc != expected_crc {
            return Err(dnp3_error(
                "DNP3_LINK_DATA_CRC_MISMATCH",
                format!("DNP3 数据链路数据块 {} CRC 校验失败", user_data.len() / 16),
                Some(serde_json::json!({
                    "block": user_data.len() / 16,
                    "expected": expected_crc,
                    "actual": actual_crc
                })),
            ));
        }
        user_data.extend_from_slice(block);
        wire_offset += block_length + 2;
    }

    let control = frame[3];
    let is_primary = control & 0x40 != 0;
    let function = control & 0x0F;
    Ok(LinkFrame {
        control,
        destination: u16::from_le_bytes([frame[4], frame[5]]),
        source: u16::from_le_bytes([frame[6], frame[7]]),
        user_data,
        is_primary,
        is_master: control & 0x80 != 0,
        function,
        is_user_data: is_primary && matches!(function, 3 | 4),
    })
}

pub fn segment_application(
    application_fragment: &[u8],
    next_sequence: &mut u8,
) -> Result<Vec<Vec<u8>>, CoreError> {
    if application_fragment.is_empty() {
        return Err(dnp3_error(
            "DNP3_APPLICATION_EMPTY",
            "DNP3 应用层片段不能为空",
            None,
        ));
    }
    let mut result = Vec::new();
    let mut offset = 0usize;
    while offset < application_fragment.len() {
        let count = (application_fragment.len() - offset).min(MAX_APPLICATION_BYTES_PER_LINK_FRAME);
        let first = offset == 0;
        let final_segment = offset + count == application_fragment.len();
        let mut segment = Vec::with_capacity(count + 1);
        segment.push(
            (if first { 0x80 } else { 0 })
                | (if final_segment { 0x40 } else { 0 })
                | (*next_sequence & 0x3F),
        );
        segment.extend_from_slice(&application_fragment[offset..offset + count]);
        result.push(segment);
        *next_sequence = next_sequence.wrapping_add(1) & 0x3F;
        offset += count;
    }
    Ok(result)
}

#[derive(Debug)]
pub struct TransportAssembler {
    maximum_size: usize,
    bytes: Vec<u8>,
    active: bool,
    expected_sequence: u8,
}

impl TransportAssembler {
    pub fn new(maximum_size: usize) -> Result<Self, CoreError> {
        if maximum_size < 4 {
            return Err(dnp3_error(
                "DNP3_FRAGMENT_LIMIT_INVALID",
                "DNP3 最大应用片段长度不能小于 4",
                Some(serde_json::json!({ "maximumSize": maximum_size })),
            ));
        }
        Ok(Self {
            maximum_size,
            bytes: Vec::new(),
            active: false,
            expected_sequence: 0,
        })
    }

    pub fn add(&mut self, user_data: &[u8]) -> Result<Option<Vec<u8>>, CoreError> {
        if user_data.len() < 2 {
            return Err(dnp3_error(
                "DNP3_TRANSPORT_TRUNCATED",
                "DNP3 传输片段没有应用层数据",
                Some(serde_json::json!({ "length": user_data.len() })),
            ));
        }
        let header = user_data[0];
        let first = header & 0x80 != 0;
        let final_segment = header & 0x40 != 0;
        let sequence = header & 0x3F;
        if first {
            self.bytes.clear();
            self.active = true;
            self.expected_sequence = sequence;
        }
        if !self.active {
            return Err(dnp3_error(
                "DNP3_TRANSPORT_FIR_MISSING",
                "DNP3 传输层续片在 FIR 首片之前到达",
                Some(serde_json::json!({ "sequence": sequence })),
            ));
        }
        if sequence != self.expected_sequence {
            let expected = self.expected_sequence;
            self.reset();
            return Err(dnp3_error(
                "DNP3_TRANSPORT_SEQUENCE_MISMATCH",
                format!("DNP3 传输序号不符，期望 {expected}，实际 {sequence}"),
                Some(serde_json::json!({ "expected": expected, "actual": sequence })),
            ));
        }
        self.bytes.extend_from_slice(&user_data[1..]);
        if self.bytes.len() > self.maximum_size {
            let actual = self.bytes.len();
            self.reset();
            return Err(dnp3_error(
                "DNP3_APPLICATION_FRAGMENT_TOO_LARGE",
                format!("DNP3 应用片段超过 {} 字节", self.maximum_size),
                Some(serde_json::json!({
                    "maximum": self.maximum_size,
                    "actual": actual
                })),
            ));
        }
        self.expected_sequence = sequence.wrapping_add(1) & 0x3F;
        if !final_segment {
            return Ok(None);
        }
        let result = std::mem::take(&mut self.bytes);
        self.active = false;
        Ok(Some(result))
    }

    fn reset(&mut self) {
        self.active = false;
        self.bytes.clear();
    }
}

fn application_control(
    sequence: u8,
    first: bool,
    final_fragment: bool,
    confirm: bool,
    unsolicited: bool,
) -> Result<u8, CoreError> {
    if sequence > 0x0F {
        return Err(dnp3_error(
            "DNP3_APPLICATION_SEQUENCE_INVALID",
            "DNP3 应用层序号必须在 0..15",
            Some(serde_json::json!({ "sequence": sequence })),
        ));
    }
    Ok((if first { 0x80 } else { 0 })
        | (if final_fragment { 0x40 } else { 0 })
        | (if confirm { 0x20 } else { 0 })
        | (if unsolicited { 0x10 } else { 0 })
        | sequence)
}

fn supported_read_group(group: u8) -> bool {
    matches!(
        group,
        1 | 2 | 3 | 4 | 10 | 11 | 20 | 21 | 22 | 23 | 30 | 32 | 40 | 42
    )
}

pub fn build_read_request(
    sequence: u8,
    group: u8,
    variation: u8,
    start: u16,
    stop: u16,
) -> Result<Vec<u8>, CoreError> {
    if stop < start {
        return Err(dnp3_error(
            "DNP3_RANGE_INVALID",
            "DNP3 读取终止索引不能小于起始索引",
            Some(serde_json::json!({ "start": start, "stop": stop })),
        ));
    }
    if !supported_read_group(group) || variation == 0 {
        return Err(dnp3_error(
            "DNP3_VARIATION_UNSUPPORTED",
            format!("DNP3 首轮只读边界不支持 g{group}v{variation}"),
            Some(serde_json::json!({ "group": group, "variation": variation })),
        ));
    }
    let mut request = vec![
        application_control(sequence, true, true, false, false)?,
        FUNCTION_READ,
        group,
        variation,
        0x01,
    ];
    append_u16_le(&mut request, start);
    append_u16_le(&mut request, stop);
    Ok(request)
}

pub fn build_read_all_request(
    sequence: u8,
    group: u8,
    variation: u8,
) -> Result<Vec<u8>, CoreError> {
    if !supported_read_group(group) || variation == 0 {
        return Err(dnp3_error(
            "DNP3_VARIATION_UNSUPPORTED",
            format!("DNP3 首轮只读边界不支持 g{group}v{variation}"),
            Some(serde_json::json!({ "group": group, "variation": variation })),
        ));
    }
    Ok(vec![
        application_control(sequence, true, true, false, false)?,
        FUNCTION_READ,
        group,
        variation,
        0x06,
    ])
}

pub fn build_class_scan(
    sequence: u8,
    class0: bool,
    class1: bool,
    class2: bool,
    class3: bool,
) -> Result<Vec<u8>, CoreError> {
    if !(class0 || class1 || class2 || class3) {
        return Err(dnp3_error(
            "DNP3_CLASS_SELECTION_EMPTY",
            "DNP3 Class 扫描至少选择 Class 0/1/2/3 中的一项",
            None,
        ));
    }
    let mut request = vec![
        application_control(sequence, true, true, false, false)?,
        FUNCTION_READ,
    ];
    for (enabled, variation) in [(class0, 1u8), (class1, 2), (class2, 3), (class3, 4)] {
        if enabled {
            request.extend_from_slice(&[60, variation, 0x06]);
        }
    }
    Ok(request)
}

pub fn build_confirm(sequence: u8, unsolicited: bool) -> Result<Vec<u8>, CoreError> {
    Ok(vec![
        application_control(sequence, true, true, false, unsolicited)?,
        FUNCTION_CONFIRM,
    ])
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Quality {
    pub raw: u8,
    pub online: bool,
    pub restart: bool,
    pub communication_lost: bool,
    pub remote_forced: bool,
    pub local_forced: bool,
    pub over_range_or_chatter: bool,
    pub reference_error: bool,
    pub is_good: bool,
    pub labels: Vec<&'static str>,
}

fn quality(raw: u8, binary: bool) -> Quality {
    let mut labels = Vec::new();
    let online = raw & 0x01 != 0;
    let restart = raw & 0x02 != 0;
    let communication_lost = raw & 0x04 != 0;
    let remote_forced = raw & 0x08 != 0;
    let local_forced = raw & 0x10 != 0;
    let over_range_or_chatter = raw & 0x20 != 0;
    let reference_error = !binary && raw & 0x40 != 0;
    if !online {
        labels.push("offline");
    }
    if restart {
        labels.push("restart");
    }
    if communication_lost {
        labels.push("communication-lost");
    }
    if remote_forced {
        labels.push("remote-forced");
    }
    if local_forced {
        labels.push("local-forced");
    }
    if over_range_or_chatter {
        labels.push(if binary {
            "chatter-filter"
        } else {
            "over-range"
        });
    }
    if reference_error {
        labels.push("reference-error");
    }
    let is_good = online
        && !(restart
            || communication_lost
            || remote_forced
            || local_forced
            || over_range_or_chatter
            || reference_error);
    if is_good {
        labels.push("good");
    }
    Quality {
        raw,
        online,
        restart,
        communication_lost,
        remote_forced,
        local_forced,
        over_range_or_chatter,
        reference_error,
        is_good,
        labels,
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    tag = "kind"
)]
pub enum PointValue {
    Binary { value: bool },
    DoubleBit { state: u8, state_name: &'static str },
    Unsigned { value: u64 },
    Signed { value: i64 },
    Float { value: f64 },
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Point {
    pub group: u8,
    pub variation: u8,
    pub variation_name: &'static str,
    pub index: u16,
    pub event: bool,
    pub flags: Option<u8>,
    pub quality: Option<Quality>,
    pub timestamp_ms: Option<u64>,
    pub relative_time_ms: Option<u16>,
    pub value: PointValue,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Iin {
    pub iin1: u8,
    pub iin2: u8,
    pub raw: u16,
    pub request_error: bool,
    pub labels: Vec<&'static str>,
}

pub fn decode_iin(iin1: u8, iin2: u8) -> Iin {
    let mut labels = Vec::new();
    for (set, label) in [
        (iin1 & 0x01 != 0, "broadcast"),
        (iin1 & 0x02 != 0, "class-1-events"),
        (iin1 & 0x04 != 0, "class-2-events"),
        (iin1 & 0x08 != 0, "class-3-events"),
        (iin1 & 0x10 != 0, "need-time"),
        (iin1 & 0x20 != 0, "local-control"),
        (iin1 & 0x40 != 0, "device-trouble"),
        (iin1 & 0x80 != 0, "device-restart"),
        (iin2 & 0x01 != 0, "function-not-supported"),
        (iin2 & 0x02 != 0, "object-unknown"),
        (iin2 & 0x04 != 0, "parameter-error"),
        (iin2 & 0x08 != 0, "event-buffer-overflow"),
        (iin2 & 0x10 != 0, "already-executing"),
        (iin2 & 0x20 != 0, "configuration-corrupt"),
    ] {
        if set {
            labels.push(label);
        }
    }
    if labels.is_empty() {
        labels.push("none");
    }
    Iin {
        iin1,
        iin2,
        raw: u16::from(iin1) | (u16::from(iin2) << 8),
        request_error: iin2 & 0x07 != 0,
        labels,
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ApplicationResponseFragment {
    pub sequence: u8,
    pub first: bool,
    pub final_fragment: bool,
    pub confirm_requested: bool,
    pub unsolicited: bool,
    pub function: u8,
    pub iin: Iin,
    pub points: Vec<Point>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UnsolicitedResponse {
    pub initial_sequence: u8,
    pub iin: Iin,
    pub points: Vec<Point>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ScanResult {
    pub operation: String,
    pub classes: Vec<u8>,
    pub request_frames: Vec<Vec<u8>>,
    pub response_frames: Vec<Vec<u8>>,
    pub confirmation_frames: Vec<Vec<u8>>,
    pub initial_application_sequence: u8,
    pub iin: Iin,
    pub points: Vec<Point>,
    pub unsolicited_responses: Vec<UnsolicitedResponse>,
    pub final_application_sequence: u8,
    pub final_transport_sequence: u8,
}

fn require(data: &[u8], offset: usize, count: usize, context: &str) -> Result<(), CoreError> {
    if offset > data.len().saturating_sub(count) {
        return Err(dnp3_error(
            "DNP3_APPLICATION_TRUNCATED",
            format!("DNP3 {context} 数据被截断"),
            Some(serde_json::json!({
                "offset": offset,
                "required": count,
                "length": data.len()
            })),
        ));
    }
    Ok(())
}

fn take_u8(data: &[u8], offset: &mut usize, context: &str) -> Result<u8, CoreError> {
    require(data, *offset, 1, context)?;
    let value = data[*offset];
    *offset += 1;
    Ok(value)
}

fn take_u16(data: &[u8], offset: &mut usize, context: &str) -> Result<u16, CoreError> {
    require(data, *offset, 2, context)?;
    let value = u16::from_le_bytes([data[*offset], data[*offset + 1]]);
    *offset += 2;
    Ok(value)
}

fn take_i16(data: &[u8], offset: &mut usize, context: &str) -> Result<i16, CoreError> {
    Ok(take_u16(data, offset, context)? as i16)
}

fn take_u32(data: &[u8], offset: &mut usize, context: &str) -> Result<u32, CoreError> {
    require(data, *offset, 4, context)?;
    let value = u32::from_le_bytes([
        data[*offset],
        data[*offset + 1],
        data[*offset + 2],
        data[*offset + 3],
    ]);
    *offset += 4;
    Ok(value)
}

fn take_i32(data: &[u8], offset: &mut usize, context: &str) -> Result<i32, CoreError> {
    Ok(take_u32(data, offset, context)? as i32)
}

fn take_f32(data: &[u8], offset: &mut usize, context: &str) -> Result<f64, CoreError> {
    let value = f32::from_bits(take_u32(data, offset, context)?) as f64;
    if !value.is_finite() {
        return Err(dnp3_error(
            "DNP3_FLOAT_NON_FINITE",
            format!("DNP3 {context} 浮点值不是有限数"),
            None,
        ));
    }
    Ok(value)
}

fn take_f64(data: &[u8], offset: &mut usize, context: &str) -> Result<f64, CoreError> {
    require(data, *offset, 8, context)?;
    let value = f64::from_le_bytes([
        data[*offset],
        data[*offset + 1],
        data[*offset + 2],
        data[*offset + 3],
        data[*offset + 4],
        data[*offset + 5],
        data[*offset + 6],
        data[*offset + 7],
    ]);
    *offset += 8;
    if !value.is_finite() {
        return Err(dnp3_error(
            "DNP3_FLOAT_NON_FINITE",
            format!("DNP3 {context} 浮点值不是有限数"),
            None,
        ));
    }
    Ok(value)
}

fn take_u48(data: &[u8], offset: &mut usize, context: &str) -> Result<u64, CoreError> {
    require(data, *offset, 6, context)?;
    let mut bytes = [0u8; 8];
    bytes[..6].copy_from_slice(&data[*offset..*offset + 6]);
    *offset += 6;
    Ok(u64::from_le_bytes(bytes))
}

fn variation_name(group: u8, variation: u8) -> &'static str {
    match (group, variation) {
        (1, 1) => "Binary Input Packed",
        (1, 2) => "Binary Input With Flags",
        (2, 1) => "Binary Input Event",
        (2, 2) => "Binary Input Event With Time",
        (2, 3) => "Binary Input Event With Relative Time",
        (3, 1) => "Double-bit Input Packed",
        (3, 2) => "Double-bit Input With Flags",
        (4, 1) => "Double-bit Input Event",
        (4, 2) => "Double-bit Input Event With Time",
        (4, 3) => "Double-bit Input Event With Relative Time",
        (10, 1) => "Binary Output Status Packed",
        (10, 2) => "Binary Output Status With Flags",
        (11, 1) => "Binary Output Status Event",
        (11, 2) => "Binary Output Status Event With Time",
        (20, 1) => "Counter 32 With Flags",
        (20, 2) => "Counter 16 With Flags",
        (20, 5) => "Counter 32",
        (20, 6) => "Counter 16",
        (21, 1) => "Frozen Counter 32 With Flags",
        (21, 2) => "Frozen Counter 16 With Flags",
        (21, 5) => "Frozen Counter 32",
        (21, 6) => "Frozen Counter 16",
        (22, 1) => "Counter Event 32",
        (22, 2) => "Counter Event 16",
        (22, 5) => "Counter Event 32 With Time",
        (22, 6) => "Counter Event 16 With Time",
        (23, 1) => "Frozen Counter Event 32",
        (23, 2) => "Frozen Counter Event 16",
        (23, 5) => "Frozen Counter Event 32 With Time",
        (23, 6) => "Frozen Counter Event 16 With Time",
        (30, 1) => "Analog Input Int32 With Flags",
        (30, 2) => "Analog Input Int16 With Flags",
        (30, 3) => "Analog Input Int32",
        (30, 4) => "Analog Input Int16",
        (30, 5) => "Analog Input Float32 With Flags",
        (30, 6) => "Analog Input Float64 With Flags",
        (32, 1) => "Analog Input Event Int32",
        (32, 2) => "Analog Input Event Int16",
        (32, 3) => "Analog Input Event Int32 With Time",
        (32, 4) => "Analog Input Event Int16 With Time",
        (32, 5) => "Analog Input Event Float32",
        (32, 6) => "Analog Input Event Float64",
        (32, 7) => "Analog Input Event Float32 With Time",
        (32, 8) => "Analog Input Event Float64 With Time",
        (40, 1) => "Analog Output Status Int32 With Flags",
        (40, 2) => "Analog Output Status Int16 With Flags",
        (40, 3) => "Analog Output Status Float32 With Flags",
        (40, 4) => "Analog Output Status Float64 With Flags",
        (42, 1) => "Analog Output Status Event Int32",
        (42, 2) => "Analog Output Status Event Int16",
        (42, 3) => "Analog Output Status Event Int32 With Time",
        (42, 4) => "Analog Output Status Event Int16 With Time",
        (42, 5) => "Analog Output Status Event Float32",
        (42, 6) => "Analog Output Status Event Float64",
        (42, 7) => "Analog Output Status Event Float32 With Time",
        (42, 8) => "Analog Output Status Event Float64 With Time",
        _ => "Unsupported",
    }
}

fn is_packed(group: u8, variation: u8) -> bool {
    matches!((group, variation), (1, 1) | (3, 1) | (10, 1))
}

fn is_event_group(group: u8) -> bool {
    matches!(group, 2 | 4 | 11 | 22 | 23 | 32 | 42)
}

fn double_state_name(state: u8) -> &'static str {
    match state {
        0 => "intermediate",
        1 => "determined-off",
        2 => "determined-on",
        _ => "indeterminate",
    }
}

fn parse_point_value(
    data: &[u8],
    offset: &mut usize,
    group: u8,
    variation: u8,
    index: u16,
) -> Result<Point, CoreError> {
    let context = format!("g{group}v{variation}");
    let event = is_event_group(group);
    let mut flags = None;
    let mut timestamp_ms = None;
    let mut relative_time_ms = None;

    let value = match (group, variation) {
        (1, 2) | (2, 1) | (2, 2) | (2, 3) | (10, 2) | (11, 1) | (11, 2) => {
            let raw = take_u8(data, offset, &context)?;
            flags = Some(raw);
            if matches!((group, variation), (2, 2) | (11, 2)) {
                timestamp_ms = Some(take_u48(data, offset, &context)?);
            } else if (group, variation) == (2, 3) {
                relative_time_ms = Some(take_u16(data, offset, &context)?);
            }
            PointValue::Binary {
                value: raw & 0x80 != 0,
            }
        }
        (3, 2) | (4, 1) | (4, 2) | (4, 3) => {
            let raw = take_u8(data, offset, &context)?;
            flags = Some(raw);
            if (group, variation) == (4, 2) {
                timestamp_ms = Some(take_u48(data, offset, &context)?);
            } else if (group, variation) == (4, 3) {
                relative_time_ms = Some(take_u16(data, offset, &context)?);
            }
            let state = (raw >> 6) & 0x03;
            PointValue::DoubleBit {
                state,
                state_name: double_state_name(state),
            }
        }
        (20 | 21, 1) => {
            flags = Some(take_u8(data, offset, &context)?);
            PointValue::Unsigned {
                value: u64::from(take_u32(data, offset, &context)?),
            }
        }
        (20 | 21, 2) => {
            flags = Some(take_u8(data, offset, &context)?);
            PointValue::Unsigned {
                value: u64::from(take_u16(data, offset, &context)?),
            }
        }
        (20 | 21, 5) => PointValue::Unsigned {
            value: u64::from(take_u32(data, offset, &context)?),
        },
        (20 | 21, 6) => PointValue::Unsigned {
            value: u64::from(take_u16(data, offset, &context)?),
        },
        (22 | 23, 1 | 5) => {
            flags = Some(take_u8(data, offset, &context)?);
            let value = u64::from(take_u32(data, offset, &context)?);
            if variation == 5 {
                timestamp_ms = Some(take_u48(data, offset, &context)?);
            }
            PointValue::Unsigned { value }
        }
        (22 | 23, 2 | 6) => {
            flags = Some(take_u8(data, offset, &context)?);
            let value = u64::from(take_u16(data, offset, &context)?);
            if variation == 6 {
                timestamp_ms = Some(take_u48(data, offset, &context)?);
            }
            PointValue::Unsigned { value }
        }
        (30, 1) | (40, 1) => {
            flags = Some(take_u8(data, offset, &context)?);
            PointValue::Signed {
                value: i64::from(take_i32(data, offset, &context)?),
            }
        }
        (30, 2) | (40, 2) => {
            flags = Some(take_u8(data, offset, &context)?);
            PointValue::Signed {
                value: i64::from(take_i16(data, offset, &context)?),
            }
        }
        (30, 3) => PointValue::Signed {
            value: i64::from(take_i32(data, offset, &context)?),
        },
        (30, 4) => PointValue::Signed {
            value: i64::from(take_i16(data, offset, &context)?),
        },
        (30, 5) | (40, 3) => {
            flags = Some(take_u8(data, offset, &context)?);
            PointValue::Float {
                value: take_f32(data, offset, &context)?,
            }
        }
        (30, 6) | (40, 4) => {
            flags = Some(take_u8(data, offset, &context)?);
            PointValue::Float {
                value: take_f64(data, offset, &context)?,
            }
        }
        (32 | 42, 1 | 3) => {
            flags = Some(take_u8(data, offset, &context)?);
            let value = i64::from(take_i32(data, offset, &context)?);
            if variation == 3 {
                timestamp_ms = Some(take_u48(data, offset, &context)?);
            }
            PointValue::Signed { value }
        }
        (32 | 42, 2 | 4) => {
            flags = Some(take_u8(data, offset, &context)?);
            let value = i64::from(take_i16(data, offset, &context)?);
            if variation == 4 {
                timestamp_ms = Some(take_u48(data, offset, &context)?);
            }
            PointValue::Signed { value }
        }
        (32 | 42, 5 | 7) => {
            flags = Some(take_u8(data, offset, &context)?);
            let value = take_f32(data, offset, &context)?;
            if variation == 7 {
                timestamp_ms = Some(take_u48(data, offset, &context)?);
            }
            PointValue::Float { value }
        }
        (32 | 42, 6 | 8) => {
            flags = Some(take_u8(data, offset, &context)?);
            let value = take_f64(data, offset, &context)?;
            if variation == 8 {
                timestamp_ms = Some(take_u48(data, offset, &context)?);
            }
            PointValue::Float { value }
        }
        _ => {
            return Err(dnp3_error(
                "DNP3_VARIATION_UNSUPPORTED",
                format!("DNP3 首轮解析器不支持 {context}"),
                Some(serde_json::json!({ "group": group, "variation": variation })),
            ));
        }
    };
    let binary_quality = matches!(group, 1 | 2 | 3 | 4 | 10 | 11);
    Ok(Point {
        group,
        variation,
        variation_name: variation_name(group, variation),
        index,
        event,
        flags,
        quality: flags.map(|raw| quality(raw, binary_quality)),
        timestamp_ms,
        relative_time_ms,
        value,
    })
}

fn push_packed_points(
    data: &[u8],
    offset: &mut usize,
    group: u8,
    variation: u8,
    start: u16,
    count: usize,
    target: &mut Vec<Point>,
) -> Result<(), CoreError> {
    let bits_per_point = if group == 3 { 2 } else { 1 };
    let byte_count = (count * bits_per_point).div_ceil(8);
    require(data, *offset, byte_count, "packed object")?;
    for i in 0..count {
        let index = u16::try_from(usize::from(start) + i)
            .map_err(|_| dnp3_error("DNP3_RANGE_INVALID", "DNP3 packed object 索引溢出", None))?;
        let value = if group == 3 {
            let bit = i * 2;
            let state = (data[*offset + bit / 8] >> (bit % 8)) & 0x03;
            PointValue::DoubleBit {
                state,
                state_name: double_state_name(state),
            }
        } else {
            PointValue::Binary {
                value: data[*offset + i / 8] & (1 << (i % 8)) != 0,
            }
        };
        target.push(Point {
            group,
            variation,
            variation_name: variation_name(group, variation),
            index,
            event: false,
            flags: None,
            quality: None,
            timestamp_ms: None,
            relative_time_ms: None,
            value,
        });
    }
    *offset += byte_count;
    Ok(())
}

fn ensure_point_budget(current: usize, additional: usize) -> Result<(), CoreError> {
    if current.saturating_add(additional) > MAX_RESPONSE_POINTS {
        return Err(dnp3_error(
            "DNP3_POINT_LIMIT_EXCEEDED",
            format!("DNP3 单次响应点数不能超过 {MAX_RESPONSE_POINTS}"),
            Some(serde_json::json!({
                "current": current,
                "additional": additional
            })),
        ));
    }
    Ok(())
}

fn parse_object_header(
    data: &[u8],
    offset: &mut usize,
    target: &mut Vec<Point>,
) -> Result<(), CoreError> {
    require(data, *offset, 3, "object header")?;
    let group = data[*offset];
    let variation = data[*offset + 1];
    let qualifier = data[*offset + 2];
    *offset += 3;
    if variation_name(group, variation) == "Unsupported" {
        return Err(dnp3_error(
            "DNP3_VARIATION_UNSUPPORTED",
            format!("DNP3 首轮解析器不支持 g{group}v{variation}"),
            Some(serde_json::json!({ "group": group, "variation": variation })),
        ));
    }

    match qualifier {
        0x00 | 0x01 => {
            let (start, stop) = if qualifier == 0x00 {
                (
                    u16::from(take_u8(data, offset, "8-bit range start")?),
                    u16::from(take_u8(data, offset, "8-bit range stop")?),
                )
            } else {
                (
                    take_u16(data, offset, "16-bit range start")?,
                    take_u16(data, offset, "16-bit range stop")?,
                )
            };
            if stop < start {
                return Err(dnp3_error(
                    "DNP3_RANGE_INVALID",
                    "DNP3 响应对象范围 stop 小于 start",
                    Some(serde_json::json!({ "start": start, "stop": stop })),
                ));
            }
            let count = usize::from(stop - start) + 1;
            ensure_point_budget(target.len(), count)?;
            if is_packed(group, variation) {
                return push_packed_points(data, offset, group, variation, start, count, target);
            }
            for delta in 0..count {
                let index = u16::try_from(usize::from(start) + delta)
                    .map_err(|_| dnp3_error("DNP3_RANGE_INVALID", "DNP3 对象索引溢出", None))?;
                target.push(parse_point_value(data, offset, group, variation, index)?);
            }
        }
        0x17 | 0x28 => {
            if is_packed(group, variation) {
                return Err(dnp3_error(
                    "DNP3_QUALIFIER_UNSUPPORTED",
                    "DNP3 packed variation 不接受 prefix qualifier",
                    Some(serde_json::json!({ "group": group, "variation": variation })),
                ));
            }
            let count = if qualifier == 0x17 {
                usize::from(take_u8(data, offset, "8-bit prefixed count")?)
            } else {
                usize::from(take_u16(data, offset, "16-bit prefixed count")?)
            };
            if count == 0 {
                return Err(dnp3_error(
                    "DNP3_OBJECT_COUNT_INVALID",
                    "DNP3 prefix 对象数量不能为 0",
                    None,
                ));
            }
            ensure_point_budget(target.len(), count)?;
            for _ in 0..count {
                let index = if qualifier == 0x17 {
                    u16::from(take_u8(data, offset, "8-bit object prefix")?)
                } else {
                    take_u16(data, offset, "16-bit object prefix")?
                };
                target.push(parse_point_value(data, offset, group, variation, index)?);
            }
        }
        _ => {
            return Err(dnp3_error(
                "DNP3_QUALIFIER_UNSUPPORTED",
                format!("DNP3 首轮解析器不支持 qualifier 0x{qualifier:02X}"),
                Some(serde_json::json!({
                    "group": group,
                    "variation": variation,
                    "qualifier": qualifier
                })),
            ));
        }
    }
    Ok(())
}

pub fn parse_application_response(pdu: &[u8]) -> Result<ApplicationResponseFragment, CoreError> {
    if pdu.len() < 4 {
        return Err(dnp3_error(
            "DNP3_APPLICATION_TRUNCATED",
            "DNP3 响应应用片段短于 4 字节",
            Some(serde_json::json!({ "length": pdu.len() })),
        ));
    }
    let control = pdu[0];
    let function = pdu[1];
    let unsolicited = control & 0x10 != 0;
    if !matches!(function, FUNCTION_RESPONSE | FUNCTION_UNSOLICITED_RESPONSE) {
        return Err(dnp3_error(
            "DNP3_FUNCTION_UNEXPECTED",
            format!("DNP3 期望 RESPONSE/UNSOLICITED_RESPONSE，收到 0x{function:02X}"),
            Some(serde_json::json!({ "function": function })),
        ));
    }
    if unsolicited != (function == FUNCTION_UNSOLICITED_RESPONSE) {
        return Err(dnp3_error(
            "DNP3_UNSOLICITED_MISMATCH",
            "DNP3 UNS 控制位与应用功能码不一致",
            Some(serde_json::json!({
                "control": control,
                "function": function
            })),
        ));
    }
    let mut points = Vec::new();
    let mut offset = 4usize;
    while offset < pdu.len() {
        parse_object_header(pdu, &mut offset, &mut points)?;
    }
    Ok(ApplicationResponseFragment {
        sequence: control & 0x0F,
        first: control & 0x80 != 0,
        final_fragment: control & 0x40 != 0,
        confirm_requested: control & 0x20 != 0,
        unsolicited,
        function,
        iin: decode_iin(pdu[2], pdu[3]),
        points,
    })
}

pub fn next_application_sequence(sequence: u8) -> u8 {
    sequence.wrapping_add(1) & 0x0F
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
    fn crc_and_header_only_vector_match_published_reference() {
        let header = [0x05, 0x64, 0x05, 0xC0, 0x01, 0x00, 0x00, 0x04];
        assert_eq!(calculate_crc(&header), 0x21E9);
        let frame = build_link_frame(0xC0, 1, 1024, &[]).unwrap();
        assert_eq!(
            frame,
            [0x05, 0x64, 0x05, 0xC0, 0x01, 0x00, 0x00, 0x04, 0xE9, 0x21]
        );
    }

    #[test]
    fn link_frame_round_trip_covers_multiple_crc_blocks() {
        let data = (0u8..=32).collect::<Vec<_>>();
        let frame = build_link_frame(MASTER_UNCONFIRMED_USER_DATA, 1024, 1, &data).unwrap();
        let parsed = parse_link_frame(&frame).unwrap();
        assert_eq!(parsed.destination, 1024);
        assert_eq!(parsed.source, 1);
        assert_eq!(parsed.user_data, data);
        let mut broken = frame;
        *broken.last_mut().unwrap() ^= 1;
        assert!(parse_link_frame(&broken).is_err());
    }

    #[test]
    fn transport_segments_and_rejects_wrong_sequence() {
        let data = (0..500).map(|value| value as u8).collect::<Vec<_>>();
        let mut sequence = 63;
        let segments = segment_application(&data, &mut sequence).unwrap();
        assert_eq!(segments.len(), 3);
        assert_eq!(segments[0][0], 0xBF);
        assert_eq!(segments[1][0], 0x00);
        assert_eq!(segments[2][0], 0x41);
        assert_eq!(sequence, 2);
        let mut assembler = TransportAssembler::new(1024).unwrap();
        assert!(assembler.add(&segments[0]).unwrap().is_none());
        let mut bad = segments[1].clone();
        bad[0] = 0x01;
        assert!(assembler.add(&bad).is_err());
    }

    #[test]
    fn class_scan_and_range_read_are_read_only_golden_vectors() {
        assert_eq!(
            build_class_scan(0, true, true, true, true).unwrap(),
            [
                0xC0, 0x01, 60, 1, 0x06, 60, 2, 0x06, 60, 3, 0x06, 60, 4, 0x06
            ]
        );
        assert_eq!(
            build_read_request(3, 30, 5, 0x1234, 0x1235).unwrap(),
            [0xC3, 0x01, 30, 5, 0x01, 0x34, 0x12, 0x35, 0x12]
        );
        assert!(build_class_scan(0, false, false, false, false).is_err());
        assert!(build_read_request(0, 12, 1, 0, 0).is_err());
    }

    #[test]
    fn parses_static_binary_counter_and_float_points() {
        let mut response = vec![
            0xC4, 0x81, 0x02, 0x00, // app + IIN
            1, 2, 1, 0, 0, 1, 0, // g1v2 range 0..1
            0x81, 0x01, // true/false, both online
            20, 1, 0x28, 1, 0, 3, 0, 0x01, // g20v1, one prefix index 3
        ];
        response.extend_from_slice(&1234u32.to_le_bytes());
        response.extend_from_slice(&[30, 5, 1, 2, 0, 2, 0, 0x01]);
        response.extend_from_slice(&12.5f32.to_le_bytes());
        let parsed = parse_application_response(&response).unwrap();
        assert_eq!(parsed.sequence, 4);
        assert_eq!(parsed.points.len(), 4);
        assert_eq!(parsed.points[2].index, 3);
        assert_eq!(parsed.points[3].value, PointValue::Float { value: 12.5 });
        assert_eq!(parsed.iin.labels, ["class-1-events"]);
    }

    #[test]
    fn parses_binary_and_analog_events_with_absolute_time() {
        let timestamp = 1_700_000_000_123u64;
        let mut response = vec![
            0xF7, 0x82, 0, 0, // unsolicited + confirm requested
            2, 2, 0x28, 1, 0, 4, 0, 0x81,
        ];
        response.extend_from_slice(&timestamp.to_le_bytes()[..6]);
        response.extend_from_slice(&[32, 7, 0x28, 1, 0, 2, 0, 0x01]);
        response.extend_from_slice(&5.5f32.to_le_bytes());
        response.extend_from_slice(&timestamp.to_le_bytes()[..6]);
        let parsed = parse_application_response(&response).unwrap();
        assert!(parsed.unsolicited);
        assert!(parsed.confirm_requested);
        assert_eq!(parsed.points.len(), 2);
        assert_eq!(parsed.points[0].timestamp_ms, Some(timestamp));
        assert_eq!(parsed.points[1].timestamp_ms, Some(timestamp));
        assert!(parsed.points.iter().all(|point| point.event));
    }

    #[test]
    fn response_parser_fails_closed_on_bad_function_qualifier_and_float() {
        assert!(parse_application_response(&[0xC0, 0x01, 0, 0]).is_err());
        assert!(parse_application_response(&[0xC0, 0x81, 0, 0, 1, 2, 0x06]).is_err());
        let mut nan = vec![0xC0, 0x81, 0, 0, 30, 5, 1, 0, 0, 0, 0, 0x01];
        nan.extend_from_slice(&f32::NAN.to_le_bytes());
        assert!(parse_application_response(&nan).is_err());
    }
}
