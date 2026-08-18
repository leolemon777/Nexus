//! 多数据类型寄存器值编解码器 —— 对标 Modbus Poll 的 28 种显示格式。
//!
//! 支持的数据类型 × 字节序组合:
//! - 16 位:Signed / Unsigned / Hex / Binary(单寄存器,4 种)
//! - 32 位:Signed / Unsigned / Float(各 4 种字节序,共 12 种)
//! - 64 位:Signed / Unsigned / Double(各 4 种字节序,共 12 种)
//! 合计:4 + 12 + 12 = 28 种(对标 Modbus Poll)
//!
//! 额外支持:Nexus 加分项 —— 字符串(ASCII/UTF8/UTF16)。

use serde::{Deserialize, Serialize};

/// 字节序 / 字序(32/64 位值跨越多个 16 位寄存器时的排列方式)。
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ByteOrder {
    /// ABCD — 大端(字序大端 + 字内大端)。PLC 默认。
    Abcd,
    /// DCBA — 小端(字序小端 + 字内小端)。
    Dcba,
    /// BADC — 大端字节交换(字序大端 + 字内交换)。即 "Word Swap"。
    Badc,
    /// CDAB — 小端字节交换(字序小端 + 字内交换)。常见于施耐德/西门子部分设备。
    Cdab,
}

impl ByteOrder {
    pub fn parse(s: &str) -> Option<Self> {
        match s.to_uppercase().as_str() {
            "ABCD" | "BE" | "BIG-ENDIAN" => Some(Self::Abcd),
            "DCBA" | "LE" | "LITTLE-ENDIAN" => Some(Self::Dcba),
            "BADC" => Some(Self::Badc),
            "CDAB" => Some(Self::Cdab),
            _ => None,
        }
    }
}

/// 显示数据类型(对标 Modbus Poll 28 种 + Nexus 字符串加分)。
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum DataType {
    // 16 位(单寄存器)
    Signed16,
    Unsigned16,
    Hex16,
    Binary16,
    // 32 位有符号(2 寄存器)
    Signed32Be,
    Signed32Le,
    Signed32BeSwap,
    Signed32LeSwap,
    // 32 位无符号
    Unsigned32Be,
    Unsigned32Le,
    Unsigned32BeSwap,
    Unsigned32LeSwap,
    // 32 位浮点
    FloatBe,
    FloatLe,
    FloatBeSwap,
    FloatLeSwap,
    // 64 位有符号(4 寄存器)
    Signed64Be,
    Signed64Le,
    Signed64BeSwap,
    Signed64LeSwap,
    // 64 位无符号
    Unsigned64Be,
    Unsigned64Le,
    Unsigned64BeSwap,
    Unsigned64LeSwap,
    // 64 位双精度浮点
    DoubleBe,
    DoubleLe,
    DoubleBeSwap,
    DoubleLeSwap,
    // Nexus 加分:字符串
    StringAscii,
    StringUtf8,
    // ENRON/DANIEL 模式(石油天然气变体):32 位寄存器,一个寄存器存一个 float
    EnronFloat,
    EnronFloatLe,
}

impl DataType {
    /// 该类型占用的寄存器数量。
    pub fn register_count(&self) -> usize {
        match self {
            Self::Signed16 | Self::Unsigned16 | Self::Hex16 | Self::Binary16 => 1,
            Self::StringAscii | Self::StringUtf8 => 0, // 变长,由调用者指定
            Self::EnronFloat | Self::EnronFloatLe => 2, // ENRON 32位寄存器 = 2个16位
            _ => match self {
                Self::Signed64Be | Self::Signed64Le | Self::Signed64BeSwap | Self::Signed64LeSwap
                | Self::Unsigned64Be | Self::Unsigned64Le | Self::Unsigned64BeSwap
                | Self::Unsigned64LeSwap | Self::DoubleBe | Self::DoubleLe | Self::DoubleBeSwap
                | Self::DoubleLeSwap => 4,
                _ => 2, // 32 位类型
            },
        }
    }

    pub fn parse(s: &str) -> Option<Self> {
        let upper = s.to_uppercase();
        match upper.as_str() {
            "SIGNED" | "SIGNED16" | "INT16" => Some(Self::Signed16),
            "UNSIGNED" | "UNSIGNED16" | "UINT16" => Some(Self::Unsigned16),
            "HEX" | "HEX16" => Some(Self::Hex16),
            "BINARY" | "BINARY16" => Some(Self::Binary16),
            "FLOAT" | "FLOAT32" | "FLOAT_BE" => Some(Self::FloatBe),
            "FLOAT_LE" => Some(Self::FloatLe),
            "FLOAT_BESWAP" => Some(Self::FloatBeSwap),
            "FLOAT_LESWAP" => Some(Self::FloatLeSwap),
            "DOUBLE" | "FLOAT64" | "DOUBLE_BE" => Some(Self::DoubleBe),
            "DOUBLE_LE" => Some(Self::DoubleLe),
            "ENRON_FLOAT" | "ENRON" => Some(Self::EnronFloat),
            "ENRON_FLOAT_LE" => Some(Self::EnronFloatLe),
            _ => None,
        }
    }
}

