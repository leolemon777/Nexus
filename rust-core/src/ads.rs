//! Beckhoff TwinCAT ADS/AMS over TCP codec.
//!
//! This module is intentionally the ADS codec boundary.  The session layer
//! owns TCP I/O, while this module builds and parses AMS/TCP frames; it does
//! not create an AMS route, cache symbol handles, subscribe to notifications,
//! or claim TwinCAT/PLC L2 evidence.  The wire format follows the existing Nexus.Beckhoff client:
//! six-byte AMS/TCP header, 32-byte AMS header, and little-endian ADS data.

use crate::error::CoreError;

pub const AMS_TCP_HEADER_BYTES: usize = 6;
pub const AMS_HEADER_BYTES: usize = 32;
pub const ADS_TCP_PORT: u16 = 48898;
pub const AMS_STATE_REQUEST: u16 = 0x0004;
pub const AMS_STATE_RESPONSE: u16 = 0x0005;

pub const ADS_READ_DEVICE_INFO: u16 = 0x0001;
pub const ADS_READ: u16 = 0x0002;
pub const ADS_WRITE: u16 = 0x0003;
pub const ADS_READ_STATE: u16 = 0x0004;
pub const ADS_WRITE_CONTROL: u16 = 0x0005;
pub const ADS_READ_WRITE: u16 = 0x0009;

pub const ADS_SYMBOL_HANDLE_BY_NAME: u32 = 0xF003;
pub const ADS_SYMBOL_VALUE_BY_HANDLE: u32 = 0xF005;
pub const ADS_SYMBOL_RELEASE_HANDLE: u32 = 0xF006;
pub const AMS_NET_ID_BYTES: usize = 6;
pub const MAX_AMS_PACKET_LENGTH: usize = 1024 * 1024;

fn ads_err(
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
    ads_err("ADS_INVALID", message, details)
}

fn param(message: impl Into<String>, details: serde_json::Value) -> CoreError {
    ads_err("ADS_PARAM_INVALID", message, Some(details))
}

