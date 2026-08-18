//! 传输无关的 Modbus PDU 层。
//!
//! 本模块处理所有功能码(FC)的 PDU 构建和解析,完全不涉及传输细节
//! (无 CRC / LRC / MBAP / unit_id)。传输层负责在外层包装:
//! - RTU:`[unit_id][pdu][crc16]`
//! - ASCII:`:[hex(unit_id + pdu + lrc)]CR LF`
//! - TCP/UDP:`[mbap_header][pdu]`(无 unit_id 在 PDU 外,unit_id 在 MBAP 头里)
//!
//! 这种分离让 FC 逻辑可被所有传输复用。

use crate::modbus_rtu::RtuError;

// === 功能码常量 ===
pub const READ_COILS: u8 = 0x01;
pub const READ_DISCRETE_INPUTS: u8 = 0x02;
pub const READ_HOLDING_REGISTERS: u8 = 0x03;
pub const READ_INPUT_REGISTERS: u8 = 0x04;
pub const WRITE_SINGLE_COIL: u8 = 0x05;
pub const WRITE_SINGLE_REGISTER: u8 = 0x06;
pub const WRITE_MULTIPLE_COILS: u8 = 0x0F;
pub const WRITE_MULTIPLE_REGISTERS: u8 = 0x10;
pub const MASK_WRITE_REGISTER: u8 = 0x16;
pub const READ_WRITE_MULTIPLE_REGISTERS: u8 = 0x17;
pub const READ_DEVICE_IDENTIFICATION: u8 = 0x2B;
pub const DIAGNOSTICS: u8 = 0x08;
pub const READ_EXCEPTION_STATUS: u8 = 0x07;
pub const GET_COMM_EVENT_COUNTER: u8 = 0x0B;
pub const GET_COMM_EVENT_LOG: u8 = 0x0C;
pub const REPORT_SLAVE_ID: u8 = 0x11;

// === 数量上限(协议规范) ===
pub const MIN_READ_COILS: u16 = 1;
pub const MAX_READ_COILS: u16 = 2000;
pub const MIN_READ_REGISTERS: u16 = 1;
pub const MAX_READ_REGISTERS: u16 = 125;
pub const MIN_WRITE_COILS: u16 = 1;
pub const MAX_WRITE_COILS: u16 = 1968;
pub const MIN_WRITE_REGISTERS: u16 = 1;
pub const MAX_WRITE_REGISTERS: u16 = 123;
/// FC23 写数量上限:PDU = 10 + N×2 ≤ 253 → N ≤ 121。
/// 与 FC16 的 123 不同,因为 FC23 PDU 多了读地址/读数量/写地址/写数量/字节数字段。
pub const MAX_FC23_WRITE_REGISTERS: u16 = 121;

// 线圈编码常量(FC05)
const COIL_ON: u16 = 0xFF00;
const COIL_OFF: u16 = 0x0000;

// =============================================================================
// 读操作 PDU 构建
// =============================================================================

/// 构建 FC01/FC02 读位(PDU = FC + start_addr_be + quantity_be,共 5 字节)。
fn build_read_bits_pdu(
    start_address: u16,
    quantity: u16,
    function_code: u8,
    min_qty: u16,
    max_qty: u16,
) -> Result<Vec<u8>, RtuError> {
    validate_quantity(quantity, min_qty, max_qty)?;
    validate_address_range(start_address, quantity)?;
    let mut pdu = Vec::with_capacity(5);
    pdu.push(function_code);
    pdu.extend_from_slice(&start_address.to_be_bytes());
    pdu.extend_from_slice(&quantity.to_be_bytes());
    Ok(pdu)
}

pub fn build_read_coils_pdu(start_address: u16, quantity: u16) -> Result<Vec<u8>, RtuError> {
    build_read_bits_pdu(start_address, quantity, READ_COILS, MIN_READ_COILS, MAX_READ_COILS)
}

pub fn build_read_discrete_inputs_pdu(
    start_address: u16,
    quantity: u16,
) -> Result<Vec<u8>, RtuError> {
    build_read_bits_pdu(
        start_address,
        quantity,
        READ_DISCRETE_INPUTS,
        MIN_READ_COILS,
        MAX_READ_COILS,
    )
}

