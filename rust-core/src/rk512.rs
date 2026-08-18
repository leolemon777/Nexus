//! 西门子 3964R 链路层 + RK512 应用层(S5 兼容计算机链接)。
//!
//! 3964R(公开规范,西门子 CP341/441 手册):
//! - 链路建立:发送方发 STX(0x02) → 接收方回 DLE(0x10)
//! - 数据传输:数据 + DLE(0x10) + ETX(0x03) + BCC(XOR)
//! - 链路释放:发送方发 DLE → 接收方回 DLE
//! - 字符填充:数据中出现 DLE(0x10) 时连发两个 DLE(接收方去重)
//!
//! RK512(应用层,公开规范):
//! - 请求帧头:类型(1B 读/写) + 数量(2B) + DB号(2B) + 偏移(2B) + 协调字(2B) = 10B
//! - 应答帧头:错误码(1B) + 类型(1B) + 数量(2B) + DB号(2B) + 偏移(2B) + 协调字(2B) = 10B + 数据
//! - 类型: 0x01=读数据块 0x02=写数据块 0x03=读标志 0x04=写标志 0x05=读输入 0x06=写输出
//!
//! 串口:RS-232/485 点对点,波特率/校验/数据位由 CP 模块配置。

use crate::error::CoreError;

pub const DLE: u8 = 0x10;
pub const STX: u8 = 0x02;
pub const ETX: u8 = 0x03;
pub const NAK: u8 = 0x15;

fn rk512_err(msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code: "RK512_INVALID", message: msg.into(), details: None }
}

// ============ 3964R 链路层 ============

/// BCC(XOR 校验):从数据首字节到 DLE+ETX(含)。
pub fn bcc_3964(data: &[u8], dle_etx: &[u8]) -> u8 {
    let mut acc = 0u8;
    for b in data.iter().chain(dle_etx.iter()) {
        acc ^= b;
    }
    acc
}

/// 对数据做 DLE 字符填充(连续 DLE 转义)。
pub fn stuff_dle(data: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(data.len() + data.iter().filter(|b| **b == DLE).count());
    for b in data {
        if *b == DLE {
            out.push(DLE);
        }
        out.push(*b);
    }
    out
}

/// 去除 DLE 字符填充。
pub fn unstuff_dle(data: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(data.len());
    let mut i = 0;
    while i < data.len() {
        if data[i] == DLE && i + 1 < data.len() && data[i + 1] == DLE {
            out.push(DLE);
            i += 2;
        } else {
            out.push(data[i]);
            i += 1;
        }
    }
    out
}

/// 构造 3964R 完整帧(STX + 数据(已填充) + DLE + ETX + BCC)。
/// 注意:发送方需先发 STX 等待 DLE 确认,再发本函数的完整帧。
pub fn build_3964_data_frame(payload: &[u8]) -> Vec<u8> {
    let stuffed = stuff_dle(payload);
    let dle_etx = [DLE, ETX];
    let checksum = bcc_3964(&stuffed, &dle_etx);
    let mut f = Vec::with_capacity(stuffed.len() + 4);
    f.push(STX);
    f.extend_from_slice(&stuffed);
    f.extend_from_slice(&dle_etx);
    f.push(checksum);
    f
}

/// 解析 3964R 数据帧(去 STX、去填充、验 BCC、去 DLE+ETX+BCC)。
pub fn parse_3964_data_frame(frame: &[u8]) -> Result<Vec<u8>, CoreError> {
    if frame.len() < 4 || frame[0] != STX {
        return Err(rk512_err("不是 3964R 帧(起始非 STX 0x02)"));
    }
    // 倒数第 3、2 字节应为 DLE ETX,最后为 BCC
    let n = frame.len();
    if frame[n - 3] != DLE || frame[n - 2] != ETX {
        return Err(rk512_err("帧尾不是 DLE+ETX"));
    }
    let stuffed = &frame[1..n - 3];
    let checksum = frame[n - 1];
    let calc = bcc_3964(stuffed, &[DLE, ETX]);
    if checksum != calc {
        return Err(rk512_err(format!("BCC 校验失败:帧内 0x{checksum:02X},计算 0x{calc:02X}")));
    }
    Ok(unstuff_dle(stuffed))
}

// ============ RK512 应用层 ============