fn put_u16(out: &mut Vec<u8>, value: u16) {
    out.extend_from_slice(&value.to_le_bytes());
}
fn put_u32(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn read_u16(bytes: &[u8], offset: usize) -> Result<u16, CoreError> {
    let pair = bytes
        .get(offset..offset.saturating_add(2))
        .ok_or_else(|| invalid(format!("ADS 帧在 {offset} 处缺少 u16 字段"), None))?;
    Ok(u16::from_le_bytes([pair[0], pair[1]]))
}

fn read_u32(bytes: &[u8], offset: usize) -> Result<u32, CoreError> {
    let word = bytes
        .get(offset..offset.saturating_add(4))
        .ok_or_else(|| invalid(format!("ADS 帧在 {offset} 处缺少 u32 字段"), None))?;
    Ok(u32::from_le_bytes([word[0], word[1], word[2], word[3]]))
}

fn validate_port(port: u16, field: &'static str) -> Result<(), CoreError> {
    if port == 0 {
        return Err(param(
            format!("{field} 不能为 0"),
            serde_json::json!({ "field": field, "value": port }),
        ));
    }
    Ok(())
}

fn validate_net_id(net_id: &[u8; AMS_NET_ID_BYTES], field: &'static str) -> Result<(), CoreError> {
    // A NetId is six octets.  All values are legal on the wire; the route and
    // TwinCAT-specific meaning are intentionally left to the later L2 gate.
    let _ = (net_id, field);
    Ok(())
}

fn build_frame(
    target_net_id: &[u8; AMS_NET_ID_BYTES],
    target_port: u16,
    source_net_id: &[u8; AMS_NET_ID_BYTES],
    source_port: u16,
    command: u16,
    state_flags: u16,
    invoke_id: u32,
    payload: &[u8],
) -> Result<Vec<u8>, CoreError> {
    validate_net_id(target_net_id, "targetNetId")?;
    validate_net_id(source_net_id, "sourceNetId")?;
    validate_port(target_port, "targetPort")?;
    validate_port(source_port, "sourcePort")?;
    if payload.len() > MAX_AMS_PACKET_LENGTH.saturating_sub(AMS_HEADER_BYTES) {
        return Err(param(
            format!(
                "ADS 数据不能超过 {} 字节",
                MAX_AMS_PACKET_LENGTH - AMS_HEADER_BYTES
            ),
            serde_json::json!({ "bytes": payload.len(), "maximum": MAX_AMS_PACKET_LENGTH - AMS_HEADER_BYTES }),
        ));
    }
    let mut ams = Vec::with_capacity(AMS_HEADER_BYTES + payload.len());
    ams.extend_from_slice(target_net_id);
    put_u16(&mut ams, target_port);
    ams.extend_from_slice(source_net_id);
    put_u16(&mut ams, source_port);
    put_u16(&mut ams, command);
    put_u16(&mut ams, state_flags);
    put_u32(&mut ams, payload.len() as u32);
    put_u32(&mut ams, 0); // AMS router error in a request
    put_u32(&mut ams, invoke_id);
    ams.extend_from_slice(payload);

    let mut frame = Vec::with_capacity(AMS_TCP_HEADER_BYTES + ams.len());
    frame.extend_from_slice(&[0, 0]); // AMS/TCP reserved field
    put_u32(&mut frame, ams.len() as u32);
    frame.extend_from_slice(&ams);
    Ok(frame)
}

fn build_read_payload(index_group: u32, index_offset: u32, read_length: u32) -> Vec<u8> {
    let mut payload = Vec::with_capacity(12);
    put_u32(&mut payload, index_group);
    put_u32(&mut payload, index_offset);
    put_u32(&mut payload, read_length);
    payload
}

/// Build an ADS Read request (IndexGroup/IndexOffset/raw byte length).
pub fn build_read(
    target_net_id: &[u8; AMS_NET_ID_BYTES],
    target_port: u16,
    source_net_id: &[u8; AMS_NET_ID_BYTES],
    source_port: u16,
    invoke_id: u32,
    index_group: u32,
    index_offset: u32,
    read_length: u32,
) -> Result<Vec<u8>, CoreError> {
    build_frame(
        target_net_id,
        target_port,
        source_net_id,
        source_port,
        ADS_READ,
        AMS_STATE_REQUEST,
        invoke_id,
        &build_read_payload(index_group, index_offset, read_length),
    )
}

/// Build an ADS Write request (IndexGroup/IndexOffset/raw bytes).
pub fn build_write(
    target_net_id: &[u8; AMS_NET_ID_BYTES],
    target_port: u16,
    source_net_id: &[u8; AMS_NET_ID_BYTES],
    source_port: u16,
    invoke_id: u32,
    index_group: u32,
    index_offset: u32,
    data: &[u8],
) -> Result<Vec<u8>, CoreError> {
    let mut payload = build_read_payload(index_group, index_offset, data.len() as u32);
    payload.extend_from_slice(data);
    build_frame(
        target_net_id,
        target_port,
        source_net_id,
        source_port,
        ADS_WRITE,
        AMS_STATE_REQUEST,
        invoke_id,
        &payload,
    )
}

/// Build an ADS ReadWrite request (read length plus write bytes).
pub fn build_read_write(
    target_net_id: &[u8; AMS_NET_ID_BYTES],
    target_port: u16,
    source_net_id: &[u8; AMS_NET_ID_BYTES],
    source_port: u16,
    invoke_id: u32,
    index_group: u32,
    index_offset: u32,
    read_length: u32,
    write_data: &[u8],
) -> Result<Vec<u8>, CoreError> {
    let mut payload = Vec::with_capacity(16 + write_data.len());
    put_u32(&mut payload, index_group);
    put_u32(&mut payload, index_offset);
    put_u32(&mut payload, read_length);
    put_u32(&mut payload, write_data.len() as u32);
    payload.extend_from_slice(write_data);
    build_frame(
        target_net_id,
        target_port,
        source_net_id,
        source_port,
        ADS_READ_WRITE,
        AMS_STATE_REQUEST,
        invoke_id,
        &payload,
    )
}

pub fn build_read_device_info(
    target_net_id: &[u8; AMS_NET_ID_BYTES],
    target_port: u16,
    source_net_id: &[u8; AMS_NET_ID_BYTES],
    source_port: u16,
    invoke_id: u32,
) -> Result<Vec<u8>, CoreError> {
    build_frame(
        target_net_id,
        target_port,
        source_net_id,
        source_port,
        ADS_READ_DEVICE_INFO,
        AMS_STATE_REQUEST,
        invoke_id,
        &[],
    )
}

pub fn build_read_state(
    target_net_id: &[u8; AMS_NET_ID_BYTES],
    target_port: u16,
    source_net_id: &[u8; AMS_NET_ID_BYTES],
    source_port: u16,
    invoke_id: u32,
) -> Result<Vec<u8>, CoreError> {
    build_frame(
        target_net_id,
        target_port,
        source_net_id,
        source_port,
        ADS_READ_STATE,
        AMS_STATE_REQUEST,
        invoke_id,
        &[],
    )
}

/// Parsed AMS/TCP + AMS header and its exact ADS payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdsFrame {
    pub target_net_id: [u8; AMS_NET_ID_BYTES],
    pub target_port: u16,
    pub source_net_id: [u8; AMS_NET_ID_BYTES],
    pub source_port: u16,
    pub command: u16,
    pub state_flags: u16,
    pub data_length: u32,
    pub ams_error: u32,
    pub invoke_id: u32,
    pub payload: Vec<u8>,
}