/// 构建 FC03/FC04 读寄存器(PDU = FC + start_addr_be + quantity_be,共 5 字节)。
fn build_read_registers_pdu(
    start_address: u16,
    quantity: u16,
    function_code: u8,
) -> Result<Vec<u8>, RtuError> {
    validate_quantity(quantity, MIN_READ_REGISTERS, MAX_READ_REGISTERS)?;
    validate_address_range(start_address, quantity)?;
    let mut pdu = Vec::with_capacity(5);
    pdu.push(function_code);
    pdu.extend_from_slice(&start_address.to_be_bytes());
    pdu.extend_from_slice(&quantity.to_be_bytes());
    Ok(pdu)
}

pub fn build_read_holding_registers_pdu(
    start_address: u16,
    quantity: u16,
) -> Result<Vec<u8>, RtuError> {
    build_read_registers_pdu(start_address, quantity, READ_HOLDING_REGISTERS)
}

pub fn build_read_input_registers_pdu(
    start_address: u16,
    quantity: u16,
) -> Result<Vec<u8>, RtuError> {
    build_read_registers_pdu(start_address, quantity, READ_INPUT_REGISTERS)
}

// =============================================================================
// 读操作响应解析
// =============================================================================

/// 解析 FC01/FC02 读位响应(PDU = FC + byte_count + packed_bits)。
pub fn parse_read_bits_response(
    pdu: &[u8],
    quantity: u16,
    expected_fc: u8,
) -> Result<Vec<bool>, RtuError> {
    let (fc, data) = split_fc(pdu, expected_fc)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::ByteCountMismatch {
            expected: 0,
            received: 0,
        });
    }
    let expected_byte_count = usize::from(quantity.div_ceil(8));
    let Some(&byte_count) = data.first() else {
        return Err(RtuError::MissingByteCount);
    };
    let byte_count = usize::from(byte_count);
    if byte_count != expected_byte_count {
        return Err(RtuError::ByteCountMismatch {
            expected: expected_byte_count,
            received: byte_count,
        });
    }
    if data.len() != byte_count + 1 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: byte_count + 1,
            received: data.len(),
        });
    }
    Ok(unpack_bits(&data[1..], quantity))
}

pub fn parse_read_coils_response(pdu: &[u8], quantity: u16) -> Result<Vec<bool>, RtuError> {
    parse_read_bits_response(pdu, quantity, READ_COILS)
}

pub fn parse_read_discrete_inputs_response(
    pdu: &[u8],
    quantity: u16,
) -> Result<Vec<bool>, RtuError> {
    parse_read_bits_response(pdu, quantity, READ_DISCRETE_INPUTS)
}

/// 解析 FC03/FC04 读寄存器响应(PDU = FC + byte_count + big_endian_u16[])。
pub fn parse_read_registers_response(
    pdu: &[u8],
    quantity: u16,
    expected_fc: u8,
) -> Result<Vec<u16>, RtuError> {
    let (fc, data) = split_fc(pdu, expected_fc)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::ByteCountMismatch {
            expected: 0,
            received: 0,
        });
    }
    let expected_byte_count = usize::from(quantity) * 2;
    let Some(&byte_count) = data.first() else {
        return Err(RtuError::MissingByteCount);
    };
    let byte_count = usize::from(byte_count);
    if byte_count != expected_byte_count {
        return Err(RtuError::ByteCountMismatch {
            expected: expected_byte_count,
            received: byte_count,
        });
    }
    if data.len() != byte_count + 1 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: byte_count + 1,
            received: data.len(),
        });
    }
    Ok(data[1..]
        .chunks_exact(2)
        .map(|c| u16::from_be_bytes([c[0], c[1]]))
        .collect())
}

pub fn parse_read_holding_registers_response(
    pdu: &[u8],
    quantity: u16,
) -> Result<Vec<u16>, RtuError> {
    parse_read_registers_response(pdu, quantity, READ_HOLDING_REGISTERS)
}

pub fn parse_read_input_registers_response(
    pdu: &[u8],
    quantity: u16,
) -> Result<Vec<u16>, RtuError> {
    parse_read_registers_response(pdu, quantity, READ_INPUT_REGISTERS)
}

// =============================================================================
// 写操作 PDU 构建
// =============================================================================

/// FC05 写单线圈:ON=0xFF00, OFF=0x0000。
pub fn build_write_single_coil_pdu(address: u16, value: bool) -> Result<Vec<u8>, RtuError> {
    let mut pdu = Vec::with_capacity(5);
    pdu.push(WRITE_SINGLE_COIL);
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(&(if value { COIL_ON } else { COIL_OFF }).to_be_bytes());
    Ok(pdu)
}

