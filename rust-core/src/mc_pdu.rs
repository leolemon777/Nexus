//! 三菱 MC 协议指令层(PDU)——与传输无关的指令构建与解析。
//!
//! 规范来源:《三菱全协议设计文档.md》§7(指令集)、§2.1(报文示例)。
//!
//! 覆盖 P0 指令:
//! - 0401 成批读取(字单位 0001 / 位单位 0000)
//! - 1401 成批写入(字单位 0001 / 位单位 0000)
//!
//! ⚠️ 全部多字节字段**小端**——与 Modbus 的 to_be_bytes 物理隔离,禁止混用。

use crate::error::CoreError;
use crate::mc_address::{encode_head_number, McAddress};

/// 成批读取(§7)
pub const CMD_READ_BATCH: u16 = 0x0401;
/// 成批写入(§7)
pub const CMD_WRITE_BATCH: u16 = 0x1401;
/// 随机读取(多个非连续元件,每个 1 字/位)
pub const CMD_READ_RANDOM: u16 = 0x0403;
/// 随机写入(字单位)
pub const CMD_WRITE_RANDOM_WORD: u16 = 0x1403;
/// 多块成批读
pub const CMD_READ_BLOCKS: u16 = 0x0406;
/// 多块成批写
pub const CMD_WRITE_BLOCKS: u16 = 0x1406;
/// 远程 RESET
pub const CMD_REMOTE_RESET: u16 = 0x1001;
/// 远程 RUN
pub const CMD_REMOTE_RUN: u16 = 0x1002;
/// 远程 PAUSE
pub const CMD_REMOTE_PAUSE: u16 = 0x1003;
/// 远程 STOP
pub const CMD_REMOTE_STOP: u16 = 0x1006;
/// 读时钟(响应 7 字节 BCD)
pub const CMD_READ_CLOCK: u16 = 0x0613;
/// 写时钟(请求 7 字节 BCD)
pub const CMD_WRITE_CLOCK: u16 = 0x1313;
/// 回送测试(链路自检)
pub const CMD_ECHO_TEST: u16 = 0x0801;
/// 读 CPU 型号(响应 ASCII 型号串)
pub const CMD_READ_CPU_TYPE: u16 = 0x0101;
/// 读 CPU 状态
pub const CMD_READ_CPU_STATUS: u16 = 0x0102;

// ⚠️ 子命令位/字取值在不同指令间不统一(§7.1 注):
// 0401: 字=0001 / 位=0000;0403: 字=0000 / 位=0001;0406: 字=0000 / 位=0001。
// 因此每条指令单独定义,不做公共常量。

/// 子命令:位单位
pub const SUBCMD_BIT: u16 = 0x0000;
/// 子命令:字单位
pub const SUBCMD_WORD: u16 = 0x0001;

/// 3E 帧成批读点数上限(§7):字 960 / 位 7168
pub const MAX_READ_WORDS: u16 = 960;
pub const MAX_READ_BITS: u16 = 7168;

/// 构建 0401 成批读取请求数据区(帧内「指令」字段之后的部分)。
///
/// 返回 `[指令 2B LE][子命令 2B LE][头设备号 3B LE][软元件代码 1B][点数 2B LE]`。
///
/// 示例(读 D100 1 个字,文档 §2.1.4-(2)):
/// `01 04 01 00 64 00 00 A8 01 00`
pub fn build_read_batch_pdu(addr: &McAddress, points: u16) -> Result<Vec<u8>, CoreError> {
    validate_points(addr, points, MAX_READ_WORDS, MAX_READ_BITS, "读")?;
    let subcmd = if addr.is_bit { SUBCMD_BIT } else { SUBCMD_WORD };
    let mut buf = Vec::with_capacity(10);
    buf.extend_from_slice(&CMD_READ_BATCH.to_le_bytes());
    buf.extend_from_slice(&subcmd.to_le_bytes());
    buf.extend_from_slice(&encode_head_number(addr.head_number));
    buf.push(addr.device_code);
    buf.extend_from_slice(&points.to_le_bytes());
    Ok(buf)
}