/// Parsed ADS result.  `ads_result` is kept as a value rather than converted
/// into a transport failure so the UI can explain a PLC-side error code.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdsResponse {
    pub frame: AdsFrame,
    pub ads_result: u32,
    pub data: Vec<u8>,
    pub declared_data_length: Option<u32>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct EndpointExpectation {
    pub net_id: Option<[u8; AMS_NET_ID_BYTES]>,
    pub port: Option<u16>,
}

pub fn parse_frame(frame: &[u8]) -> Result<AdsFrame, CoreError> {
    if frame.len() < AMS_TCP_HEADER_BYTES + AMS_HEADER_BYTES {
        return Err(invalid(
            format!(
                "ADS 帧至少需要 {} 字节，实际 {}",
                AMS_TCP_HEADER_BYTES + AMS_HEADER_BYTES,
                frame.len()
            ),
            Some(serde_json::json!({ "length": frame.len() })),
        ));
    }
    if frame[0] != 0 || frame[1] != 0 {
        return Err(invalid("AMS/TCP 保留字段必须为 0", None));
    }
    let packet_length = read_u32(frame, 2)? as usize;
    if !(AMS_HEADER_BYTES..=MAX_AMS_PACKET_LENGTH).contains(&packet_length) {
        return Err(invalid(
            format!(
                "AMS/TCP 长度必须在 {}..{} 字节，收到 {}",
                AMS_HEADER_BYTES, MAX_AMS_PACKET_LENGTH, packet_length
            ),
            Some(serde_json::json!({ "length": packet_length, "maximum": MAX_AMS_PACKET_LENGTH })),
        ));
    }
    let expected = AMS_TCP_HEADER_BYTES
        .checked_add(packet_length)
        .ok_or_else(|| invalid("ADS 帧长度溢出", None))?;
    if frame.len() != expected {
        return Err(invalid(
            format!(
                "AMS/TCP 长度不匹配:声明 {}B，实际 {}B",
                packet_length,
                frame.len().saturating_sub(AMS_TCP_HEADER_BYTES)
            ),
            Some(
                serde_json::json!({ "declared": packet_length, "actual": frame.len().saturating_sub(AMS_TCP_HEADER_BYTES) }),
            ),
        ));
    }
    let data_length = read_u32(frame, AMS_TCP_HEADER_BYTES + 20)? as usize;
    let actual_data = packet_length - AMS_HEADER_BYTES;
    if data_length != actual_data {
        return Err(invalid(
            format!(
                "AMS DataLength 不一致:声明 {}B，实际 {}B",
                data_length, actual_data
            ),
            Some(serde_json::json!({ "declared": data_length, "actual": actual_data })),
        ));
    }
    let base = AMS_TCP_HEADER_BYTES;
    let mut target_net_id = [0u8; AMS_NET_ID_BYTES];
    let mut source_net_id = [0u8; AMS_NET_ID_BYTES];
    target_net_id.copy_from_slice(&frame[base..base + 6]);
    source_net_id.copy_from_slice(&frame[base + 8..base + 14]);
    Ok(AdsFrame {
        target_net_id,
        target_port: read_u16(frame, base + 6)?,
        source_net_id,
        source_port: read_u16(frame, base + 14)?,
        command: read_u16(frame, base + 16)?,
        state_flags: read_u16(frame, base + 18)?,
        data_length: data_length as u32,
        ams_error: read_u32(frame, base + 24)?,
        invoke_id: read_u32(frame, base + 28)?,
        payload: frame[base + AMS_HEADER_BYTES..].to_vec(),
    })
}