/// RK512 请求帧头(10 字节)。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Rk512Request {
    /// 0x01=读DB 0x02=写DB 0x03=读标志 0x04=写标志 0x05=读输入 0x06=写输出
    pub func: u8,
    /// 数量(字)
    pub count: u16,
    /// DB 号(读标志/输入/输出时为 0)
    pub db: u16,
    /// 偏移(字节)
    pub offset: u16,
    /// 协调字(通常 0)
    pub coordination: u16,
}

impl Rk512Request {
    pub fn encode(&self) -> Vec<u8> {
        let mut f = Vec::with_capacity(9);
        f.push(self.func);
        f.extend_from_slice(&self.count.to_be_bytes());
        f.extend_from_slice(&self.db.to_be_bytes());
        f.extend_from_slice(&self.offset.to_be_bytes());
        f.extend_from_slice(&self.coordination.to_be_bytes());
        f
    }

    pub fn decode(data: &[u8]) -> Result<Self, CoreError> {
        if data.len() < 9 {
            return Err(rk512_err(format!("RK512 请求头过短({}B,需 9B)", data.len())));
        }
        Ok(Self {
            func: data[0],
            count: u16::from_be_bytes([data[1], data[2]]),
            db: u16::from_be_bytes([data[3], data[4]]),
            offset: u16::from_be_bytes([data[5], data[6]]),
            coordination: u16::from_be_bytes([data[7], data[8]]),
        })
    }
}

/// RK512 应答帧头(10 字节 + 数据)。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Rk512Response {
    /// 0=成功,非零=错误码
    pub error: u8,
    pub func: u8,
    pub count: u16,
    pub db: u16,
    pub offset: u16,
    pub coordination: u16,
}

impl Rk512Response {
    pub fn encode(&self, data: &[u8]) -> Vec<u8> {
        let mut f = Vec::with_capacity(10 + data.len());
        f.push(self.error);
        f.push(self.func);
        f.extend_from_slice(&self.count.to_be_bytes());
        f.extend_from_slice(&self.db.to_be_bytes());
        f.extend_from_slice(&self.offset.to_be_bytes());
        f.extend_from_slice(&self.coordination.to_be_bytes());
        f.extend_from_slice(data);
        f
    }
}

/// RK512 错误码 → 人话。
pub fn rk512_error_message(code: u8) -> &'static str {
    match code {
        0x00 => "成功",
        0x01 => "通用错误",
        0x02 => "不允许的任务类型",
        0x03 => "协调标志冲突",
        0x04 => "非法 DB 编号",
        0x05 => "非法地址偏移",
        0x06 => "非法数量(超限)",
        0x07 => "DB 不存在",
        0x08 => "数据不存在",
        0x09 => "CPU 不可用",
        0x0A => "CPU 忙",
        0x0B => "通讯超时",
        0x0C => "BCC 错误",
        _ => "未知错误码",
    }
}

/// RK512 区码映射:区名 → RK512 func 编码。
pub fn rk512_area_func(area: &str) -> Option<u8> {
    match area.to_ascii_uppercase().as_str() {
        "DB" => Some(0x01), // 读 DB
        "M" | "FLAG" => Some(0x03), // 读标志
        "I" | "INPUT" => Some(0x05), // 读输入
        "Q" | "OUTPUT" => Some(0x06), // 写输出
        _ => None,
    }
}

/// 构造完整的 RK512 读请求(3964R 数据帧包裹 RK512 帧头)。
pub fn build_rk512_read(area: &str, db: u16, offset: u16, count: u16) -> Result<Vec<u8>, CoreError> {
    let func = match area.to_ascii_uppercase().as_str() {
        "DB" => 0x01,
        "M" => 0x03,
        "I" => 0x05,
        "Q" => 0x05, // 读输出也用 05(输入方向)
        _ => return Err(rk512_err(format!("未知区「{area}」(DB/M/I/Q)"))),
    };
    let req = Rk512Request { func, count, db, offset, coordination: 0 };
    Ok(build_3964_data_frame(&req.encode()))
}

/// 构造完整的 RK512 写请求(帧头 + 数据,3964R 包裹)。
pub fn build_rk512_write(area: &str, db: u16, offset: u16, data: &[u8]) -> Result<Vec<u8>, CoreError> {
    let func = match area.to_ascii_uppercase().as_str() {
        "DB" => 0x02,
        "M" => 0x04,
        "Q" => 0x06,
        _ => return Err(rk512_err(format!("未知区「{area}」(DB/M/Q)"))),
    };
    let count = (data.len() as u16 + 1) / 2; // 字数(向上取整)
    let req = Rk512Request { func, count, db, offset, coordination: 0 };
    let mut payload = req.encode();
    payload.extend_from_slice(data);
    Ok(build_3964_data_frame(&payload))
}