/// FC06 写单寄存器。
pub fn build_write_single_register_pdu(address: u16, value: u16) -> Result<Vec<u8>, RtuError> {
    let mut pdu = Vec::with_capacity(5);
    pdu.push(WRITE_SINGLE_REGISTER);
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(&value.to_be_bytes());
    Ok(pdu)
}

/// FC15 写多线圈:PDU = FC + addr_be + qty_be + byte_count + packed_bits。
pub fn build_write_multiple_coils_pdu(
    address: u16,
    values: &[bool],
) -> Result<Vec<u8>, RtuError> {
    let quantity = u16::try_from(values.len()).unwrap_or(0);
    validate_quantity(quantity, MIN_WRITE_COILS, MAX_WRITE_COILS)?;
    validate_address_range(address, quantity)?;
    let byte_count = u8::try_from(usize::from(quantity.div_ceil(8))).unwrap();
    let mut pdu = Vec::with_capacity(6 + usize::from(byte_count));
    pdu.push(WRITE_MULTIPLE_COILS);
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(&quantity.to_be_bytes());
    pdu.push(byte_count);
    pdu.extend_from_slice(&pack_bits(values));
    Ok(pdu)
}

/// FC16 写多寄存器:PDU = FC + addr_be + qty_be + byte_count + big_endian_u16[]。
pub fn build_write_multiple_registers_pdu(
    address: u16,
    values: &[u16],
) -> Result<Vec<u8>, RtuError> {
    let quantity = u16::try_from(values.len()).unwrap_or(0);
    validate_quantity(quantity, MIN_WRITE_REGISTERS, MAX_WRITE_REGISTERS)?;
    validate_address_range(address, quantity)?;
    let byte_count = u8::try_from(usize::from(quantity) * 2).unwrap();
    let mut pdu = Vec::with_capacity(6 + usize::from(byte_count));
    pdu.push(WRITE_MULTIPLE_REGISTERS);
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(&quantity.to_be_bytes());
    pdu.push(byte_count);
    for &v in values {
        pdu.extend_from_slice(&v.to_be_bytes());
    }
    Ok(pdu)
}

// =============================================================================
// 写操作响应解析(写响应回显请求头,无 byte_count 字段)
// =============================================================================

/// FC05 写单线圈响应:回显请求(addr + value, 4 字节)。
pub fn parse_write_single_coil_response(pdu: &[u8]) -> Result<(u16, bool), RtuError> {
    let (fc, data) = split_fc(pdu, WRITE_SINGLE_COIL)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::CoilValueInvalid { value: 0 });
    }
    if data.len() != 4 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: 4,
            received: data.len(),
        });
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let raw_value = u16::from_be_bytes([data[2], data[3]]);
    match raw_value {
        COIL_ON => Ok((address, true)),
        COIL_OFF => Ok((address, false)),
        _ => Err(RtuError::CoilValueInvalid { value: raw_value }),
    }
}

/// FC06 写单寄存器响应:回显请求(addr + value, 4 字节)。
pub fn parse_write_single_register_response(pdu: &[u8]) -> Result<(u16, u16), RtuError> {
    let (fc, data) = split_fc(pdu, WRITE_SINGLE_REGISTER)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::WriteResponseEchoMismatch {
            expected_address: 0,
            received_address: 0,
        });
    }
    if data.len() != 4 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: 4,
            received: data.len(),
        });
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let value = u16::from_be_bytes([data[2], data[3]]);
    Ok((address, value))
}

/// FC15/FC16 写多响应:addr_be + quantity_be(4 字节,无 byte_count)。
pub fn parse_write_multiple_response(
    pdu: &[u8],
    expected_fc: u8,
) -> Result<(u16, u16), RtuError> {
    let (fc, data) = split_fc(pdu, expected_fc)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::WriteResponseQuantityMismatch {
            expected: 0,
            received: 0,
        });
    }
    if data.len() != 4 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: 4,
            received: data.len(),
        });
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let quantity = u16::from_be_bytes([data[2], data[3]]);
    Ok((address, quantity))
}

pub fn parse_write_multiple_coils_response(pdu: &[u8]) -> Result<(u16, u16), RtuError> {
    parse_write_multiple_response(pdu, WRITE_MULTIPLE_COILS)
}

pub fn parse_write_multiple_registers_response(pdu: &[u8]) -> Result<(u16, u16), RtuError> {
    parse_write_multiple_response(pdu, WRITE_MULTIPLE_REGISTERS)
}

// =============================================================================
// 异常响应检查(所有 FC 共用)
// =============================================================================

