use serde::Serialize;
use serde_json::{Value, json};
use thiserror::Error;

use crate::modbus_rtu::RtuError;

#[derive(Debug, Error)]
pub enum CoreError {
    #[error("JSON 报文格式无效")]
    InvalidJson,
    #[error("请求信封缺少字段或字段类型无效")]
    InvalidEnvelope,
    #[error("不支持协议版本 {received}，当前版本为 {supported}")]
    UnsupportedProtocolVersion { received: u16, supported: u16 },
    #[error("未知命令：{0}")]
    UnknownCommand(String),
    #[error("串口配置无效：{message}")]
    InvalidSerialConfig {
        field: &'static str,
        message: String,
    },
    #[error("输入行超过最大长度 {0} 字节")]
    LineTooLong(usize),
    #[error("{message}")]
    Modbus {
        code: &'static str,
        message: String,
        details: Option<Value>,
    },
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ErrorBody {
    pub code: &'static str,
    pub message: String,
    pub details: Option<Value>,
}

impl CoreError {
    pub fn body(&self) -> ErrorBody {
        let (code, details) = match self {
            Self::InvalidJson => ("INVALID_JSON", None),
            Self::InvalidEnvelope => ("INVALID_ENVELOPE", None),
            Self::UnsupportedProtocolVersion {
                received,
                supported,
            } => (
                "UNSUPPORTED_PROTOCOL_VERSION",
                Some(json!({ "received": received, "supported": supported })),
            ),
            Self::UnknownCommand(command) => {
                ("UNKNOWN_COMMAND", Some(json!({ "command": command })))
            }
            Self::InvalidSerialConfig { field, .. } => {
                ("INVALID_SERIAL_CONFIG", Some(json!({ "field": field })))
            }
            Self::LineTooLong(maximum) => {
                ("LINE_TOO_LONG", Some(json!({ "maximumBytes": maximum })))
            }
            Self::Modbus { code, details, .. } => (*code, details.clone()),
        };

        ErrorBody {
            code,
            message: self.to_string(),
            details,
        }
    }
}

impl From<RtuError> for CoreError {
    fn from(error: RtuError) -> Self {
        let (code, details) = match &error {
            RtuError::AduTooShort { len } => ("ADU_TOO_SHORT", Some(json!({ "length": len }))),
            RtuError::AduTooLong { len } => ("ADU_TOO_LONG", Some(json!({ "length": len }))),
            RtuError::PduTooLong { len } => ("PDU_TOO_LONG", Some(json!({ "length": len }))),
            RtuError::ReservedUnitId { unit_id } => {
                ("RESERVED_UNIT_ID", Some(json!({ "unitId": unit_id })))
            }
            RtuError::BroadcastResponse => ("BROADCAST_RESPONSE", None),
            RtuError::InvalidFunctionCode {
                role,
                function_code,
            } => (
                "INVALID_FUNCTION_CODE",
                Some(json!({ "role": role.to_string(), "functionCode": function_code })),
            ),
            RtuError::InvalidExceptionLength { data_len } => (
                "INVALID_EXCEPTION_LENGTH",
                Some(json!({ "dataLength": data_len })),
            ),
            RtuError::CrcMismatch { expected, received } => (
                "CRC_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
            RtuError::BroadcastReadNotAllowed => ("BROADCAST_READ_NOT_ALLOWED", None),
            RtuError::InvalidReadQuantity { quantity } => (
                "INVALID_READ_QUANTITY",
                Some(json!({ "quantity": quantity })),
            ),
            RtuError::RegisterRangeOverflow {
                start_address,
                quantity,
            } => (
                "REGISTER_RANGE_OVERFLOW",
                Some(json!({ "startAddress": start_address, "quantity": quantity })),
            ),
            RtuError::UnitIdMismatch { expected, received } => (
                "UNIT_ID_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
            RtuError::FunctionCodeMismatch { expected, received } => (
                "FUNCTION_CODE_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
            RtuError::MissingByteCount => ("MISSING_BYTE_COUNT", None),
            RtuError::ByteCountMismatch { expected, received } => (
                "BYTE_COUNT_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
            RtuError::ResponseDataLengthMismatch { expected, received } => (
                "RESPONSE_DATA_LENGTH_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
            // === 写操作错误(阶段 1) ===
            RtuError::InvalidWriteQuantity { quantity, max } => (
                "INVALID_WRITE_QUANTITY",
                Some(json!({ "quantity": quantity, "max": max })),
            ),
            RtuError::WriteByteCountMismatch { expected, received } => (
                "WRITE_BYTE_COUNT_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
            RtuError::CoilValueInvalid { value } => (
                "COIL_VALUE_INVALID",
                Some(json!({ "value": format!("0x{value:04X}") })),
            ),
            RtuError::WriteResponseEchoMismatch {
                expected_address,
                received_address,
            } => (
                "WRITE_RESPONSE_ECHO_MISMATCH",
                Some(json!({
                    "expectedAddress": expected_address,
                    "receivedAddress": received_address
                })),
            ),
            RtuError::WriteResponseQuantityMismatch {
                expected,
                received,
            } => (
                "WRITE_RESPONSE_QUANTITY_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
            // === MBAP / TCP 错误(阶段 1) ===
            RtuError::MbapFrameTooShort { len } => (
                "MBAP_FRAME_TOO_SHORT",
                Some(json!({ "length": len })),
            ),
            RtuError::MbapProtocolMismatch { received } => (
                "MBAP_PROTOCOL_MISMATCH",
                Some(json!({ "received": received })),
            ),
            RtuError::MbapLengthMismatch { expected, received } => (
                "MBAP_LENGTH_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
            RtuError::TransactionIdMismatch { expected, received } => (
                "TRANSACTION_ID_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
            // === ASCII / LRC 错误(阶段 1) ===
            RtuError::AsciiFrameTooShort { len } => (
                "ASCII_FRAME_TOO_SHORT",
                Some(json!({ "length": len })),
            ),
            RtuError::AsciiStartByteMissing => ("ASCII_START_BYTE_MISSING", None),
            RtuError::AsciiEndBytesMissing => ("ASCII_END_BYTES_MISSING", None),
            RtuError::AsciiHexDecodeFailed { char } => (
                "ASCII_HEX_DECODE_FAILED",
                Some(json!({ "char": char })),
            ),
            RtuError::LrcMismatch { expected, received } => (
                "LRC_MISMATCH",
                Some(json!({ "expected": expected, "received": received })),
            ),
        };
        Self::Modbus {
            code,
            message: error.to_string(),
            details,
        }
    }
}