/// 解析 RK512 应答(去 3964R 封装 → RK512 帧头 + 数据)。
pub fn parse_rk512_response(frame: &[u8]) -> Result<(Rk512Response, Vec<u8>), CoreError> {
    let payload = parse_3964_data_frame(frame)?;
    if payload.len() < 10 {
        return Err(rk512_err("RK512 应答过短"));
    }
    let resp = Rk512Response {
        error: payload[0],
        func: payload[1],
        count: u16::from_be_bytes([payload[2], payload[3]]),
        db: u16::from_be_bytes([payload[4], payload[5]]),
        offset: u16::from_be_bytes([payload[6], payload[7]]),
        coordination: u16::from_be_bytes([payload[8], payload[9]]),
    };
    Ok((resp, payload[10..].to_vec()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dle_stuffing_roundtrip() {
        let data = vec![0x01, DLE, 0x02, DLE, DLE, 0x03];
        let stuffed = stuff_dle(&data);
        assert_eq!(stuffed, vec![0x01, DLE, DLE, 0x02, DLE, DLE, DLE, DLE, 0x03]);
        assert_eq!(unstuff_dle(&stuffed), data);
    }

    #[test]
    fn bcc_xor() {
        // 公开手册样例:BCC = 数据 XOR(含 DLE+ETX)
        let data = [0x01, 0x02, 0x03];
        let dle_etx = [DLE, ETX];
        let expected = 0x01 ^ 0x02 ^ 0x03 ^ DLE ^ ETX;
        assert_eq!(bcc_3964(&data, &dle_etx), expected);
    }

    #[test]
    fn frame_roundtrip_with_dle_in_data() {
        let payload = vec![0x12, DLE, 0x34, 0x56];
        let frame = build_3964_data_frame(&payload);
        let parsed = parse_3964_data_frame(&frame).unwrap();
        assert_eq!(parsed, payload);
    }

    #[test]
    fn rk512_request_encode_decode() {
        let req = Rk512Request { func: 0x01, count: 4, db: 1, offset: 10, coordination: 0 };
        let enc = req.encode();
        assert_eq!(enc.len(), 9);
        assert_eq!(enc[0], 0x01); // 读 DB
        assert_eq!(&enc[1..3], &[0x00, 0x04]); // count=4 字
        assert_eq!(&enc[3..5], &[0x00, 0x01]); // DB1
        assert_eq!(&enc[5..7], &[0x00, 0x0A]); // 偏移 10
        let dec = Rk512Request::decode(&enc).unwrap();
        assert_eq!(dec, req);
    }

    #[test]
    fn rk512_read_write_full_roundtrip() {
        // 读 DB1 偏移 0 共 2 字 → 3964R 帧 → 剥回 → 验证
        let frame = build_rk512_read("DB", 1, 0, 2).unwrap();
        let payload = parse_3964_data_frame(&frame).unwrap();
        let req = Rk512Request::decode(&payload).unwrap();
        assert_eq!((req.func, req.db, req.count), (0x01, 1, 2));

        // 写 DB1 偏移 10 数据 [AA BB CC DD]
        let wframe = build_rk512_write("DB", 1, 10, &[0xAA, 0xBB, 0xCC, 0xDD]).unwrap();
        let wpayload = parse_3964_data_frame(&wframe).unwrap();
        assert_eq!(wpayload.len(), 9 + 4); // 帧 9B + 数据 4B
        assert_eq!(&wpayload[9..], &[0xAA, 0xBB, 0xCC, 0xDD]);
    }

    #[test]
    fn rk512_response_with_error() {
        let resp = Rk512Response { error: 0x07, func: 0x01, count: 0, db: 1, offset: 0, coordination: 0 };
        let frame = build_3964_data_frame(&resp.encode(&[]));
        let (parsed, data) = parse_rk512_response(&frame).unwrap();
        assert_eq!(parsed.error, 0x07); // DB 不存在
        assert!(data.is_empty());
        assert_eq!(rk512_error_message(0x07), "DB 不存在");
    }

    #[test]
    fn corrupted_bcc_rejected() {
        let payload = vec![0x01, 0x02];
        let mut frame = build_3964_data_frame(&payload);
        let n = frame.len();
        frame[n - 1] ^= 0xFF; // 破坏 BCC
        assert!(parse_3964_data_frame(&frame).is_err());
    }
}