/// 检查 PDU 是否为异常响应。如果是,返回 `Some(exception_code)`,否则 `None`。
pub fn check_exception(pdu: &[u8], expected_fc: u8) -> Result<Option<u8>, RtuError> {
    if pdu.is_empty() {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: 1,
            received: 0,
        });
    }
    let fc = pdu[0];
    if fc & 0x80 != 0 {
        // 异常响应
        if pdu.len() != 2 {
            return Err(RtuError::InvalidExceptionLength {
                data_len: pdu.len().saturating_sub(1),
            });
        }
        let base_fc = fc & 0x7F;
        if base_fc != expected_fc {
            return Err(RtuError::FunctionCodeMismatch {
                expected: expected_fc,
                received: fc,
            });
        }
        Ok(Some(pdu[1]))
    } else if fc != expected_fc {
        Err(RtuError::FunctionCodeMismatch {
            expected: expected_fc,
            received: fc,
        })
    } else {
        Ok(None)
    }
}

// =============================================================================
// 高级功能码(FC22/23/43/08)
// =============================================================================

/// FC22 屏蔽写寄存器:PDU = FC + addr_be + and_mask_be + or_mask_be(7 字节)。
pub fn build_mask_write_register_pdu(
    address: u16,
    and_mask: u16,
    or_mask: u16,
) -> Result<Vec<u8>, RtuError> {
    let mut pdu = Vec::with_capacity(7);
    pdu.push(MASK_WRITE_REGISTER);
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(&and_mask.to_be_bytes());
    pdu.extend_from_slice(&or_mask.to_be_bytes());
    Ok(pdu)
}

pub fn parse_mask_write_register_response(pdu: &[u8]) -> Result<(u16, u16, u16), RtuError> {
    let (_fc, data) = split_fc(pdu, MASK_WRITE_REGISTER)?;
    if data.len() != 6 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: 6,
            received: data.len(),
        });
    }
    let address = u16::from_be_bytes([data[0], data[1]]);
    let and_mask = u16::from_be_bytes([data[2], data[3]]);
    let or_mask = u16::from_be_bytes([data[4], data[5]]);
    Ok((address, and_mask, or_mask))
}

/// FC23 读写多寄存器(原子):PDU = FC + read_addr_be + read_qty_be + write_addr_be + write_byte_count + write_data。
pub fn build_read_write_multiple_registers_pdu(
    read_address: u16,
    read_quantity: u16,
    write_address: u16,
    write_values: &[u16],
) -> Result<Vec<u8>, RtuError> {
    validate_quantity(read_quantity, MIN_READ_REGISTERS, MAX_READ_REGISTERS)?;
    let write_qty = u16::try_from(write_values.len()).unwrap_or(0);
    validate_quantity(write_qty, MIN_WRITE_REGISTERS, MAX_FC23_WRITE_REGISTERS)?;
    let byte_count = u8::try_from(write_values.len() * 2).unwrap();
    let mut pdu = Vec::with_capacity(10 + write_values.len() * 2);
    pdu.push(READ_WRITE_MULTIPLE_REGISTERS);
    pdu.extend_from_slice(&read_address.to_be_bytes());
    pdu.extend_from_slice(&read_quantity.to_be_bytes());
    pdu.extend_from_slice(&write_address.to_be_bytes());
    pdu.extend_from_slice(&write_qty.to_be_bytes());
    pdu.push(byte_count);
    for &v in write_values {
        pdu.extend_from_slice(&v.to_be_bytes());
    }
    Ok(pdu)
}

pub fn parse_read_write_multiple_registers_response(
    pdu: &[u8],
    read_quantity: u16,
) -> Result<Vec<u16>, RtuError> {
    let (fc, data) = split_fc(pdu, READ_WRITE_MULTIPLE_REGISTERS)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::ByteCountMismatch {
            expected: 0,
            received: 0,
        });
    }
    let expected_byte_count = usize::from(read_quantity) * 2;
    let Some(&byte_count) = data.first() else {
        return Err(RtuError::MissingByteCount);
    };
    let byte_count = usize::from(byte_count);
    if byte_count != expected_byte_count {
        return Err(RtuError::ByteCountMismatch {
            expected: expected_byte_count,
            received: byte_count,
        });
    }
    if data.len() != byte_count + 1 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: byte_count + 1,
            received: data.len(),
        });
    }
    Ok(data[1..]
        .chunks_exact(2)
        .map(|c| u16::from_be_bytes([c[0], c[1]]))
        .collect())
}

