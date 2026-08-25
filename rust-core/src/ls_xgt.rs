//! LS Electric XGT FEnet binary codec.
//!
//! The software-first boundary follows the retained Nexus.LsElectric client:
//! a 20-byte FEnet application header plus XGT Dedicated service payloads.
//! The session layer adds an explicitly read-only TCP boundary around this
//! codec; this module itself does not claim XGK/XGI hardware, PLC control, or
//! L2 evidence.

use crate::error::CoreError;

pub const HEADER_BYTES: usize = 20;
pub const DEFAULT_PORT: u16 = 2004;
pub const READ_REQUEST: u8 = 0x54;
pub const READ_RESPONSE: u8 = 0x55;
pub const WRITE_REQUEST: u8 = 0x58;
pub const WRITE_RESPONSE: u8 = 0x59;
pub const INDIVIDUAL_BIT: u8 = 0x00;
pub const INDIVIDUAL_BYTE: u8 = 0x01;
pub const INDIVIDUAL_WORD: u8 = 0x02;
pub const INDIVIDUAL_DWORD: u8 = 0x03;
pub const INDIVIDUAL_LWORD: u8 = 0x04;
pub const CONTINUOUS: u8 = 0x14;
pub const CPU_XGK: u8 = 0xA0;
pub const CPU_XGI: u8 = 0xA4;
pub const CPU_XGR: u8 = 0xA8;
pub const CPU_XGB_MK: u8 = 0xB0;
pub const CPU_XGB_IEC: u8 = 0xB4;

fn xgt_err(
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
    xgt_err("LS_XGT_INVALID", message, details)
}

fn param(message: impl Into<String>, details: serde_json::Value) -> CoreError {
    xgt_err("LS_XGT_PARAM_INVALID", message, Some(details))
}