/// 构建 1401 成批写入请求数据区。
///
/// 字单位:数据 = 每个 u16 小端 2 字节。
/// 位单位:数据 = 每个位 1 字节(00/01)。
///
/// 示例(写 M100 = ON,文档 §2.1.4-(3)):
/// `01 14 00 00 64 00 00 90 01 00 01`
pub fn build_write_batch_pdu(
    addr: &McAddress,
    values: &[u16], // 位单位时每项取 0/1
) -> Result<Vec<u8>, CoreError> {
    let count = u16::try_from(values.len()).map_err(|_| {
        CoreError::Modbus {
            code: "MC_TOO_MANY_VALUES",
            message: format!("写入数量 {} 超出 u16 范围", values.len()),
            details: None,
        }
    })?;
    if count == 0 {
        return Err(CoreError::Modbus {
            code: "MC_EMPTY_VALUES",
            message: "写入数据不能为空".into(),
            details: None,
        });
    }
    if addr.is_bit {
        for (i, v) in values.iter().enumerate() {
            if *v > 1 {
                return Err(CoreError::Modbus {
                    code: "MC_INVALID_BIT_VALUE",
                    message: format!("位元件第 {i} 项值 {v} 非法(只能 0/1)"),
                    details: None,
                });
            }
        }
    }
    let subcmd = if addr.is_bit { SUBCMD_BIT } else { SUBCMD_WORD };
    let mut buf = Vec::with_capacity(11 + values.len() * 2);
    buf.extend_from_slice(&CMD_WRITE_BATCH.to_le_bytes());
    buf.extend_from_slice(&subcmd.to_le_bytes());
    buf.extend_from_slice(&encode_head_number(addr.head_number));
    buf.push(addr.device_code);
    buf.extend_from_slice(&count.to_le_bytes());
    if addr.is_bit {
        for v in values {
            buf.push(*v as u8);
        }
    } else {
        for v in values {
            buf.extend_from_slice(&v.to_le_bytes());
        }
    }
    Ok(buf)
}

/// 解析 0401 成批读取的**应答数据区**(帧内「结束代码」之后的部分)。
///
/// 位单位:每字节 1 个位(00/01)。
/// 字单位:每 2 字节 1 个字(小端)。
pub fn parse_read_batch_response(data: &[u8], points: u16, is_bit: bool) -> Result<Vec<u16>, CoreError> {
    let expected = if is_bit { points as usize } else { points as usize * 2 };
    if data.len() < expected {
        return Err(CoreError::Modbus {
            code: "MC_RESPONSE_TOO_SHORT",
            message: format!("读取响应数据 {} 字节,期望 {} 字节", data.len(), expected),
            details: None,
        });
    }
    if is_bit {
        Ok(data[..points as usize].iter().map(|&b| u16::from(b)).collect())
    } else {
        let mut words = Vec::with_capacity(points as usize);
        for i in 0..points as usize {
            let lo = data[i * 2];
            let hi = data[i * 2 + 1];
            words.push(u16::from_le_bytes([lo, hi]));
        }
        Ok(words)
    }
}

// =============================================================================
// 进阶指令(M2):随机读 0403 / 随机写 1403 / 多块读写 0406·1406
// ============================================================================

/// 构建 0403 随机读取:多个非连续软元件,每个读 1 字/位。
/// 子命令:**字=0000 / 位=0001**(与 0401 相反,§7.1)。
///
/// 数据区 = 点数(2B) + [地址(3B)+代码(1B)] × n
pub fn build_read_random_pdu(addrs: &[McAddress]) -> Result<Vec<u8>, CoreError> {
    if addrs.is_empty() {
        return Err(CoreError::Modbus {
            code: "MC_EMPTY_ADDRESS_LIST",
            message: "随机读地址列表为空".into(),
            details: None,
        });
    }
    let count = u16::try_from(addrs.len()).map_err(|_| {
        CoreError::Modbus {
            code: "MC_TOO_MANY_VALUES",
            message: format!("随机读点数 {} 超出 u16", addrs.len()),
            details: None,
        }
    })?;
    // 0403 子命令:所有元件须同为位或同为字
    let is_bit = addrs[0].is_bit;
    if addrs.iter().any(|a| a.is_bit != is_bit) {
        return Err(CoreError::Modbus {
            code: "MC_MIXED_BIT_WORD",
            message: "随机读不支持位/字混合(0403 需分两次请求)".into(),
            details: None,
        });
    }
    let subcmd = if is_bit { 0x0001u16 } else { 0x0000u16 };
    let mut buf = Vec::with_capacity(8 + addrs.len() * 4);
    buf.extend_from_slice(&CMD_READ_RANDOM.to_le_bytes());
    buf.extend_from_slice(&subcmd.to_le_bytes());
    buf.extend_from_slice(&count.to_le_bytes());
    for a in addrs {
        buf.extend_from_slice(&encode_head_number(a.head_number));
        buf.push(a.device_code);
    }
    Ok(buf)
}