/// FC43/14 读设备标识:PDU = FC(0x2B) + MEI(0x0E) + read_dev_id_code + object_id。
pub fn build_read_device_id_pdu(read_device_id_code: u8, object_id: u8) -> Vec<u8> {
    vec![READ_DEVICE_IDENTIFICATION, 0x0E, read_device_id_code, object_id]
}

/// FC08 诊断:PDU = FC + sub_function_be + data_be(5 字节)。
pub fn build_diagnostics_pdu(sub_function: u8, data: u16) -> Vec<u8> {
    let mut pdu = vec![DIAGNOSTICS];
    pdu.push(sub_function);
    pdu.extend_from_slice(&data.to_be_bytes());
    pdu
}

pub fn parse_diagnostics_response(pdu: &[u8]) -> Result<(u8, u16), RtuError> {
    let (fc, data) = split_fc(pdu, DIAGNOSTICS)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::ByteCountMismatch {
            expected: 0,
            received: 0,
        });
    }
    if data.len() != 3 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: 3,
            received: data.len(),
        });
    }
    let sub_function = data[0];
    let data_val = u16::from_be_bytes([data[1], data[2]]);
    Ok((sub_function, data_val))
}

// =============================================================================
// 诊断类功能码(FC07/FC11/FC12/FC17)— 仅串行线
// =============================================================================

/// FC07 读异常状态:请求 PDU 只有 FC(1 字节),无参数。
pub fn build_read_exception_status_pdu() -> Vec<u8> {
    vec![READ_EXCEPTION_STATUS]
}

/// FC07 响应:PDU = FC + status(1 字节,8 位异常状态)。
pub fn parse_read_exception_status_response(pdu: &[u8]) -> Result<u8, RtuError> {
    let (fc, data) = split_fc(pdu, READ_EXCEPTION_STATUS)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::ByteCountMismatch { expected: 0, received: 0 });
    }
    if data.len() != 1 {
        return Err(RtuError::ResponseDataLengthMismatch { expected: 1, received: data.len() });
    }
    Ok(data[0])
}

/// FC11 获取通信事件计数:请求 PDU 只有 FC。
pub fn build_get_comm_event_counter_pdu() -> Vec<u8> {
    vec![GET_COMM_EVENT_COUNTER]
}

/// FC11 响应:PDU = FC + status(2B) + event_count(2B)。
pub fn parse_get_comm_event_counter_response(pdu: &[u8]) -> Result<(u16, u16), RtuError> {
    let (fc, data) = split_fc(pdu, GET_COMM_EVENT_COUNTER)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::ByteCountMismatch { expected: 0, received: 0 });
    }
    if data.len() != 4 {
        return Err(RtuError::ResponseDataLengthMismatch { expected: 4, received: data.len() });
    }
    let status = u16::from_be_bytes([data[0], data[1]]);
    let event_count = u16::from_be_bytes([data[2], data[3]]);
    Ok((status, event_count))
}

/// FC12 获取通信事件日志:请求 PDU 只有 FC。
pub fn build_get_comm_event_log_pdu() -> Vec<u8> {
    vec![GET_COMM_EVENT_LOG]
}

/// FC12 响应:PDU = FC + byte_count + status(2B) + event_count(2B) + message_count(2B) + events[]。
pub fn parse_get_comm_event_log_response(pdu: &[u8]) -> Result<(u16, u16, u16, Vec<u8>), RtuError> {
    let (fc, data) = split_fc(pdu, GET_COMM_EVENT_LOG)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::ByteCountMismatch { expected: 0, received: 0 });
    }
    let Some(&byte_count) = data.first() else {
        return Err(RtuError::MissingByteCount);
    };
    let byte_count = usize::from(byte_count);
    if data.len() != byte_count + 1 || byte_count < 6 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: byte_count + 1,
            received: data.len(),
        });
    }
    let status = u16::from_be_bytes([data[1], data[2]]);
    let event_count = u16::from_be_bytes([data[3], data[4]]);
    let message_count = u16::from_be_bytes([data[5], data[6]]);
    let events = data[7..].to_vec();
    Ok((status, event_count, message_count, events))
}

/// FC17 报告从站 ID:请求 PDU 只有 FC。
pub fn build_report_slave_id_pdu() -> Vec<u8> {
    vec![REPORT_SLAVE_ID]
}