fn put_u16(out: &mut Vec<u8>, value: u16) {
    out.extend_from_slice(&value.to_le_bytes());
}
fn read_u16(bytes: &[u8], offset: usize) -> Result<u16, CoreError> {
    let pair = bytes
        .get(offset..offset.saturating_add(2))
        .ok_or_else(|| invalid(format!("XGT 在 {offset} 处缺少 u16"), None))?;
    Ok(u16::from_le_bytes([pair[0], pair[1]]))
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct XgtAddress {
    pub area: char,
    pub data_type: u8,
    pub offset: u32,
    pub variable_name: String,
}

pub fn parse_address(address: &str) -> Result<XgtAddress, CoreError> {
    let mut value = address.trim().to_ascii_uppercase();
    if value.starts_with('%') {
        value.remove(0);
    }
    if value.len() < 3 || value.chars().any(char::is_control) {
        return Err(param(
            "LS XGT 地址必须是 %DW100 / MW100 / MX10 等显式类型",
            serde_json::json!({ "address": address }),
        ));
    }
    let mut chars = value.chars();
    let area = chars.next().unwrap();
    if !"PMLKFTCDSQINUZR".contains(area) {
        return Err(param(
            "LS XGT 地址区不支持",
            serde_json::json!({ "address": address, "area": area.to_string() }),
        ));
    }
    let type_char = chars.next().unwrap();
    let data_type = match type_char {
        'X' => INDIVIDUAL_BIT,
        'B' => INDIVIDUAL_BYTE,
        'W' => INDIVIDUAL_WORD,
        'D' => INDIVIDUAL_DWORD,
        'L' => INDIVIDUAL_LWORD,
        _ => {
            return Err(param(
                "LS XGT 地址必须包含 X/B/W/D/L 类型",
                serde_json::json!({ "address": address }),
            ));
        }
    };
    let offset_text: String = chars.collect();
    let offset = offset_text.parse::<u32>().map_err(|_| {
        param(
            "LS XGT 地址偏移必须是 0..4294967295",
            serde_json::json!({ "address": address }),
        )
    })?;
    Ok(XgtAddress {
        area,
        data_type,
        offset,
        variable_name: format!("%{area}{type_char}{offset}"),
    })
}

fn validate_type(data_type: u8) -> Result<(), CoreError> {
    if data_type > INDIVIDUAL_LWORD {
        return Err(param(
            "LS XGT 单变量类型必须是 X/B/W/D/L",
            serde_json::json!({ "dataType": data_type }),
        ));
    }
    Ok(())
}

fn variable_bytes(variable_name: &str, data_type: Option<u8>) -> Result<Vec<u8>, CoreError> {
    let parsed = parse_address(variable_name)?;
    if let Some(expected) = data_type {
        validate_type(expected)?;
        if parsed.data_type != expected {
            return Err(param(
                "LS XGT 地址类型与操作 dataType 不匹配",
                serde_json::json!({ "addressType": parsed.data_type, "dataType": expected }),
            ));
        }
    }
    let bytes = parsed.variable_name.into_bytes();
    if bytes.len() > u8::MAX as usize {
        return Err(param(
            "LS XGT 变量名不能超过 255 字节",
            serde_json::json!({ "bytes": bytes.len() }),
        ));
    }
    Ok(bytes)
}

fn build_frame(
    application: &[u8],
    invoke_id: u16,
    cpu: u8,
    base_no: u8,
    slot_no: u8,
    company_id: &str,
) -> Result<Vec<u8>, CoreError> {
    if application.len() > u16::MAX as usize {
        return Err(param(
            "XGT application payload 不能超过 65535 字节",
            serde_json::json!({ "bytes": application.len() }),
        ));
    }
    if base_no > 15 || slot_no > 15 {
        return Err(param(
            "XGT baseNo/slotNo 必须是 0..15",
            serde_json::json!({ "baseNo": base_no, "slotNo": slot_no }),
        ));
    }
    if company_id.is_empty() || !company_id.is_ascii() || company_id.len() > 10 {
        return Err(param(
            "XGT companyId 必须是 1..10 字节 ASCII",
            serde_json::json!({ "companyId": company_id }),
        ));
    }
    let mut frame = vec![0u8; HEADER_BYTES + application.len()];
    frame[..company_id.len()].copy_from_slice(company_id.as_bytes());
    frame[12] = cpu;
    frame[13] = 0x33;
    frame[14..16].copy_from_slice(&invoke_id.to_le_bytes());
    frame[16..18].copy_from_slice(&(application.len() as u16).to_le_bytes());
    frame[18] = (base_no << 4) | slot_no;
    frame[19] = calculate_header_checksum(&frame);
    frame[HEADER_BYTES..].copy_from_slice(application);
    Ok(frame)
}

pub fn calculate_header_checksum(frame: &[u8]) -> u8 {
    frame
        .iter()
        .take(19)
        .fold(0u8, |sum, byte| sum.wrapping_add(*byte))
}

pub fn build_individual_read(
    variable_name: &str,
    data_type: u8,
    invoke_id: u16,
    cpu: u8,
    base_no: u8,
    slot_no: u8,
    company_id: &str,
) -> Result<Vec<u8>, CoreError> {
    validate_type(data_type)?;
    let variable = variable_bytes(variable_name, Some(data_type))?;
    let mut application = vec![READ_REQUEST, 0, data_type, 0, 0, 0, 1, 0];
    put_u16(&mut application, variable.len() as u16);
    application.extend_from_slice(&variable);
    build_frame(&application, invoke_id, cpu, base_no, slot_no, company_id)
}

pub fn build_continuous_read(
    variable_name: &str,
    byte_count: u16,
    invoke_id: u16,
    cpu: u8,
    base_no: u8,
    slot_no: u8,
    company_id: &str,
) -> Result<Vec<u8>, CoreError> {
    if byte_count == 0 {
        return Err(param(
            "XGT 连续读取字节数不能为 0",
            serde_json::json!({ "byteCount": byte_count }),
        ));
    }
    let variable = variable_bytes(variable_name, None)?;
    let mut application = vec![READ_REQUEST, 0, CONTINUOUS, 0, 0, 0, 1, 0];
    put_u16(&mut application, variable.len() as u16);
    application.extend_from_slice(&variable);
    put_u16(&mut application, byte_count);
    build_frame(&application, invoke_id, cpu, base_no, slot_no, company_id)
}

pub fn build_individual_write(
    variable_name: &str,
    data_type: u8,
    value: &[u8],
    invoke_id: u16,
    cpu: u8,
    base_no: u8,
    slot_no: u8,
    company_id: &str,
) -> Result<Vec<u8>, CoreError> {
    validate_type(data_type)?;
    if value.is_empty() {
        return Err(param(
            "XGT 写入数据不能为空",
            serde_json::json!({ "bytes": value.len() }),
        ));
    }
    let variable = variable_bytes(variable_name, Some(data_type))?;
    let mut application = vec![WRITE_REQUEST, 0, data_type, 0, 0, 0, 1, 0];
    put_u16(&mut application, variable.len() as u16);
    application.extend_from_slice(&variable);
    put_u16(&mut application, value.len() as u16);
    application.extend_from_slice(value);
    build_frame(&application, invoke_id, cpu, base_no, slot_no, company_id)
}

pub fn build_continuous_write(
    variable_name: &str,
    value: &[u8],
    invoke_id: u16,
    cpu: u8,
    base_no: u8,
    slot_no: u8,
    company_id: &str,
) -> Result<Vec<u8>, CoreError> {
    if value.is_empty() {
        return Err(param(
            "XGT 连续写入数据不能为空",
            serde_json::json!({ "bytes": value.len() }),
        ));
    }
    let variable = variable_bytes(variable_name, None)?;
    let mut application = vec![WRITE_REQUEST, 0, CONTINUOUS, 0, 0, 0, 1, 0];
    put_u16(&mut application, variable.len() as u16);
    application.extend_from_slice(&variable);
    put_u16(&mut application, value.len() as u16);
    application.extend_from_slice(value);
    build_frame(&application, invoke_id, cpu, base_no, slot_no, company_id)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct XgtResponse {
    pub company_id: String,
    pub cpu: u8,
    pub source: u8,
    pub invoke_id: u16,
    pub application_length: u16,
    pub base_no: u8,
    pub slot_no: u8,
    pub command: u8,
    pub data_type: u8,
    pub error_status: u16,
    pub block_count: Option<u16>,
    pub data_length: Option<u16>,
    pub data: Vec<u8>,
}

pub fn parse_response(
    response: &[u8],
    expected_invoke_id: Option<u16>,
) -> Result<XgtResponse, CoreError> {
    if response.len() < HEADER_BYTES + 8 {
        return Err(invalid(
            format!(
                "XGT 响应至少需要 {} 字节，实际 {}",
                HEADER_BYTES + 8,
                response.len()
            ),
            None,
        ));
    }
    let company_id = String::from_utf8_lossy(&response[..10])
        .trim_end_matches('\0')
        .to_string();
    if company_id != "LSIS-XGT" && company_id != "LGIS-GLOFA" {
        return Err(invalid(
            format!("XGT Company ID 无效: {company_id}"),
            Some(serde_json::json!({ "companyId": company_id })),
        ));
    }
    if response[13] != 0x11 {
        return Err(invalid(
            format!("XGT 响应 source 必须为 0x11，收到 0x{:02X}", response[13]),
            None,
        ));
    }
    if calculate_header_checksum(response) != response[19] {
        return Err(invalid("XGT FEnet header checksum 不匹配", None));
    }
    let invoke_id = read_u16(response, 14)?;
    if let Some(expected) = expected_invoke_id {
        if expected != invoke_id {
            return Err(invalid(
                format!("XGT InvokeId 不匹配:期望 {expected}，收到 {invoke_id}"),
                Some(serde_json::json!({ "expected": expected, "actual": invoke_id })),
            ));
        }
    }
    let application_length = read_u16(response, 16)? as usize;
    if application_length != response.len() - HEADER_BYTES {
        return Err(invalid(
            format!(
                "XGT application length 不匹配:声明 {application_length}，实际 {}",
                response.len() - HEADER_BYTES
            ),
            None,
        ));
    }
    let command = response[20];
    let data_type = response[22];
    if response[21] != 0 || (command != READ_RESPONSE && command != WRITE_RESPONSE) {
        return Err(invalid(
            format!(
                "XGT 响应 command/type 不支持: 0x{command:02X}{:02X}",
                response[21]
            ),
            None,
        ));
    }
    let error_status = read_u16(response, 26)?;
    if error_status != 0 {
        return Err(xgt_err(
            "LS_XGT_DEVICE_ERROR",
            format!("XGT PLC error 0x{error_status:04X}"),
            Some(serde_json::json!({ "errorStatus": error_status })),
        ));
    }
    let (block_count, data_length, data) = if command == WRITE_RESPONSE {
        if response.len() != HEADER_BYTES + 8 {
            return Err(invalid("XGT Write 响应必须是 28 字节", None));
        }
        (None, None, Vec::new())
    } else {
        if response.len() < HEADER_BYTES + 12 {
            return Err(invalid("XGT Read 响应缺少数据长度", None));
        }
        let blocks = read_u16(response, 28)?;
        if blocks != 1 {
            return Err(invalid(
                format!("XGT Read block count 不支持: {blocks}"),
                Some(serde_json::json!({ "blockCount": blocks })),
            ));
        }
        let length = read_u16(response, 30)? as usize;
        if response.len() != HEADER_BYTES + 12 + length {
            return Err(invalid(
                format!(
                    "XGT 数据长度不匹配:声明 {length}，实际 {}",
                    response.len().saturating_sub(32)
                ),
                None,
            ));
        }
        (Some(blocks), Some(length as u16), response[32..].to_vec())
    };
    Ok(XgtResponse {
        company_id,
        cpu: response[12],
        source: response[13],
        invoke_id,
        application_length: application_length as u16,
        base_no: response[18] >> 4,
        slot_no: response[18] & 0x0F,
        command,
        data_type,
        error_status,
        block_count,
        data_length,
        data,
    })
}

pub fn frame_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02X}")).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn xgt_read_request_has_20_byte_header_and_typed_variable() {
        let frame = build_individual_read("%DD100", INDIVIDUAL_DWORD, 7, CPU_XGK, 0, 3, "LSIS-XGT")
            .unwrap();
        assert_eq!(frame.len(), 36);
        assert_eq!(&frame[..8], b"LSIS-XGT");
        assert_eq!(
            &frame[20..],
            &[
                READ_REQUEST,
                0,
                INDIVIDUAL_DWORD,
                0,
                0,
                0,
                1,
                0,
                6,
                0,
                b'%',
                b'D',
                b'D',
                b'1',
                b'0',
                b'0'
            ]
        );
        assert_eq!(frame[19], calculate_header_checksum(&frame));
    }

    #[test]
    fn xgt_read_response_parses_data_and_rejects_bad_invoke() {
        let mut response = vec![0u8; 32 + 4];
        response[..8].copy_from_slice(b"LSIS-XGT");
        response[12] = CPU_XGK;
        response[13] = 0x11;
        response[14..16].copy_from_slice(&7u16.to_le_bytes());
        response[16..18].copy_from_slice(&16u16.to_le_bytes());
        response[18] = 3;
        response[20] = READ_RESPONSE;
        response[22] = INDIVIDUAL_WORD;
        response[28..30].copy_from_slice(&1u16.to_le_bytes());
        response[30..32].copy_from_slice(&4u16.to_le_bytes());
        response[32..].copy_from_slice(&[1, 2, 3, 4]);
        response[19] = calculate_header_checksum(&response);
        let parsed = parse_response(&response, Some(7)).unwrap();
        assert_eq!(parsed.data, vec![1, 2, 3, 4]);
        assert!(parse_response(&response, Some(8)).is_err());
    }

    #[test]
    fn xgt_address_and_lengths_fail_closed() {
        assert!(parse_address("D100").is_err());
        assert!(parse_address("%DW-1").is_err());
        assert!(
            build_individual_read("%DD100", INDIVIDUAL_WORD, 1, CPU_XGK, 0, 3, "LSIS-XGT").is_err()
        );
        assert!(
            build_individual_read("%DW100", INDIVIDUAL_WORD, 1, CPU_XGK, 0, 3, "LSIS-XGT").is_ok()
        );
        assert!(build_continuous_read("%DB0", 0, 1, CPU_XGK, 0, 3, "LSIS-XGT").is_err());
    }
}