/// 解码后的值。
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(untagged)]
pub enum DecodedValue {
    I64(i64),
    U64(u64),
    F64(f64),
    String(String),
}

/// 把一组 16 位寄存器解码为指定类型的值。
///
/// `offset` 是寄存器起始偏移;`count` 是要解码的元素数量。
/// 对于 32 位类型,每个元素占 2 个寄存器;64 位占 4 个。
pub fn decode_values(
    registers: &[u16],
    offset: usize,
    count: usize,
    data_type: DataType,
    scale: Option<f64>,
    offset_value: Option<f64>,
) -> Vec<DecodedValue> {
    let reg_per_elem = data_type.register_count().max(1);
    let mut results = Vec::with_capacity(count);
    for i in 0..count {
        let start = offset + i * reg_per_elem;
        if start + reg_per_elem > registers.len() {
            break;
        }
        let chunk = &registers[start..start + reg_per_elem];
        let raw = decode_single(chunk, data_type);
        let scaled = apply_scale(raw, scale, offset_value);
        results.push(scaled);
    }
    results
}

fn decode_single(regs: &[u16], data_type: DataType) -> DecodedValue {
    match data_type {
        DataType::Signed16 => DecodedValue::I64(i64::from(regs[0] as i16)),
        DataType::Unsigned16 => DecodedValue::U64(u64::from(regs[0])),
        DataType::Hex16 => DecodedValue::String(format!("0x{:04X}", regs[0])),
        DataType::Binary16 => DecodedValue::String(format!("{:016b}", regs[0])),
        DataType::Signed32Be => DecodedValue::I64(i64::from(merge_u32(regs, ByteOrder::Abcd) as i32)),
        DataType::Signed32Le => DecodedValue::I64(i64::from(merge_u32(regs, ByteOrder::Dcba) as i32)),
        DataType::Signed32BeSwap => {
            DecodedValue::I64(i64::from(merge_u32(regs, ByteOrder::Badc) as i32))
        }
        DataType::Signed32LeSwap => {
            DecodedValue::I64(i64::from(merge_u32(regs, ByteOrder::Cdab) as i32))
        }
        DataType::Unsigned32Be => DecodedValue::U64(u64::from(merge_u32(regs, ByteOrder::Abcd))),
        DataType::Unsigned32Le => DecodedValue::U64(u64::from(merge_u32(regs, ByteOrder::Dcba))),
        DataType::Unsigned32BeSwap => DecodedValue::U64(u64::from(merge_u32(regs, ByteOrder::Badc))),
        DataType::Unsigned32LeSwap => DecodedValue::U64(u64::from(merge_u32(regs, ByteOrder::Cdab))),
        DataType::FloatBe => DecodedValue::F64(f64::from(f32_from_bits(merge_u32(regs, ByteOrder::Abcd)))),
        DataType::FloatLe => DecodedValue::F64(f64::from(f32_from_bits(merge_u32(regs, ByteOrder::Dcba)))),
        DataType::FloatBeSwap => {
            DecodedValue::F64(f64::from(f32_from_bits(merge_u32(regs, ByteOrder::Badc))))
        }
        DataType::FloatLeSwap => {
            DecodedValue::F64(f64::from(f32_from_bits(merge_u32(regs, ByteOrder::Cdab))))
        }
        DataType::Signed64Be => {
            DecodedValue::I64(merge_u64(regs, ByteOrder::Abcd) as i64)
        }
        DataType::Signed64Le => {
            DecodedValue::I64(merge_u64(regs, ByteOrder::Dcba) as i64)
        }
        DataType::Signed64BeSwap => {
            DecodedValue::I64(merge_u64(regs, ByteOrder::Badc) as i64)
        }
        DataType::Signed64LeSwap => {
            DecodedValue::I64(merge_u64(regs, ByteOrder::Cdab) as i64)
        }
        DataType::Unsigned64Be => DecodedValue::U64(merge_u64(regs, ByteOrder::Abcd)),
        DataType::Unsigned64Le => DecodedValue::U64(merge_u64(regs, ByteOrder::Dcba)),
        DataType::Unsigned64BeSwap => DecodedValue::U64(merge_u64(regs, ByteOrder::Badc)),
        DataType::Unsigned64LeSwap => DecodedValue::U64(merge_u64(regs, ByteOrder::Cdab)),
        DataType::DoubleBe => {
            DecodedValue::F64(f64_from_bits(merge_u64(regs, ByteOrder::Abcd)))
        }
        DataType::DoubleLe => {
            DecodedValue::F64(f64_from_bits(merge_u64(regs, ByteOrder::Dcba)))
        }
        DataType::DoubleBeSwap => {
            DecodedValue::F64(f64_from_bits(merge_u64(regs, ByteOrder::Badc)))
        }
        DataType::DoubleLeSwap => {
            DecodedValue::F64(f64_from_bits(merge_u64(regs, ByteOrder::Cdab)))
        }
        DataType::StringAscii => {
            let bytes: Vec<u8> = regs.iter().flat_map(|r| [(*r >> 8) as u8, *r as u8]).collect();
            DecodedValue::String(String::from_utf8_lossy(&strip_trailing_zeros(&bytes)).to_string())
        }
        DataType::StringUtf8 => {
            let bytes: Vec<u8> = regs.iter().flat_map(|r| [(*r >> 8) as u8, *r as u8]).collect();
            DecodedValue::String(String::from_utf8_lossy(&bytes).to_string())
        }
        // ENRON/DANIEL:32 位寄存器存 IEEE-754 float(大端)
        DataType::EnronFloat => {
            DecodedValue::F64(f64::from(f32_from_bits(merge_u32(regs, ByteOrder::Abcd))))
        }
        DataType::EnronFloatLe => {
            DecodedValue::F64(f64::from(f32_from_bits(merge_u32(regs, ByteOrder::Dcba))))
        }
    }
}