/// FC17 响应:PDU = FC + byte_count + slave_id_bytes + run_status(1B)。
pub fn parse_report_slave_id_response(pdu: &[u8]) -> Result<(Vec<u8>, u8), RtuError> {
    let (fc, data) = split_fc(pdu, REPORT_SLAVE_ID)?;
    if fc & 0x80 != 0 {
        return Err(RtuError::ByteCountMismatch { expected: 0, received: 0 });
    }
    let Some(&byte_count) = data.first() else {
        return Err(RtuError::MissingByteCount);
    };
    let byte_count = usize::from(byte_count);
    if data.len() != byte_count + 1 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: byte_count + 1,
            received: data.len(),
        });
    }
    let payload = &data[1..];
    // 最后一个字节是 run_status_indicator (0xFF=ON, 0x00=OFF)
    let (run_status, slave_id) = if payload.len() > 1 {
        (*payload.last().unwrap(), payload[..payload.len() - 1].to_vec())
    } else if payload.len() == 1 {
        (payload[0], vec![])
    } else {
        (0, vec![])
    };
    Ok((slave_id, run_status))
}

/// 把 `bool` 数组打包成字节(LSB 优先,对标 Modbus 规范)。
fn pack_bits(values: &[bool]) -> Vec<u8> {
    let byte_count = values.len().div_ceil(8);
    let mut bytes = vec![0u8; byte_count];
    for (i, &v) in values.iter().enumerate() {
        if v {
            bytes[i / 8] |= 1 << (i % 8);
        }
    }
    bytes
}

/// 从字节解包成 `quantity` 个 bool(LSB 优先)。
fn unpack_bits(data: &[u8], quantity: u16) -> Vec<bool> {
    let quantity = usize::from(quantity);
    let mut bits = Vec::with_capacity(quantity);
    for i in 0..quantity {
        let byte = data.get(i / 8).copied().unwrap_or(0);
        bits.push(byte & (1 << (i % 8)) != 0);
    }
    bits
}

fn validate_quantity(quantity: u16, min: u16, max: u16) -> Result<(), RtuError> {
    if !(min..=max).contains(&quantity) {
        return Err(RtuError::InvalidWriteQuantity { quantity, max });
    }
    Ok(())
}

fn validate_address_range(start_address: u16, quantity: u16) -> Result<(), RtuError> {
    if start_address.checked_add(quantity.saturating_sub(1)).is_none() {
        return Err(RtuError::RegisterRangeOverflow {
            start_address,
            quantity,
        });
    }
    Ok(())
}

/// 分离 PDU 的功能码和数据部分,并验证 FC 匹配(支持异常码位)。
fn split_fc(pdu: &[u8], expected_fc: u8) -> Result<(u8, &[u8]), RtuError> {
    if pdu.is_empty() {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: 1,
            received: 0,
        });
    }
    let fc = pdu[0];
    let base_fc = fc & 0x7F;
    if base_fc != expected_fc {
        return Err(RtuError::FunctionCodeMismatch {
            expected: expected_fc,
            received: fc,
        });
    }
    Ok((fc, &pdu[1..]))
}