/// 解析 0403 随机读响应(每元件 1 字/位)。
pub fn parse_read_random_response(data: &[u8], count: usize, is_bit: bool) -> Result<Vec<u16>, CoreError> {
    let expected = if is_bit { count } else { count * 2 };
    if data.len() < expected {
        return Err(CoreError::Modbus {
            code: "MC_RESPONSE_TOO_SHORT",
            message: format!("随机读响应 {} 字节,期望 {}", data.len(), expected),
            details: None,
        });
    }
    if is_bit {
        Ok(data[..count].iter().map(|&b| u16::from(b)).collect())
    } else {
        Ok((0..count)
            .map(|i| u16::from_le_bytes([data[i * 2], data[i * 2 + 1]]))
            .collect())
    }
}

/// 构建 1403 随机写入(字单位,子命令 0000):点数 + [地址+代码+字数据2B]×n。
pub fn build_write_random_word_pdu(entries: &[(McAddress, u16)]) -> Result<Vec<u8>, CoreError> {
    if entries.is_empty() {
        return Err(CoreError::Modbus {
            code: "MC_EMPTY_VALUES",
            message: "随机写列表为空".into(),
            details: None,
        });
    }
    let count = u16::try_from(entries.len()).map_err(|_| {
        CoreError::Modbus {
            code: "MC_TOO_MANY_VALUES",
            message: format!("随机写点数 {} 超出 u16", entries.len()),
            details: None,
        }
    })?;
    let mut buf = Vec::with_capacity(8 + entries.len() * 6);
    buf.extend_from_slice(&CMD_WRITE_RANDOM_WORD.to_le_bytes());
    buf.extend_from_slice(&0x0000u16.to_le_bytes()); // 1403 字单位子命令固定 0000
    buf.extend_from_slice(&count.to_le_bytes());
    for (a, v) in entries {
        buf.extend_from_slice(&encode_head_number(a.head_number));
        buf.push(a.device_code);
        buf.extend_from_slice(&v.to_le_bytes());
    }
    Ok(buf)
}

/// 一个读块(多块读写用):起始地址 + 点数。
#[derive(Debug, Clone)]
pub struct McBlock {
    pub address: McAddress,
    pub points: u16,
}

/// 构建 0406 多块成批读:块数 + [点数+地址+代码]×块数。
/// 子命令:字=0000 / 位=0001(与 0406 文档 §7.1 一致)。
/// ⚠️ 内置以太网口 QCPU 不支持 0406(驱动须降级为多次 0401)。
pub fn build_read_blocks_pdu(blocks: &[McBlock]) -> Result<Vec<u8>, CoreError> {
    if blocks.is_empty() {
        return Err(CoreError::Modbus {
            code: "MC_EMPTY_ADDRESS_LIST",
            message: "多块读块列表为空".into(),
            details: None,
        });
    }
    let block_count = u16::try_from(blocks.len()).map_err(|_| {
        CoreError::Modbus {
            code: "MC_TOO_MANY_BLOCKS",
            message: format!("块数 {} 超出 u16", blocks.len()),
            details: None,
        }
    })?;
    let is_bit = blocks[0].address.is_bit;
    if blocks.iter().any(|b| b.address.is_bit != is_bit) {
        return Err(CoreError::Modbus {
            code: "MC_MIXED_BIT_WORD",
            message: "多块读写不支持位/字混合".into(),
            details: None,
        });
    }
    let subcmd = if is_bit { 0x0001u16 } else { 0x0000u16 };
    let mut buf = Vec::with_capacity(8 + blocks.len() * 6);
    buf.extend_from_slice(&CMD_READ_BLOCKS.to_le_bytes());
    buf.extend_from_slice(&subcmd.to_le_bytes());
    buf.extend_from_slice(&block_count.to_le_bytes());
    for b in blocks {
        validate_points(&b.address, b.points, MAX_READ_WORDS, MAX_READ_BITS, "读")?;
        buf.extend_from_slice(&b.points.to_le_bytes());
        buf.extend_from_slice(&encode_head_number(b.address.head_number));
        buf.push(b.address.device_code);
    }
    Ok(buf)
}