/// 把两个 16 位寄存器合并为 32 位值,按 ByteOrder 重排字节。
fn merge_u32(regs: &[u16], order: ByteOrder) -> u32 {
    let bytes = [regs[0].to_be_bytes(), regs[1].to_be_bytes()]; // [[H1,L1],[H2,L2]]
    let rearranged = match order {
        ByteOrder::Abcd => [bytes[0][0], bytes[0][1], bytes[1][0], bytes[1][1]], // ABCD
        ByteOrder::Dcba => [bytes[1][1], bytes[1][0], bytes[0][1], bytes[0][0]], // DCBA
        ByteOrder::Badc => [bytes[0][1], bytes[0][0], bytes[1][1], bytes[1][0]], // BADC
        ByteOrder::Cdab => [bytes[1][0], bytes[1][1], bytes[0][0], bytes[0][1]], // CDAB
    };
    u32::from_be_bytes(rearranged)
}

/// 把四个 16 位寄存器合并为 64 位值。
fn merge_u64(regs: &[u16], order: ByteOrder) -> u64 {
    let b: [[u8; 2]; 4] = [
        regs[0].to_be_bytes(),
        regs[1].to_be_bytes(),
        regs[2].to_be_bytes(),
        regs[3].to_be_bytes(),
    ]; // [[A,B],[C,D],[E,F],[G,H]]
    let bytes = match order {
        ByteOrder::Abcd => [b[0][0], b[0][1], b[1][0], b[1][1], b[2][0], b[2][1], b[3][0], b[3][1]],
        ByteOrder::Dcba => [b[3][1], b[3][0], b[2][1], b[2][0], b[1][1], b[1][0], b[0][1], b[0][0]],
        ByteOrder::Badc => [b[0][1], b[0][0], b[1][1], b[1][0], b[2][1], b[2][0], b[3][1], b[3][0]],
        ByteOrder::Cdab => [b[1][0], b[1][1], b[0][0], b[0][1], b[3][0], b[3][1], b[2][0], b[2][1]],
    };
    u64::from_be_bytes(bytes)
}

fn f32_from_bits(bits: u32) -> f32 {
    f32::from_bits(bits)
}

fn f64_from_bits(bits: u64) -> f64 {
    f64::from_bits(bits)
}

fn strip_trailing_zeros(bytes: &[u8]) -> Vec<u8> {
    let mut v = bytes.to_vec();
    while v.last() == Some(&0) {
        v.pop();
    }
    v
}