// =============================================================================
// 测试
// =============================================================================

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn read_coils_pdu_matches_canonical_form() {
        let pdu = build_read_coils_pdu(100, 8).unwrap();
        assert_eq!(pdu, [0x01, 0x00, 0x64, 0x00, 0x08]);
    }

    #[test]
    fn read_holding_registers_pdu_matches_canonical_form() {
        let pdu = build_read_holding_registers_pdu(0, 10).unwrap();
        assert_eq!(pdu, [0x03, 0x00, 0x00, 0x00, 0x0A]);
    }

    #[test]
    fn read_quantities_are_enforced() {
        assert!(build_read_coils_pdu(0, 0).is_err());
        assert!(build_read_coils_pdu(0, 2001).is_err());
        assert!(build_read_holding_registers_pdu(0, 0).is_err());
        assert!(build_read_holding_registers_pdu(0, 126).is_err());
        assert!(build_read_coils_pdu(0, 2000).is_ok());
        assert!(build_read_holding_registers_pdu(0, 125).is_ok());
    }

    #[test]
    fn address_range_overflow_is_rejected() {
        assert!(build_read_coils_pdu(65535, 2).is_err());
        assert!(build_read_holding_registers_pdu(65535, 2).is_err());
        assert!(build_read_holding_registers_pdu(65534, 2).is_ok());
    }

    #[test]
    fn parse_read_coils_response_unpacks_bits() {
        // FC01 + byte_count=1 + 0x05(位 0 和位 2 为 true)
        let pdu = [0x01, 0x01, 0x05];
        let bits = parse_read_coils_response(&pdu, 3).unwrap();
        assert_eq!(bits, vec![true, false, true]);
    }

    #[test]
    fn parse_read_holding_registers_response_decodes_big_endian() {
        let pdu = [0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD];
        let regs = parse_read_holding_registers_response(&pdu, 2).unwrap();
        assert_eq!(regs, vec![0x1234, 0xABCD]);
    }

    #[test]
    fn write_single_coil_pdu_encodes_on_off() {
        let on = build_write_single_coil_pdu(10, true).unwrap();
        assert_eq!(on, [0x05, 0x00, 0x0A, 0xFF, 0x00]);
        let off = build_write_single_coil_pdu(10, false).unwrap();
        assert_eq!(off, [0x05, 0x00, 0x0A, 0x00, 0x00]);
    }

    #[test]
    fn write_single_register_pdu_encodes_value() {
        let pdu = build_write_single_register_pdu(5, 0x1234).unwrap();
        assert_eq!(pdu, [0x06, 0x00, 0x05, 0x12, 0x34]);
    }

    #[test]
    fn write_multiple_coils_pdu_packs_bits() {
        let values = vec![true, false, true, true, false, false, false, true];
        let pdu = build_write_multiple_coils_pdu(0, &values).unwrap();
        // FC=0x0F, addr=0x0000, qty=0x0008, byte_count=0x01, bits=0b10001101=0x8D
        assert_eq!(pdu, [0x0F, 0x00, 0x00, 0x00, 0x08, 0x01, 0x8D]);
    }

    #[test]
    fn write_multiple_registers_pdu_encodes_big_endian() {
        let pdu = build_write_multiple_registers_pdu(0, &[0x1234, 0xABCD]).unwrap();
        // FC=0x10, addr=0x0000, qty=0x0002, byte_count=0x04, data
        assert_eq!(pdu, [0x10, 0x00, 0x00, 0x00, 0x02, 0x04, 0x12, 0x34, 0xAB, 0xCD]);
    }

    #[test]
    fn write_multiple_quantities_are_enforced() {
        assert!(build_write_multiple_coils_pdu(0, &[]).is_err());
        // 1969 个线圈超限
        let too_many: Vec<bool> = vec![true; 1969];
        assert!(build_write_multiple_coils_pdu(0, &too_many).is_err());
        assert!(build_write_multiple_registers_pdu(0, &[]).is_err());
        let too_many_regs: Vec<u16> = vec![0; 124];
        assert!(build_write_multiple_registers_pdu(0, &too_many_regs).is_err());
    }

    #[test]
    fn parse_write_single_coil_response_round_trips() {
        let pdu = [0x05, 0x00, 0x0A, 0xFF, 0x00];
        let (addr, value) = parse_write_single_coil_response(&pdu).unwrap();
        assert_eq!(addr, 10);
        assert!(value);
    }

    #[test]
    fn parse_write_single_register_response_round_trips() {
        let pdu = [0x06, 0x00, 0x05, 0x12, 0x34];
        let (addr, value) = parse_write_single_register_response(&pdu).unwrap();
        assert_eq!(addr, 5);
        assert_eq!(value, 0x1234);
    }

    #[test]
    fn parse_write_multiple_response_returns_address_and_quantity() {
        let pdu = [0x10, 0x00, 0x05, 0x00, 0x02];
        let (addr, qty) = parse_write_multiple_registers_response(&pdu).unwrap();
        assert_eq!(addr, 5);
        assert_eq!(qty, 2);
    }

    #[test]
    fn check_exception_detects_and_classifies() {
        // 正常响应
        assert_eq!(check_exception(&[0x03, 0x04, 0, 0], 0x03).unwrap(), None);
        // 异常响应(FC=0x83, code=0x02)
        assert_eq!(check_exception(&[0x83, 0x02], 0x03).unwrap(), Some(0x02));
        // FC 不匹配
        assert!(check_exception(&[0x04, 0x02], 0x03).is_err());
    }

    #[test]
    fn fc_mismatch_is_detected() {
        let pdu = [0x04, 0x02, 0x00, 0x01];
        assert!(parse_read_holding_registers_response(&pdu, 1).is_err());
    }

    #[test]
    fn pack_unpack_bits_round_trips() {
        let original: Vec<bool> = vec![true, false, true, true, false, true, false, true, true];
        let packed = pack_bits(&original);
        let unpacked = unpack_bits(&packed, original.len() as u16);
        assert_eq!(unpacked, original);
    }

    // === 高级 FC 测试(FC22/23/43/08)===

    #[test]
    fn fc22_mask_write_register_builds_correct_pdu() {
        let pdu = build_mask_write_register_pdu(4, 0xFF00, 0x00FF).unwrap();
        // FC=0x16, addr=0x0004, andMask=0xFF00, orMask=0x00FF
        assert_eq!(pdu, [0x16, 0x00, 0x04, 0xFF, 0x00, 0x00, 0xFF]);
    }

    #[test]
    fn fc22_mask_write_register_response_round_trips() {
        let pdu = vec![0x16, 0x00, 0x04, 0xFF, 0x00, 0x00, 0xFF];
        let (addr, and_mask, or_mask) = parse_mask_write_register_response(&pdu).unwrap();
        assert_eq!(addr, 4);
        assert_eq!(and_mask, 0xFF00);
        assert_eq!(or_mask, 0x00FF);
    }

    #[test]
    fn fc23_read_write_multiple_builds_correct_pdu() {
        let pdu =
            build_read_write_multiple_registers_pdu(0, 2, 10, &[0x1111, 0x2222]).unwrap();
        // FC=0x17, readAddr=0x0000, readQty=0x0002, writeAddr=0x000A, writeQty=0x0002, byteCount=0x04, data
        assert_eq!(
            pdu,
            [0x17, 0x00, 0x00, 0x00, 0x02, 0x00, 0x0A, 0x00, 0x02, 0x04, 0x11, 0x11, 0x22, 0x22]
        );
    }

    #[test]
    fn fc23_read_write_multiple_response_decodes_registers() {
        // 响应:FC=0x17, byteCount=0x04, data=[0x1234, 0xABCD]
        let pdu = vec![0x17, 0x04, 0x12, 0x34, 0xAB, 0xCD];
        let regs = parse_read_write_multiple_registers_response(&pdu, 2).unwrap();
        assert_eq!(regs, vec![0x1234, 0xABCD]);
    }

    #[test]
    fn fc43_read_device_id_builds_pdu() {
        let pdu = build_read_device_id_pdu(1, 0);
        // FC=0x2B, MEI=0x0E, readCode=1, objectId=0
        assert_eq!(pdu, [0x2B, 0x0E, 0x01, 0x00]);
    }

    #[test]
    fn fc08_diagnostics_builds_pdu() {
        let pdu = build_diagnostics_pdu(0, 0xA5A5);
        // FC=0x08, subFunction=0x00, data=0xA5A5
        assert_eq!(pdu, [0x08, 0x00, 0xA5, 0xA5]);
    }

    #[test]
    fn fc08_diagnostics_response_parses() {
        let pdu = vec![0x08, 0x00, 0xA5, 0xA5];
        let (sub, data) = parse_diagnostics_response(&pdu).unwrap();
        assert_eq!(sub, 0);
        assert_eq!(data, 0xA5A5);
    }

    // === FC07/11/12/17 测试 ===

    #[test]
    fn fc07_read_exception_status_builds_and_parses() {
        let pdu = build_read_exception_status_pdu();
        assert_eq!(pdu, [0x07]);
        let resp = vec![0x07, 0x42]; // status=0x42
        let status = parse_read_exception_status_response(&resp).unwrap();
        assert_eq!(status, 0x42);
    }

    #[test]
    fn fc11_get_comm_event_counter_parses() {
        // FC=0x0B, status=0x0000, eventCount=0x00FF
        let pdu = vec![0x0B, 0x00, 0x00, 0x00, 0xFF];
        let (status, count) = parse_get_comm_event_counter_response(&pdu).unwrap();
        assert_eq!(status, 0);
        assert_eq!(count, 0xFF);
    }

    #[test]
    fn fc12_get_comm_event_log_parses() {
        // FC=0x0C, byteCount=0x08, status=0x0000, eventCount=0x0001, msgCount=0x0002, events=[0x01,0x02]
        let pdu = vec![0x0C, 0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x01, 0x02];
        let (status, event_count, msg_count, events) = parse_get_comm_event_log_response(&pdu).unwrap();
        assert_eq!(status, 0);
        assert_eq!(event_count, 1);
        assert_eq!(msg_count, 2);
        assert_eq!(events, vec![0x01, 0x02]);
    }

    #[test]
    fn fc17_report_slave_id_parses() {
        // FC=0x11, byteCount=0x04, slaveId=[0x41,0x42,0x43], runStatus=0xFF (ON)
        let pdu = vec![0x11, 0x04, 0x41, 0x42, 0x43, 0xFF];
        let (slave_id, run) = parse_report_slave_id_response(&pdu).unwrap();
        assert_eq!(slave_id, vec![0x41, 0x42, 0x43]); // "ABC"
        assert_eq!(run, 0xFF); // ON
    }
}