/// 解析 0406 多块读响应:各块数据顺序拼接(块1全部字+块2全部字+…)。
/// 返回按块切分好的 Vec<Vec<u16>>。
pub fn parse_read_blocks_response(
    data: &[u8],
    blocks: &[McBlock],
) -> Result<Vec<Vec<u16>>, CoreError> {
    let mut offset = 0usize;
    let mut result = Vec::with_capacity(blocks.len());
    for b in blocks {
        let need = if b.address.is_bit {
            b.points as usize
        } else {
            b.points as usize * 2
        };
        if offset + need > data.len() {
            return Err(CoreError::Modbus {
                code: "MC_RESPONSE_TOO_SHORT",
                message: format!("多块读响应在第 {} 块不足(偏移 {offset},需 {need},共 {})", blocks.len(), data.len()),
                details: None,
            });
        }
        let chunk = if b.address.is_bit {
            data[offset..offset + need].iter().map(|&x| u16::from(x)).collect()
        } else {
            (0..b.points as usize)
                .map(|i| u16::from_le_bytes([data[offset + i * 2], data[offset + i * 2 + 1]]))
                .collect()
        };
        result.push(chunk);
        offset += need;
    }
    Ok(result)
}

// =============================================================================
// CPU 控制与时钟(M2):1001/1002/1003/1006、0613/1313、0801、0101、0102
// ============================================================================

/// 构建 CPU 控制指令(RESET/RUN/PAUSE/STOP,子命令 0000,无数据区)。
pub fn build_remote_control_pdu(cmd: u16) -> Result<Vec<u8>, CoreError> {
    match cmd {
        CMD_REMOTE_RESET | CMD_REMOTE_RUN | CMD_REMOTE_PAUSE | CMD_REMOTE_STOP => {
            Ok(vec![
                (cmd & 0xFF) as u8,
                (cmd >> 8) as u8,
                0x00, 0x00, // 子命令 0000
            ])
        }
        other => Err(CoreError::Modbus {
            code: "MC_BAD_REMOTE_CMD",
            message: format!("{other:#06x} 不是远程控制指令"),
            details: None,
        }),
    }
}

/// 时钟数据(BCD,§7.3):年月日时分秒星期各 1 字节。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct McClock {
    pub year: u8,   // BCD 00-99
    pub month: u8,  // BCD 01-12
    pub day: u8,    // BCD 01-31
    pub hour: u8,   // BCD 00-23
    pub minute: u8, // BCD 00-59
    pub second: u8, // BCD 00-59
    /// 0=日 1=一 … 6=六
    pub weekday: u8, // BCD 0-6
}

/// 构建 0613 读时钟请求(无数据区)。
pub fn build_read_clock_pdu() -> Vec<u8> {
    vec![0x13, 0x06, 0x00, 0x00]
}

/// 解析 0613 读时钟响应(7 字节 BCD)。
pub fn parse_read_clock_response(data: &[u8]) -> Result<McClock, CoreError> {
    if data.len() < 7 {
        return Err(CoreError::Modbus {
            code: "MC_RESPONSE_TOO_SHORT",
            message: format!("时钟响应 {} 字节,期望 7", data.len()),
            details: None,
        });
    }
    Ok(McClock {
        year: data[0],
        month: data[1],
        day: data[2],
        hour: data[3],
        minute: data[4],
        second: data[5],
        weekday: data[6],
    })
}

/// 构建 1313 写时钟请求(7 字节 BCD)。
pub fn build_write_clock_pdu(clock: &McClock) -> Vec<u8> {
    let mut buf = Vec::with_capacity(11);
    buf.extend_from_slice(&CMD_WRITE_CLOCK.to_le_bytes());
    buf.extend_from_slice(&0x0000u16.to_le_bytes());
    buf.extend_from_slice(&[
        clock.year, clock.month, clock.day, clock.hour, clock.minute, clock.second, clock.weekday,
    ]);
    buf
}

/// 构建 0801 回送测试:任意数据,PLC 原样返回(链路自检首选)。
pub fn build_echo_test_pdu(payload: &[u8]) -> Vec<u8> {
    let mut buf = Vec::with_capacity(4 + payload.len());
    buf.extend_from_slice(&CMD_ECHO_TEST.to_le_bytes());
    buf.extend_from_slice(&0x0000u16.to_le_bytes());
    buf.extend_from_slice(payload);
    buf
}

