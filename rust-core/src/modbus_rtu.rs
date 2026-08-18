use std::fmt;

use thiserror::Error;

pub const MIN_RTU_ADU_LEN: usize = 4;
pub const MAX_RTU_ADU_LEN: usize = 256;
pub const MAX_RTU_PDU_LEN: usize = 253;
pub const MAX_RTU_DATA_LEN: usize = MAX_RTU_PDU_LEN - 1;
pub const MAX_UNIT_ID: u8 = 247;
pub const READ_HOLDING_REGISTERS: u8 = 0x03;
pub const READ_INPUT_REGISTERS: u8 = 0x04;
pub const MIN_READ_REGISTER_QUANTITY: u16 = 1;
pub const MAX_READ_REGISTER_QUANTITY: u16 = 125;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BuiltReadHoldingRegistersRequest {
    pub adu: Vec<u8>,
    pub expected_response_len: usize,
    pub exception_response_len: usize,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParsedReadHoldingRegistersResponse {
    pub registers: Vec<u16>,
    pub exception_code: Option<u8>,
}

impl ParsedReadHoldingRegistersResponse {
    pub fn is_exception(&self) -> bool {
        self.exception_code.is_some()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RtuFrameRole {
    Request,
    Response,
}

impl fmt::Display for RtuFrameRole {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Request => formatter.write_str("request"),
            Self::Response => formatter.write_str("response"),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RtuFrame {
    // The role is transport context and is not encoded on the Modbus RTU wire.
    role: RtuFrameRole,
    unit_id: u8,
    function_code: u8,
    data: Vec<u8>,
}

impl RtuFrame {
    pub fn request(
        unit_id: u8,
        function_code: u8,
        data: impl Into<Vec<u8>>,
    ) -> Result<Self, RtuError> {
        Self::new(RtuFrameRole::Request, unit_id, function_code, data.into())
    }

    pub fn response(
        unit_id: u8,
        function_code: u8,
        data: impl Into<Vec<u8>>,
    ) -> Result<Self, RtuError> {
        Self::new(RtuFrameRole::Response, unit_id, function_code, data.into())
    }

    pub fn decode(bytes: &[u8], role: RtuFrameRole) -> Result<Self, RtuError> {
        if bytes.len() < MIN_RTU_ADU_LEN {
            return Err(RtuError::AduTooShort { len: bytes.len() });
        }
        if bytes.len() > MAX_RTU_ADU_LEN {
            return Err(RtuError::AduTooLong { len: bytes.len() });
        }

        let content_end = bytes.len() - 2;
        let expected = crc16_modbus(&bytes[..content_end]);
        let received = u16::from_le_bytes([bytes[content_end], bytes[content_end + 1]]);
        if received != expected {
            return Err(RtuError::CrcMismatch { expected, received });
        }

        Self::new(role, bytes[0], bytes[1], bytes[2..content_end].to_vec())
    }

    pub fn encode(&self) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(self.adu_len());
        bytes.push(self.unit_id);
        bytes.push(self.function_code);
        bytes.extend_from_slice(&self.data);
        bytes.extend_from_slice(&crc16_modbus(&bytes).to_le_bytes());
        bytes
    }

    pub const fn role(&self) -> RtuFrameRole {
        self.role
    }

    pub const fn unit_id(&self) -> u8 {
        self.unit_id
    }

    pub const fn function_code(&self) -> u8 {
        self.function_code
    }

    pub fn data(&self) -> &[u8] {
        &self.data
    }

    pub fn is_broadcast(&self) -> bool {
        self.role == RtuFrameRole::Request && self.unit_id == 0
    }

    pub fn is_exception(&self) -> bool {
        self.role == RtuFrameRole::Response && self.function_code & 0x80 != 0
    }

    pub fn exception_code(&self) -> Option<u8> {
        self.is_exception().then(|| self.data[0])
    }

    pub fn pdu_len(&self) -> usize {
        1 + self.data.len()
    }

    pub fn adu_len(&self) -> usize {
        1 + self.pdu_len() + 2
    }

    fn new(
        role: RtuFrameRole,
        unit_id: u8,
        function_code: u8,
        data: Vec<u8>,
    ) -> Result<Self, RtuError> {
        if data.len() > MAX_RTU_DATA_LEN {
            return Err(RtuError::PduTooLong {
                len: data.len().saturating_add(1),
            });
        }
        if unit_id > MAX_UNIT_ID {
            return Err(RtuError::ReservedUnitId { unit_id });
        }
        if role == RtuFrameRole::Response && unit_id == 0 {
            return Err(RtuError::BroadcastResponse);
        }

        let invalid_function = function_code == 0
            || (role == RtuFrameRole::Request && function_code & 0x80 != 0)
            || (role == RtuFrameRole::Response && function_code == 0x80);
        if invalid_function {
            return Err(RtuError::InvalidFunctionCode {
                role,
                function_code,
            });
        }

        if role == RtuFrameRole::Response && function_code & 0x80 != 0 && data.len() != 1 {
            return Err(RtuError::InvalidExceptionLength {
                data_len: data.len(),
            });
        }

        Ok(Self {
            role,
            unit_id,
            function_code,
            data,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Error)]
pub enum RtuError {
    #[error("Modbus RTU ADU 只有 {len} 字节，最少需要 {MIN_RTU_ADU_LEN} 字节")]
    AduTooShort { len: usize },
    #[error("Modbus RTU ADU 有 {len} 字节，最大允许 {MAX_RTU_ADU_LEN} 字节")]
    AduTooLong { len: usize },
    #[error("Modbus RTU PDU 有 {len} 字节，最大允许 {MAX_RTU_PDU_LEN} 字节")]
    PduTooLong { len: usize },
    #[error("Modbus 站号 {unit_id} 位于保留范围 248..=255")]
    ReservedUnitId { unit_id: u8 },
    #[error("Modbus 响应不能使用广播站号 0")]
    BroadcastResponse,
    #[error("Modbus {role} 功能码 0x{function_code:02X} 无效")]
    InvalidFunctionCode {
        role: RtuFrameRole,
        function_code: u8,
    },
    #[error("Modbus 异常响应数据长度为 {data_len}，必须恰好为 1 字节")]
    InvalidExceptionLength { data_len: usize },
    #[error("Modbus RTU CRC 不匹配：期望 0x{expected:04X}，收到 0x{received:04X}")]
    CrcMismatch { expected: u16, received: u16 },
    #[error("读寄存器功能不允许使用广播站号 0")]
    BroadcastReadNotAllowed,
    #[error(
        "读寄存器数量 {quantity} 无效，必须在 {MIN_READ_REGISTER_QUANTITY}..={MAX_READ_REGISTER_QUANTITY} 之间"
    )]
    InvalidReadQuantity { quantity: u16 },
    #[error("寄存器范围越界：起始地址 {start_address}，数量 {quantity}")]
    RegisterRangeOverflow { start_address: u16, quantity: u16 },
    #[error("Modbus 响应站号不匹配：期望 {expected}，收到 {received}")]
    UnitIdMismatch { expected: u8, received: u8 },
    #[error("Modbus 响应功能码不匹配：期望 0x{expected:02X}，收到 0x{received:02X}")]
    FunctionCodeMismatch { expected: u8, received: u8 },
    #[error("读寄存器响应缺少字节计数字段")]
    MissingByteCount,
    #[error("读寄存器字节计数不匹配：期望 {expected}，收到 {received}")]
    ByteCountMismatch { expected: usize, received: usize },
    #[error("读寄存器响应数据长度不匹配：期望 {expected}，收到 {received}")]
    ResponseDataLengthMismatch { expected: usize, received: usize },
    // === 写操作错误(阶段 1) ===
    #[error("写操作数量 {quantity} 无效，最大允许 {max}")]
    InvalidWriteQuantity { quantity: u16, max: u16 },
    #[error("写操作字节计数不匹配：期望 {expected}，收到 {received}")]
    WriteByteCountMismatch { expected: usize, received: usize },
    #[error("线圈值 0x{value:04X} 无效，必须是 0x0000 或 0xFF00")]
    CoilValueInvalid { value: u16 },
    #[error("写响应地址回显不匹配：期望 {expected_address}，收到 {received_address}")]
    WriteResponseEchoMismatch {
        expected_address: u16,
        received_address: u16,
    },
    #[error("写响应数量不匹配：期望 {expected}，收到 {received}")]
    WriteResponseQuantityMismatch { expected: u16, received: u16 },
    // === MBAP / TCP 错误(阶段 1) ===
    #[error("MBAP 帧只有 {len} 字节，最少需要 7 字节(头)+ 1 字节(PDU)")]
    MbapFrameTooShort { len: usize },
    #[error("MBAP 协议标识符 {received} 无效，必须为 0")]
    MbapProtocolMismatch { received: u16 },
    #[error("MBAP 长度字段不匹配：期望 {expected}，收到 {received}")]
    MbapLengthMismatch { expected: usize, received: usize },
    #[error("MBAP 事务 ID 不匹配：期望 {expected}，收到 {received}")]
    TransactionIdMismatch { expected: u16, received: u16 },
    // === ASCII / LRC 错误(阶段 1) ===
    #[error("ASCII 帧只有 {len} 字节，格式无效")]
    AsciiFrameTooShort { len: usize },
    #[error("ASCII 帧缺少起始字节 ':'")]
    AsciiStartByteMissing,
    #[error("ASCII 帧缺少结束字节 CR LF")]
    AsciiEndBytesMissing,
    #[error("ASCII 十六进制解码失败：非法字符 '{char}'")]
    AsciiHexDecodeFailed { char: char },
    #[error("ASCII LRC 校验不匹配：期望 0x{expected:02X}，收到 0x{received:02X}")]
    LrcMismatch { expected: u8, received: u8 },
}

pub fn crc16_modbus(bytes: &[u8]) -> u16 {
    let mut crc = 0xFFFF_u16;
    for byte in bytes {
        crc ^= u16::from(*byte);
        for _ in 0..8 {
            crc = if crc & 1 != 0 {
                (crc >> 1) ^ 0xA001
            } else {
                crc >> 1
            };
        }
    }
    crc
}

pub fn build_read_holding_registers_request(
    unit_id: u8,
    start_address: u16,
    quantity: u16,
) -> Result<BuiltReadHoldingRegistersRequest, RtuError> {
    build_read_registers_request(unit_id, start_address, quantity, READ_HOLDING_REGISTERS)
}

pub fn build_read_input_registers_request(
    unit_id: u8,
    start_address: u16,
    quantity: u16,
) -> Result<BuiltReadHoldingRegistersRequest, RtuError> {
    build_read_registers_request(unit_id, start_address, quantity, READ_INPUT_REGISTERS)
}

fn build_read_registers_request(
    unit_id: u8,
    start_address: u16,
    quantity: u16,
    function_code: u8,
) -> Result<BuiltReadHoldingRegistersRequest, RtuError> {
    validate_read_registers_range(unit_id, start_address, quantity)?;

    let mut data = Vec::with_capacity(4);
    data.extend_from_slice(&start_address.to_be_bytes());
    data.extend_from_slice(&quantity.to_be_bytes());
    let adu = RtuFrame::request(unit_id, function_code, data)?.encode();

    Ok(BuiltReadHoldingRegistersRequest {
        adu,
        expected_response_len: 5 + usize::from(quantity) * 2,
        exception_response_len: 5,
    })
}

pub fn parse_read_holding_registers_response(
    bytes: &[u8],
    expected_unit_id: u8,
    quantity: u16,
) -> Result<ParsedReadHoldingRegistersResponse, RtuError> {
    parse_read_registers_response(bytes, expected_unit_id, quantity, READ_HOLDING_REGISTERS)
}

pub fn parse_read_input_registers_response(
    bytes: &[u8],
    expected_unit_id: u8,
    quantity: u16,
) -> Result<ParsedReadHoldingRegistersResponse, RtuError> {
    parse_read_registers_response(bytes, expected_unit_id, quantity, READ_INPUT_REGISTERS)
}

fn parse_read_registers_response(
    bytes: &[u8],
    expected_unit_id: u8,
    quantity: u16,
    expected_function_code: u8,
) -> Result<ParsedReadHoldingRegistersResponse, RtuError> {
    validate_read_registers_range(expected_unit_id, 0, quantity)?;
    let frame = RtuFrame::decode(bytes, RtuFrameRole::Response)?;

    if frame.unit_id() != expected_unit_id {
        return Err(RtuError::UnitIdMismatch {
            expected: expected_unit_id,
            received: frame.unit_id(),
        });
    }

    let response_function = frame.function_code();
    let base_function = response_function & 0x7F;
    if base_function != expected_function_code {
        return Err(RtuError::FunctionCodeMismatch {
            expected: expected_function_code,
            received: response_function,
        });
    }

    if frame.is_exception() {
        return Ok(ParsedReadHoldingRegistersResponse {
            registers: Vec::new(),
            exception_code: frame.exception_code(),
        });
    }

    let data = frame.data();
    let Some(&received_byte_count) = data.first() else {
        return Err(RtuError::MissingByteCount);
    };
    let expected_byte_count = usize::from(quantity) * 2;
    let received_byte_count = usize::from(received_byte_count);
    if received_byte_count != expected_byte_count {
        return Err(RtuError::ByteCountMismatch {
            expected: expected_byte_count,
            received: received_byte_count,
        });
    }
    if data.len() != received_byte_count + 1 {
        return Err(RtuError::ResponseDataLengthMismatch {
            expected: received_byte_count + 1,
            received: data.len(),
        });
    }

    let registers = data[1..]
        .chunks_exact(2)
        .map(|chunk| u16::from_be_bytes([chunk[0], chunk[1]]))
        .collect();
    Ok(ParsedReadHoldingRegistersResponse {
        registers,
        exception_code: None,
    })
}

pub fn modbus_exception_name(code: u8) -> &'static str {
    match code {
        0x01 => "非法功能",
        0x02 => "非法数据地址",
        0x03 => "非法数据值",
        0x04 => "从站设备故障",
        0x05 => "确认",
        0x06 => "从站设备忙",
        0x08 => "存储奇偶性差错",
        0x0A => "网关路径不可用",
        0x0B => "网关目标设备响应失败",
        _ => "未知异常",
    }
}

fn validate_read_registers_range(
    unit_id: u8,
    start_address: u16,
    quantity: u16,
) -> Result<(), RtuError> {
    if unit_id == 0 {
        return Err(RtuError::BroadcastReadNotAllowed);
    }
    if !(MIN_READ_REGISTER_QUANTITY..=MAX_READ_REGISTER_QUANTITY).contains(&quantity) {
        return Err(RtuError::InvalidReadQuantity { quantity });
    }
    if start_address.checked_add(quantity - 1).is_none() {
        return Err(RtuError::RegisterRangeOverflow {
            start_address,
            quantity,
        });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const READ_TEN_HOLDING_REGISTERS: [u8; 8] = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD];

    fn with_crc(content: &[u8]) -> Vec<u8> {
        let mut bytes = content.to_vec();
        bytes.extend_from_slice(&crc16_modbus(content).to_le_bytes());
        bytes
    }

    #[test]
    fn crc_matches_canonical_vectors_and_wire_byte_order() {
        assert_eq!(crc16_modbus(b"123456789"), 0x4B37);
        assert_eq!(crc16_modbus(&[]), 0xFFFF);
        assert_eq!(crc16_modbus(&READ_TEN_HOLDING_REGISTERS[..6]), 0xCDC5);

        let frame = RtuFrame::request(1, 0x03, [0x00, 0x00, 0x00, 0x0A]).unwrap();
        assert_eq!(frame.encode(), READ_TEN_HOLDING_REGISTERS);
    }

    #[test]
    fn known_request_decodes_and_round_trips() {
        let frame = RtuFrame::decode(&READ_TEN_HOLDING_REGISTERS, RtuFrameRole::Request).unwrap();
        assert_eq!(frame.role(), RtuFrameRole::Request);
        assert_eq!(frame.unit_id(), 1);
        assert_eq!(frame.function_code(), 0x03);
        assert_eq!(frame.data(), [0x00, 0x00, 0x00, 0x0A]);
        assert!(!frame.is_broadcast());
        assert!(!frame.is_exception());
        assert_eq!(frame.encode(), READ_TEN_HOLDING_REGISTERS);
    }

    #[test]
    fn corrupted_data_or_crc_is_rejected() {
        for index in [0, 1, 2, READ_TEN_HOLDING_REGISTERS.len() - 1] {
            let mut corrupted = READ_TEN_HOLDING_REGISTERS;
            corrupted[index] ^= 0x01;
            assert!(matches!(
                RtuFrame::decode(&corrupted, RtuFrameRole::Request),
                Err(RtuError::CrcMismatch { .. })
            ));
        }
    }

    #[test]
    fn exact_adu_boundaries_are_enforced() {
        for len in 0..MIN_RTU_ADU_LEN {
            assert_eq!(
                RtuFrame::decode(&vec![0; len], RtuFrameRole::Request),
                Err(RtuError::AduTooShort { len })
            );
        }
        assert_eq!(
            RtuFrame::decode(&vec![0; MAX_RTU_ADU_LEN + 1], RtuFrameRole::Request),
            Err(RtuError::AduTooLong {
                len: MAX_RTU_ADU_LEN + 1
            })
        );

        let maximum = RtuFrame::request(247, 0x7F, vec![0xA5; MAX_RTU_DATA_LEN]).unwrap();
        let encoded = maximum.encode();
        assert_eq!(encoded.len(), MAX_RTU_ADU_LEN);
        assert_eq!(
            RtuFrame::decode(&encoded, RtuFrameRole::Request).unwrap(),
            maximum
        );
        assert_eq!(
            RtuFrame::request(1, 0x03, vec![0; MAX_RTU_DATA_LEN + 1]),
            Err(RtuError::PduTooLong {
                len: MAX_RTU_PDU_LEN + 1
            })
        );
    }

    #[test]
    fn request_and_response_address_policies_are_distinct() {
        let broadcast = RtuFrame::request(0, 0x10, [0x00]).unwrap();
        assert!(broadcast.is_broadcast());
        assert_eq!(
            RtuFrame::response(0, 0x10, [0x00]),
            Err(RtuError::BroadcastResponse)
        );
        assert!(RtuFrame::response(247, 0x03, [0x00]).is_ok());
        for unit_id in 248..=255 {
            assert_eq!(
                RtuFrame::request(unit_id, 0x03, []),
                Err(RtuError::ReservedUnitId { unit_id })
            );
        }

        let reserved_wire_frame = with_crc(&[248, 0x03]);
        assert_eq!(
            RtuFrame::decode(&reserved_wire_frame, RtuFrameRole::Request),
            Err(RtuError::ReservedUnitId { unit_id: 248 })
        );

        let broadcast_wire_frame = with_crc(&[0, 0x03]);
        assert_eq!(
            RtuFrame::decode(&broadcast_wire_frame, RtuFrameRole::Response),
            Err(RtuError::BroadcastResponse)
        );
    }

    #[test]
    fn invalid_function_code_roles_are_rejected() {
        assert!(matches!(
            RtuFrame::request(1, 0x00, []),
            Err(RtuError::InvalidFunctionCode { .. })
        ));
        assert!(matches!(
            RtuFrame::request(1, 0x83, [0x02]),
            Err(RtuError::InvalidFunctionCode { .. })
        ));
        assert!(matches!(
            RtuFrame::response(1, 0x00, []),
            Err(RtuError::InvalidFunctionCode { .. })
        ));
        assert!(matches!(
            RtuFrame::response(1, 0x80, [0x01]),
            Err(RtuError::InvalidFunctionCode { .. })
        ));
    }

    #[test]
    fn exception_response_is_validated_and_classified() {
        let raw = [0x01, 0x83, 0x02, 0xC0, 0xF1];
        let exception = RtuFrame::decode(&raw, RtuFrameRole::Response).unwrap();
        assert!(exception.is_exception());
        assert_eq!(exception.exception_code(), Some(0x02));
        assert_eq!(exception.adu_len(), 5);
        assert_eq!(exception.encode(), raw);
        assert_eq!(
            RtuFrame::decode(&raw, RtuFrameRole::Request),
            Err(RtuError::InvalidFunctionCode {
                role: RtuFrameRole::Request,
                function_code: 0x83
            })
        );

        assert_eq!(
            RtuFrame::response(1, 0x83, []),
            Err(RtuError::InvalidExceptionLength { data_len: 0 })
        );
        assert_eq!(
            RtuFrame::response(1, 0x83, [0x02, 0x03]),
            Err(RtuError::InvalidExceptionLength { data_len: 2 })
        );

        let empty_exception = with_crc(&[1, 0x83]);
        assert_eq!(
            RtuFrame::decode(&empty_exception, RtuFrameRole::Response),
            Err(RtuError::InvalidExceptionLength { data_len: 0 })
        );
        let oversized_exception = with_crc(&[1, 0x83, 0x02, 0x03]);
        assert_eq!(
            RtuFrame::decode(&oversized_exception, RtuFrameRole::Response),
            Err(RtuError::InvalidExceptionLength { data_len: 2 })
        );
    }

    #[test]
    fn normal_response_round_trips_and_encode_is_deterministic() {
        let response = RtuFrame::response(7, 0x03, [0x02, 0x12, 0x34]).unwrap();
        assert!(!response.is_exception());
        assert_eq!(response.exception_code(), None);

        let first = response.encode();
        let second = response.encode();
        assert_eq!(first, second);
        assert_eq!(
            &first[first.len() - 2..],
            &crc16_modbus(&first[..first.len() - 2]).to_le_bytes()
        );
        assert_eq!(
            RtuFrame::decode(&first, RtuFrameRole::Response).unwrap(),
            response
        );
    }

    #[test]
    fn minimal_normal_frame_and_deterministic_matrix_round_trip() {
        let minimal = RtuFrame::response(1, 0x03, []).unwrap();
        let encoded = minimal.encode();
        assert_eq!(encoded.len(), MIN_RTU_ADU_LEN);
        assert_eq!(
            RtuFrame::decode(&encoded, RtuFrameRole::Response).unwrap(),
            minimal
        );

        for unit_id in [0, 1, 17, 247] {
            for function_code in [0x01, 0x03, 0x10, 0x7F] {
                for data in [vec![], vec![0], vec![0x55; 17], vec![0xAA; 252]] {
                    let frame = RtuFrame::request(unit_id, function_code, data)
                        .expect("valid matrix frame");
                    let encoded = frame.encode();
                    assert_eq!(
                        RtuFrame::decode(&encoded, RtuFrameRole::Request).unwrap(),
                        frame
                    );
                }
            }
        }
    }

    #[test]
    fn fc03_request_matches_the_canonical_vector_and_length_contract() {
        let built = build_read_holding_registers_request(1, 0, 10).unwrap();
        assert_eq!(built.adu, READ_TEN_HOLDING_REGISTERS);
        assert_eq!(built.expected_response_len, 25);
        assert_eq!(built.exception_response_len, 5);

        let maximum = build_read_holding_registers_request(247, 65_411, 125).unwrap();
        assert_eq!(maximum.expected_response_len, 255);
    }

    #[test]
    fn fc03_request_rejects_broadcast_quantity_and_address_overflow() {
        assert_eq!(
            build_read_holding_registers_request(0, 0, 1),
            Err(RtuError::BroadcastReadNotAllowed)
        );
        for quantity in [0, 126] {
            assert_eq!(
                build_read_holding_registers_request(1, 0, quantity),
                Err(RtuError::InvalidReadQuantity { quantity })
            );
        }
        assert_eq!(
            build_read_holding_registers_request(1, 65_535, 2),
            Err(RtuError::RegisterRangeOverflow {
                start_address: 65_535,
                quantity: 2,
            })
        );
    }

    #[test]
    fn fc03_normal_response_decodes_big_endian_registers() {
        let raw = with_crc(&[1, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD]);
        let parsed = parse_read_holding_registers_response(&raw, 1, 2).unwrap();
        assert_eq!(parsed.registers, [0x1234, 0xABCD]);
        assert_eq!(parsed.exception_code, None);
        assert!(!parsed.is_exception());
    }

    #[test]
    fn fc03_exception_response_is_returned_without_waiting_for_normal_length() {
        let raw = with_crc(&[1, 0x83, 0x02]);
        let parsed = parse_read_holding_registers_response(&raw, 1, 125).unwrap();
        assert!(parsed.registers.is_empty());
        assert_eq!(parsed.exception_code, Some(0x02));
        assert!(parsed.is_exception());
        assert_eq!(modbus_exception_name(0x02), "非法数据地址");
    }

    #[test]
    fn fc03_response_rejects_wrong_unit_function_and_shape() {
        let wrong_unit = with_crc(&[2, 0x03, 0x02, 0, 1]);
        assert_eq!(
            parse_read_holding_registers_response(&wrong_unit, 1, 1),
            Err(RtuError::UnitIdMismatch {
                expected: 1,
                received: 2,
            })
        );

        let wrong_function = with_crc(&[1, 0x84, 0x02]);
        assert_eq!(
            parse_read_holding_registers_response(&wrong_function, 1, 1),
            Err(RtuError::FunctionCodeMismatch {
                expected: 0x03,
                received: 0x84,
            })
        );

        let wrong_byte_count = with_crc(&[1, 0x03, 0x04, 0, 1, 0, 2]);
        assert_eq!(
            parse_read_holding_registers_response(&wrong_byte_count, 1, 1),
            Err(RtuError::ByteCountMismatch {
                expected: 2,
                received: 4,
            })
        );

        let short_data = with_crc(&[1, 0x03, 0x02, 0]);
        assert_eq!(
            parse_read_holding_registers_response(&short_data, 1, 1),
            Err(RtuError::ResponseDataLengthMismatch {
                expected: 3,
                received: 2,
            })
        );
    }

    #[test]
    fn fc04_request_and_response_follow_the_input_register_contract() {
        let built = build_read_input_registers_request(1, 0, 2).unwrap();
        assert_eq!(built.adu, [1, 0x04, 0, 0, 0, 2, 0x71, 0xCB]);
        assert_eq!(built.expected_response_len, 9);
        assert_eq!(built.exception_response_len, 5);

        let raw = with_crc(&[1, 0x04, 0x04, 0x00, 0x2A, 0xFF, 0xFE]);
        let parsed = parse_read_input_registers_response(&raw, 1, 2).unwrap();
        assert_eq!(parsed.registers, [42, 65_534]);
        assert_eq!(parsed.exception_code, None);
    }

    #[test]
    fn fc04_accepts_only_its_own_normal_and_exception_function_codes() {
        let fc03 = with_crc(&[1, 0x03, 0x02, 0, 1]);
        assert_eq!(
            parse_read_input_registers_response(&fc03, 1, 1),
            Err(RtuError::FunctionCodeMismatch {
                expected: 0x04,
                received: 0x03,
            })
        );

        let exception = with_crc(&[1, 0x84, 0x02]);
        let parsed = parse_read_input_registers_response(&exception, 1, 125).unwrap();
        assert!(parsed.registers.is_empty());
        assert_eq!(parsed.exception_code, Some(0x02));

        assert_eq!(
            build_read_input_registers_request(0, 0, 1),
            Err(RtuError::BroadcastReadNotAllowed)
        );
    }
}