pub fn parse_response(
    bytes: &[u8],
    expected_invoke_id: Option<u32>,
    expected_command: Option<u16>,
) -> Result<AdsResponse, CoreError> {
    parse_response_with_endpoints(bytes, expected_invoke_id, expected_command, None, None)
}

pub fn parse_response_with_endpoints(
    bytes: &[u8],
    expected_invoke_id: Option<u32>,
    expected_command: Option<u16>,
    expected_target: Option<&EndpointExpectation>,
    expected_source: Option<&EndpointExpectation>,
) -> Result<AdsResponse, CoreError> {
    let frame = parse_frame(bytes)?;
    if frame.state_flags != AMS_STATE_RESPONSE {
        return Err(invalid(
            format!(
                "AMS 响应 StateFlags 必须为 0x{AMS_STATE_RESPONSE:04X}，收到 0x{:04X}",
                frame.state_flags
            ),
            Some(serde_json::json!({ "stateFlags": frame.state_flags })),
        ));
    }
    if let Some(expected) = expected_command {
        if frame.command != expected {
            return Err(invalid(
                format!(
                    "AMS CommandId 不匹配:期望 0x{expected:04X}，收到 0x{:04X}",
                    frame.command
                ),
                Some(serde_json::json!({ "expected": expected, "actual": frame.command })),
            ));
        }
    }
    if let Some(expected) = expected_invoke_id {
        if frame.invoke_id != expected {
            return Err(invalid(
                format!(
                    "AMS InvokeId 不匹配:期望 {expected}，收到 {}",
                    frame.invoke_id
                ),
                Some(serde_json::json!({ "expected": expected, "actual": frame.invoke_id })),
            ));
        }
    }
    for (label, endpoint, actual_net_id, actual_port) in [
        (
            "目标",
            expected_target,
            &frame.target_net_id,
            frame.target_port,
        ),
        (
            "源",
            expected_source,
            &frame.source_net_id,
            frame.source_port,
        ),
    ] {
        if let Some(expected) = endpoint {
            if let Some(net_id) = expected.net_id {
                if actual_net_id != &net_id {
                    return Err(invalid(
                        format!(
                            "AMS 响应{label} NetId 不匹配:期望 {}，收到 {}",
                            net_id_string(&net_id),
                            net_id_string(actual_net_id)
                        ),
                        Some(
                            serde_json::json!({ "endpoint": label, "expectedNetId": net_id_string(&net_id), "actualNetId": net_id_string(actual_net_id) }),
                        ),
                    ));
                }
            }
            if let Some(port) = expected.port {
                if actual_port != port {
                    return Err(invalid(
                        format!("AMS 响应{label} Port 不匹配:期望 {port}，收到 {actual_port}"),
                        Some(
                            serde_json::json!({ "endpoint": label, "expectedPort": port, "actualPort": actual_port }),
                        ),
                    ));
                }
            }
        }
    }
    if frame.ams_error != 0 {
        return Err(ads_err(
            "ADS_AMS_ROUTER_ERROR",
            format!(
                "AMS 路由错误: 0x{:08X} ({})",
                frame.ams_error,
                ams_error_message(frame.ams_error)
            ),
            Some(serde_json::json!({ "amsError": frame.ams_error })),
        ));
    }

    let (ads_result, data, declared_data_length) = match frame.command {
        ADS_READ | ADS_READ_WRITE => {
            if frame.payload.len() < 8 {
                return Err(invalid("ADS Read/ReadWrite 响应至少需要 8 字节", None));
            }
            let result = read_u32(&frame.payload, 0)?;
            let declared = read_u32(&frame.payload, 4)? as usize;
            if declared != frame.payload.len() - 8 {
                return Err(invalid(
                    format!(
                        "ADS 返回数据长度不一致:声明 {}B，实际 {}B",
                        declared,
                        frame.payload.len() - 8
                    ),
                    Some(
                        serde_json::json!({ "declared": declared, "actual": frame.payload.len() - 8 }),
                    ),
                ));
            }
            (result, frame.payload[8..].to_vec(), Some(declared as u32))
        }
        ADS_WRITE | ADS_WRITE_CONTROL => {
            if frame.payload.len() != 4 {
                return Err(invalid(
                    format!("ADS Write 响应长度应为 4，实际 {}", frame.payload.len()),
                    Some(serde_json::json!({ "actual": frame.payload.len() })),
                ));
            }
            (read_u32(&frame.payload, 0)?, Vec::new(), None)
        }
        ADS_READ_DEVICE_INFO => {
            if frame.payload.len() != 24 {
                return Err(invalid(
                    format!(
                        "ADS ReadDeviceInfo 响应长度应为 24，实际 {}",
                        frame.payload.len()
                    ),
                    Some(serde_json::json!({ "actual": frame.payload.len() })),
                ));
            }
            (
                read_u32(&frame.payload, 0)?,
                frame.payload[4..].to_vec(),
                None,
            )
        }
        ADS_READ_STATE => {
            if frame.payload.len() != 8 {
                return Err(invalid(
                    format!("ADS ReadState 响应长度应为 8，实际 {}", frame.payload.len()),
                    Some(serde_json::json!({ "actual": frame.payload.len() })),
                ));
            }
            (
                read_u32(&frame.payload, 0)?,
                frame.payload[4..].to_vec(),
                None,
            )
        }
        _ => (0, frame.payload.clone(), None),
    };
    Ok(AdsResponse {
        frame,
        ads_result,
        data,
        declared_data_length,
    })
}