/// 解析 0801 回送响应:数据应与发送一致。
pub fn parse_echo_test_response(data: &[u8], sent: &[u8]) -> Result<bool, CoreError> {
    Ok(data == sent)
}

/// 构建 0101 读 CPU 型号请求(无数据区)。
pub fn build_read_cpu_type_pdu() -> Vec<u8> {
    vec![0x01, 0x01, 0x00, 0x00]
}

/// 解析 0101 响应:ASCII 型号串(如 "Q06UDV")。
pub fn parse_read_cpu_type_response(data: &[u8]) -> Result<String, CoreError> {
    let end = data.iter().position(|&b| b == 0x00).unwrap_or(data.len());
    String::from_utf8(data[..end].to_vec()).map_err(|_| CoreError::Modbus {
        code: "MC_BAD_CPU_TYPE",
        message: "CPU 型号不是合法 ASCII".into(),
        details: None,
    })
}

/// 构建 0102 读 CPU 状态请求。
pub fn build_read_cpu_status_pdu() -> Vec<u8> {
    vec![0x02, 0x01, 0x00, 0x00]
}

/// CPU 状态(0102 响应)。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CpuStatus {
    Run,
    Stop,
    Pause,
    Other(u8),
}

/// 解析 0102 响应(1 字节状态)。
pub fn parse_read_cpu_status_response(data: &[u8]) -> Result<CpuStatus, CoreError> {
    let b = *data.first().ok_or_else(|| CoreError::Modbus {
        code: "MC_RESPONSE_TOO_SHORT",
        message: "CPU 状态响应为空".into(),
        details: None,
    })?;
    Ok(match b {
        0x00 => CpuStatus::Run,
        0x01 => CpuStatus::Stop,
        0x02 => CpuStatus::Pause,
        other => CpuStatus::Other(other),
    })
}