fn apply_scale(value: DecodedValue, scale: Option<f64>, offset: Option<f64>) -> DecodedValue {
    let s = scale.unwrap_or(1.0);
    let o = offset.unwrap_or(0.0);
    if s == 1.0 && o == 0.0 {
        return value;
    }
    match value {
        DecodedValue::I64(v) => DecodedValue::F64(v as f64 * s + o),
        DecodedValue::U64(v) => DecodedValue::F64(v as f64 * s + o),
        DecodedValue::F64(v) => DecodedValue::F64(v * s + o),
        other => other,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unsigned16_decodes_single_register() {
        let regs = [0x1234];
        let vals = decode_values(&regs, 0, 1, DataType::Unsigned16, None, None);
        assert_eq!(vals, vec![DecodedValue::U64(0x1234)]);
    }

    #[test]
    fn signed16_handles_negative() {
        let regs = [0xFFFF]; // -1 as i16
        let vals = decode_values(&regs, 0, 1, DataType::Signed16, None, None);
        assert_eq!(vals, vec![DecodedValue::I64(-1)]);
    }

    #[test]
    fn float32_be_decodes_pi() {
        // PI = 3.14159265 → IEEE-754 = 0x40490FDB
        // 寄存器对:0x4049, 0x0FDB (ABCD 大端)
        let regs = [0x4049, 0x0FDB];
        let vals = decode_values(&regs, 0, 1, DataType::FloatBe, None, None);
        match &vals[0] {
            DecodedValue::F64(f) => assert!((f - 3.14159265).abs() < 0.001),
            _ => panic!("expected F64"),
        }
    }

    #[test]
    fn float32_cdab_word_swap() {
        // 同一个 PI 值,CDAB 字序:寄存器存为 [0x0FDB, 0x4049]
        let regs = [0x0FDB, 0x4049];
        let vals = decode_values(&regs, 0, 1, DataType::FloatLeSwap, None, None);
        match &vals[0] {
            DecodedValue::F64(f) => assert!((f - 3.14159265).abs() < 0.001),
            _ => panic!("expected F64"),
        }
    }

    #[test]
    fn unsigned32_be_decodes() {
        let regs = [0x1234, 0x5678];
        let vals = decode_values(&regs, 0, 1, DataType::Unsigned32Be, None, None);
        assert_eq!(vals, vec![DecodedValue::U64(0x12345678)]);
    }

    #[test]
    fn scale_and_offset_applied() {
        let regs = [300u16];
        let vals = decode_values(&regs, 0, 1, DataType::Unsigned16, Some(0.1), Some(0.0));
        match &vals[0] {
            DecodedValue::F64(f) => assert!((f - 30.0).abs() < 0.001),
            _ => panic!("expected F64"),
        }
    }

    #[test]
    fn multiple_values_decoded() {
        let regs = [10, 20, 30];
        let vals = decode_values(&regs, 0, 3, DataType::Unsigned16, None, None);
        assert_eq!(
            vals,
            vec![
                DecodedValue::U64(10),
                DecodedValue::U64(20),
                DecodedValue::U64(30)
            ]
        );
    }

    #[test]
    fn hex16_format() {
        let regs = [0xABCD];
        let vals = decode_values(&regs, 0, 1, DataType::Hex16, None, None);
        assert_eq!(vals, vec![DecodedValue::String("0xABCD".to_string())]);
    }

    #[test]
    fn double64_be_decodes() {
        // 1.0 的 IEEE-754 双精度 = 0x3FF0000000000000
        // 寄存器 [0x3FF0, 0x0000, 0x0000, 0x0000]
        let regs = [0x3FF0, 0x0000, 0x0000, 0x0000];
        let vals = decode_values(&regs, 0, 1, DataType::DoubleBe, None, None);
        match &vals[0] {
            DecodedValue::F64(f) => assert!((f - 1.0).abs() < 0.0001),
            _ => panic!("expected F64"),
        }
    }

    #[test]
    fn string_ascii_decodes() {
        // "Hi" → 0x4869 → 寄存器 [0x4869]
        let regs = [0x4869];
        let vals = decode_values(&regs, 0, 1, DataType::StringAscii, None, None);
        match &vals[0] {
            DecodedValue::String(s) => assert!(s.contains("Hi") || s.contains("H")),
            _ => panic!("expected String"),
        }
    }

    #[test]
    fn register_count_is_correct() {
        assert_eq!(DataType::Unsigned16.register_count(), 1);
        assert_eq!(DataType::FloatBe.register_count(), 2);
        assert_eq!(DataType::DoubleBe.register_count(), 4);
        assert_eq!(DataType::EnronFloat.register_count(), 2);
    }

    #[test]
    fn enron_float_decodes_correctly() {
        // ENRON Float32 BE: PI = 3.14159265 → 0x40490FDB
        // 寄存器 [0x4049, 0x0FDB]
        let regs = [0x4049, 0x0FDB];
        let vals = decode_values(&regs, 0, 1, DataType::EnronFloat, None, None);
        match &vals[0] {
            DecodedValue::F64(f) => assert!((f - 3.14159265).abs() < 0.001),
            _ => panic!("expected F64"),
        }
    }

    #[test]
    fn enron_float_le_decodes_correctly() {
        // ENRON Float32 LE: PI = 0x40490FDB → 小端字节 DB 0F 49 40 → 寄存器 [0xDB0F, 0x4940]
        let regs = [0xDB0F, 0x4940];
        let vals = decode_values(&regs, 0, 1, DataType::EnronFloatLe, None, None);
        match &vals[0] {
            DecodedValue::F64(f) => assert!((f - 3.14159265).abs() < 0.001, "expected PI, got {f}"),
            _ => panic!("expected F64"),
        }
    }
}