pub fn parse_device_info(response: &AdsResponse) -> Result<(u8, u8, u16, String), CoreError> {
    if response.frame.command != ADS_READ_DEVICE_INFO || response.data.len() != 20 {
        return Err(invalid("不是有效的 ADS ReadDeviceInfo 结果", None));
    }
    let mut name = response.data[4..20].to_vec();
    while name.last() == Some(&0) {
        name.pop();
    }
    let device_name = String::from_utf8_lossy(&name).to_string();
    Ok((
        response.data[0],
        response.data[1],
        u16::from_le_bytes([response.data[2], response.data[3]]),
        device_name,
    ))
}

pub fn parse_state(response: &AdsResponse) -> Result<(u16, u16), CoreError> {
    if response.frame.command != ADS_READ_STATE || response.data.len() != 4 {
        return Err(invalid("不是有效的 ADS ReadState 结果", None));
    }
    Ok((
        u16::from_le_bytes([response.data[0], response.data[1]]),
        u16::from_le_bytes([response.data[2], response.data[3]]),
    ))
}

pub fn parse_net_id(value: &str) -> Result<[u8; AMS_NET_ID_BYTES], CoreError> {
    let trimmed = value.trim();
    let parts: Vec<&str> = trimmed.split('.').collect();
    if parts.len() != AMS_NET_ID_BYTES {
        return Err(param(
            "AMS NetId 必须包含 6 个十进制字节，例如 5.72.144.1.1.1",
            serde_json::json!({ "netId": value }),
        ));
    }
    let mut result = [0u8; AMS_NET_ID_BYTES];
    for (index, part) in parts.iter().enumerate() {
        result[index] = part.parse::<u8>().map_err(|_| {
            param(
                "AMS NetId 每段必须是 0..255 的十进制字节",
                serde_json::json!({ "netId": value, "segment": part }),
            )
        })?;
    }
    Ok(result)
}

pub fn net_id_string(net_id: &[u8; AMS_NET_ID_BYTES]) -> String {
    net_id
        .iter()
        .map(u8::to_string)
        .collect::<Vec<_>>()
        .join(".")
}