/// 校验点数上限(字/位分别限制)。
fn validate_points(
    addr: &McAddress,
    points: u16,
    max_words: u16,
    max_bits: u16,
    op: &str,
) -> Result<(), CoreError> {
    if points == 0 {
        return Err(CoreError::Modbus {
            code: "MC_INVALID_POINTS",
            message: format!("{op}点数必须 ≥ 1"),
            details: None,
        });
    }
    let (max, kind) = if addr.is_bit { (max_bits, "位") } else { (max_words, "字") };
    if points > max {
        return Err(CoreError::Modbus {
            code: "MC_POINTS_EXCEEDED",
            message: format!("{op}点数 {points} 超出上限({kind}最多 {max})"),
            details: None,
        });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::mc_address::parse_mc_address;

    /// 文档 §2.1.4-(2):读 D100 1 个字的请求数据区
    #[test]
    fn read_batch_d100_matches_doc_vector() {
        let addr = parse_mc_address("D100").unwrap();
        let pdu = build_read_batch_pdu(&addr, 1).unwrap();
        assert_eq!(pdu, [0x01, 0x04, 0x01, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00]);
    }

    /// 文档 §2.1.4-(1) 抓包:读 D7000 5 个字(位单位子命令 0000)
    #[test]
    fn read_batch_d7000_capture_vector() {
        let addr = parse_mc_address("D7000").unwrap();
        let pdu = build_read_batch_pdu(&addr, 5).unwrap();
        // 注意:抓包用了位单位子命令(特殊用法);标准字读子命令是 0001。
        // 这里按标准构造:01 04 01 00 | 58 1B 00 | A8 | 05 00
        assert_eq!(pdu, [0x01, 0x04, 0x01, 0x00, 0x58, 0x1B, 0x00, 0xA8, 0x05, 0x00]);
    }

    /// 文档 §2.1.4-(3):写 M100 = ON
    #[test]
    fn write_batch_m100_on_matches_doc_vector() {
        let addr = parse_mc_address("M100").unwrap();
        let pdu = build_write_batch_pdu(&addr, &[1]).unwrap();
        assert_eq!(pdu, [0x01, 0x14, 0x00, 0x00, 0x64, 0x00, 0x00, 0x90, 0x01, 0x00, 0x01]);
    }

    #[test]
    fn write_batch_words_little_endian() {
        let addr = parse_mc_address("D200").unwrap();
        let pdu = build_write_batch_pdu(&addr, &[0x1234, 0xABCD]).unwrap();
        // 指令+子命令+地址+代码+点数 = 10 字节,数据 34 12 CD AB(小端)
        assert_eq!(&pdu[10..], &[0x34, 0x12, 0xCD, 0xAB]);
    }

    /// 文档 §2.1.3:读 D100=0x1234 响应数据区是 34 12
    #[test]
    fn parse_read_response_word_little_endian() {
        let words = parse_read_batch_response(&[0x34, 0x12], 1, false).unwrap();
        assert_eq!(words, vec![0x1234]);
    }

    #[test]
    fn parse_read_response_bits() {
        let bits = parse_read_batch_response(&[0x01, 0x00, 0x01], 3, true).unwrap();
        assert_eq!(bits, vec![1, 0, 1]);
    }

    #[test]
    fn rejects_zero_points() {
        let addr = parse_mc_address("D100").unwrap();
        assert!(build_read_batch_pdu(&addr, 0).is_err());
    }

    #[test]
    fn rejects_over_limit_words() {
        let addr = parse_mc_address("D0").unwrap();
        assert!(build_read_batch_pdu(&addr, 961).is_err());
        assert!(build_read_batch_pdu(&addr, 960).is_ok());
    }

    #[test]
    fn rejects_over_limit_bits() {
        let addr = parse_mc_address("M0").unwrap();
        assert!(build_read_batch_pdu(&addr, 7169).is_err());
        assert!(build_read_batch_pdu(&addr, 7168).is_ok());
    }

    #[test]
    fn rejects_invalid_bit_value() {
        let addr = parse_mc_address("M0").unwrap();
        assert!(build_write_batch_pdu(&addr, &[0, 2]).is_err());
    }

    #[test]
    fn rejects_empty_write() {
        let addr = parse_mc_address("D0").unwrap();
        assert!(build_write_batch_pdu(&addr, &[]).is_err());
    }

    // ===== 进阶指令测试(M2) =====

    use crate::mc_address::parse_mc_address as pa;

    /// 文档 §2.1.4-(4):随机读 D0、D10 两个字(0403 子命令 0000 字单位)
    #[test]
    fn read_random_matches_doc_vector() {
        let d0 = pa("D0").unwrap();
        let d10 = pa("D10").unwrap();
        let pdu = build_read_random_pdu(&[d0, d10]).unwrap();
        assert_eq!(
            pdu,
            [0x03, 0x04, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0xA8, 0x0A, 0x00, 0x00, 0xA8]
        );
    }

    /// 0403 位单位子命令是 0001(与 0401 相反)
    #[test]
    fn read_random_bit_subcmd_is_0001() {
        let m0 = pa("M0").unwrap();
        let m5 = pa("M5").unwrap();
        let pdu = build_read_random_pdu(&[m0, m5]).unwrap();
        assert_eq!(&pdu[2..4], &[0x01, 0x00], "0403 位单位子命令应为 0001");
    }

    #[test]
    fn read_random_rejects_mixed_bit_word() {
        let d0 = pa("D0").unwrap();
        let m0 = pa("M0").unwrap();
        assert!(build_read_random_pdu(&[d0, m0]).is_err());
    }

    #[test]
    fn read_random_rejects_empty() {
        assert!(build_read_random_pdu(&[]).is_err());
    }

    #[test]
    fn parse_read_random_response_words() {
        let vals = parse_read_random_response(&[0x34, 0x12, 0xCD, 0xAB], 2, false).unwrap();
        assert_eq!(vals, vec![0x1234, 0xABCD]);
    }

    #[test]
    fn write_random_word_layout() {
        let d100 = pa("D100").unwrap();
        let d200 = pa("D200").unwrap();
        let pdu = build_write_random_word_pdu(&[(d100, 0x1234), (d200, 0xBEEF)]).unwrap();
        assert_eq!(&pdu[..4], &[0x03, 0x14, 0x00, 0x00], "指令 1403 + 子命令 0000");
        assert_eq!(&pdu[4..6], &[0x02, 0x00], "点数 2");
        assert_eq!(&pdu[6..11], &[0x64, 0x00, 0x00, 0xA8, 0x34], "D100 地址+代码+数据低字节");
        assert_eq!(&pdu[11..16], &[0x12, 0xC8, 0x00, 0x00, 0xA8], "数据高字节+D200 地址");
        assert_eq!(&pdu[16..18], &[0xEF, 0xBE], "D200 数据 0xBEEF 小端");
    }

    /// 文档 §2.1.4-(5):多块读 D0~D1(2字) + W10(1字)(0406 子命令 0000 字单位)
    #[test]
    fn read_blocks_matches_doc_vector() {
        let b = vec![
            McBlock { address: pa("D0").unwrap(), points: 2 },
            McBlock { address: pa("W10").unwrap(), points: 1 },
        ];
        let pdu = build_read_blocks_pdu(&b).unwrap();
        assert_eq!(
            pdu,
            [0x06, 0x04, 0x00, 0x00, 0x02, 0x00,
             0x02, 0x00, 0x00, 0x00, 0x00, 0xA8,
             0x01, 0x00, 0x0A, 0x00, 0x00, 0xB4]
        );
    }

    /// 文档 §2.1.4-(5) 响应:34 12 AB CD 56 78 → 两块 [0x1234,0xABCD] + [0x5678]
    #[test]
    fn parse_read_blocks_response_splits_by_block() {
        let blocks = vec![
            McBlock { address: pa("D0").unwrap(), points: 2 },
            McBlock { address: pa("W10").unwrap(), points: 1 },
        ];
        let data = [0x34, 0x12, 0xAB, 0xCD, 0x56, 0x78];
        let result = parse_read_blocks_response(&data, &blocks).unwrap();
        // 小端:34 12 → 0x1234;AB CD → 0xCDAB;56 78 → 0x7856
        assert_eq!(result[0], vec![0x1234u16, 0xCDABu16]);
        assert_eq!(result[1], vec![0x7856u16]);
    }

    #[test]
    fn remote_run_pdu_layout() {
        assert_eq!(build_remote_control_pdu(CMD_REMOTE_RUN).unwrap(), [0x02, 0x10, 0x00, 0x00]);
        assert_eq!(build_remote_control_pdu(CMD_REMOTE_STOP).unwrap(), [0x06, 0x10, 0x00, 0x00]);
        assert_eq!(build_remote_control_pdu(CMD_REMOTE_RESET).unwrap(), [0x01, 0x10, 0x00, 0x00]);
        // 非法指令拒绝
        assert!(build_remote_control_pdu(0x0401).is_err());
    }

    #[test]
    fn clock_roundtrip_bcd() {
        let clock = McClock {
            year: 0x26, month: 0x08, day: 0x15,
            hour: 0x14, minute: 0x30, second: 0x00, weekday: 0x5,
        };
        let pdu = build_write_clock_pdu(&clock);
        assert_eq!(&pdu[..4], &[0x13, 0x13, 0x00, 0x00]);
        assert_eq!(&pdu[4..], &[0x26, 0x08, 0x15, 0x14, 0x30, 0x00, 0x05]);
        let parsed = parse_read_clock_response(&pdu[4..]).unwrap();
        assert_eq!(parsed, clock);
    }

    #[test]
    fn read_clock_request_is_bare() {
        assert_eq!(build_read_clock_pdu(), [0x13, 0x06, 0x00, 0x00]);
    }

    #[test]
    fn echo_test_roundtrip() {
        let payload = [0xAB, 0xCD, 0xEF];
        let pdu = build_echo_test_pdu(&payload);
        assert_eq!(&pdu[..4], &[0x01, 0x08, 0x00, 0x00]);
        assert!(parse_echo_test_response(&payload, &payload).unwrap());
        assert!(!parse_echo_test_response(&[0x00], &payload).unwrap());
    }

    #[test]
    fn cpu_type_parses_ascii_stops_at_null() {
        let resp = parse_read_cpu_type_response(b"Q06UDV\0\0").unwrap();
        assert_eq!(resp, "Q06UDV");
    }

    #[test]
    fn cpu_status_maps() {
        assert_eq!(parse_read_cpu_status_response(&[0x00]).unwrap(), CpuStatus::Run);
        assert_eq!(parse_read_cpu_status_response(&[0x01]).unwrap(), CpuStatus::Stop);
        assert_eq!(parse_read_cpu_status_response(&[0x99]).unwrap(), CpuStatus::Other(0x99));
    }
}