pub fn frame_hex(frame: &[u8]) -> String {
    frame.iter().map(|byte| format!("{byte:02X}")).collect()
}

pub fn ads_error_message(code: u32) -> &'static str {
    match code {
        0x0000 => "成功",
        0x0001 => "内部错误",
        0x0006 => "目标 ADS 端口不存在",
        0x0700 => "设备错误",
        0x0701 => "服务不支持",
        0x0702 => "无效 IndexGroup",
        0x0703 => "无效 IndexOffset",
        0x0704 => "访问被拒绝",
        0x0705 => "无效参数大小",
        0x0706 => "无效参数数据",
        0x0707 => "设备未就绪",
        0x0708 => "设备忙",
        0x070A => "内存不足",
        0x070B => "无效参数值",
        0x070C => "对象未找到",
        0x0710 => "符号未找到",
        0x0711 => "符号版本无效",
        0x0712 => "设备状态无效",
        0x0713 => "通知模式不支持",
        0x0714 => "通知句柄无效",
        0x0715 => "通知客户端未注册",
        0x0716 => "没有更多句柄",
        0x0717 => "通知大小过大",
        0x0718 => "设备未初始化",
        0x0719 => "设备超时",
        0x0745 => "客户端超时",
        _ => "未知 ADS 错误",
    }
}

pub fn ams_error_message(code: u32) -> &'static str {
    match code {
        0x0000 => "成功",
        0x0001 => "无效 AMS 命令",
        0x0002 => "目标端口未注册",
        0x0003 => "目标计算机未找到",
        0x0004 => "目标计算机不可达",
        0x0005 => "目标路由未找到",
        _ => "未知 AMS 路由错误",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ids() -> ([u8; 6], [u8; 6]) {
        ([5, 72, 144, 1, 1, 1], [5, 72, 144, 2, 1, 1])
    }

    #[test]
    fn read_request_matches_ams_tcp_and_ams_headers() {
        let (target, source) = ids();
        let frame = build_read(&target, 851, &source, 32905, 7, 0xF000, 0x20, 4).unwrap();
        assert_eq!(frame.len(), 50);
        assert_eq!(&frame[0..6], &[0, 0, 44, 0, 0, 0]);
        assert_eq!(&frame[6..12], &target);
        assert_eq!(u16::from_le_bytes([frame[12], frame[13]]), 851);
        assert_eq!(u16::from_le_bytes([frame[22], frame[23]]), ADS_READ);
        assert_eq!(
            u16::from_le_bytes([frame[24], frame[25]]),
            AMS_STATE_REQUEST
        );
        let parsed = parse_frame(&frame).unwrap();
        assert_eq!(parsed.command, ADS_READ);
        assert_eq!(parsed.invoke_id, 7);
        assert_eq!(
            &parsed.payload[0..12],
            &[0, 240, 0, 0, 32, 0, 0, 0, 4, 0, 0, 0]
        );
    }

    #[test]
    fn read_response_parses_result_and_data() {
        let (target, source) = ids();
        let mut payload = Vec::new();
        put_u32(&mut payload, 0);
        put_u32(&mut payload, 4);
        payload.extend_from_slice(&[0x11, 0x22, 0x33, 0x44]);
        let response = build_frame(
            &source,
            32905,
            &target,
            851,
            ADS_READ,
            AMS_STATE_RESPONSE,
            7,
            &payload,
        )
        .unwrap();
        let parsed = parse_response(&response, Some(7), Some(ADS_READ)).unwrap();
        assert_eq!(parsed.ads_result, 0);
        assert_eq!(parsed.data, vec![0x11, 0x22, 0x33, 0x44]);
        assert_eq!(parsed.declared_data_length, Some(4));
    }

    #[test]
    fn malformed_length_and_net_id_fail_closed() {
        let (target, source) = ids();
        let mut frame = build_read(&target, 851, &source, 32905, 1, 0, 0, 1).unwrap();
        frame[2] = 0;
        assert!(parse_frame(&frame).is_err());
        assert!(parse_net_id("5.72.144.1.1").is_err());
        assert!(parse_net_id("5.72.144.1.1.300").is_err());
    }
}
