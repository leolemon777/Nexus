//! 西门子 S7comm PDU 层:Read/Write 作业构建与 Ack_Data 解析、PDU 协商。
//!
//! 规范来源:《西门子全协议设计文档.md》§3.3-§3.8/§4 + deep-dive §3/§4
//! (snap7 源码 + Wireshark dissector + gymgit/s7-pcaps 真实抓包 + python-snap7 3.0 源码四重印证)。
//!
//! 两套 TransportSize 编码(deep-dive §4.1,最易错点):
//! - 请求侧(AnyPointer):`0x01` BIT / `0x02` BYTE / `0x04` WORD / `0x06` DWORD / `0x08` REAL /
//!   `0x1C` COUNTER / `0x1D` TIMER,Length=**元素个数**
//! - 响应/写数据侧:`0x03`(BIT)/`0x04`(B/W/DW)/`0x05`(INT) Length 单位是 **bit**;
//!   `0x06`(DINT)/`0x07`(REAL)/`0x09`(OCTET) 单位是 byte。
//!   解析时除 `0x03/0x07/0x09` 外 `size >>= 3`(snap7 源码);python-snap7 3.0 仅对 0x04 做 /8,
//!   本实现按「TS 0x04 → bit 单位,其余按元素宽度换算」处理,两源兼容。
//!
//! Read 响应数据项结构(python-snap7 3.0 `extract_multi_read_data` 权威):
//! `RC(1) + TS(1) + Length(2 BE) + data + [奇数长度且非末项补 1 字节]` —— **无 0x00 保留头**。

use crate::error::CoreError;
use crate::s7_address::{parse_s7_address, S7Address, S7Kind};

/// ROSCTR(Message Type)
pub const ROSCTR_JOB: u8 = 0x01;
pub const ROSCTR_ACK: u8 = 0x02;
pub const ROSCTR_ACK_DATA: u8 = 0x03;
pub const ROSCTR_USERDATA: u8 = 0x07;

/// 参数区 Function
pub const FUN_SETUP: u8 = 0xF0;
pub const FUN_READ: u8 = 0x04;
pub const FUN_WRITE: u8 = 0x05;

/// snap7 默认 PDU 协商请求值
pub const DEFAULT_PDU_REQUEST: u16 = 480;
/// snap7 MaxVars:一次作业 Item 上限
pub const MAX_ITEMS: usize = 20;

/// 写数据项 TransportSize(deep-dive §4.1 响应侧同套)。
fn data_transport_size(kind: S7Kind) -> u8 {
    match kind {
        S7Kind::Bit => 0x03,
        S7Kind::Timer | S7Kind::Counter => 0x09, // OCTET STRING
        _ => 0x04,                               // BYTE/WORD/DWORD → bit 单位
    }
}

fn err(code: &'static str, msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code, message: msg.into(), details: None }
}

// ============ 通用头 ============

/// 解析后的 S7 Ack_Data(或任意响应)。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct S7Ack {
    pub rosctr: u8,
    pub pdu_ref: u16,
    /// Ack_Data 头部的 Error Class/Code(0x0000 成功;仅 rosctr=0x03 存在)
    pub error: u16,
    pub param: Vec<u8>,
    pub data: Vec<u8>,
}

/// 解析 S7 PDU 头并拆出 param/data。
///
/// 头长:Job/Userdata 10 字节;Ack_Data 12 字节(多 Error Class/Code 2 字节)。
pub fn parse_ack(pdu: &[u8]) -> Result<S7Ack, CoreError> {
    if pdu.len() < 10 || pdu[0] != 0x32 {
        return Err(err("S7_PDU_INVALID", "不是 S7comm PDU(首字节应为 0x32)"));
    }
    let rosctr = pdu[1];
    let pdu_ref = u16::from_be_bytes([pdu[4], pdu[5]]);
    let param_len = u16::from_be_bytes([pdu[6], pdu[7]]) as usize;
    let data_len = u16::from_be_bytes([pdu[8], pdu[9]]) as usize;
    // 头长度按方向分:Job/Userdata 请求 = 10B;Ack_Data 与 Userdata 响应 = 12B(多 Error 2B)。
    if (rosctr == ROSCTR_ACK_DATA || rosctr == ROSCTR_USERDATA)
        && pdu.len() >= 12
        && 12 + param_len + data_len == pdu.len()
    {
        return Ok(S7Ack {
            rosctr,
            pdu_ref,
            error: u16::from_be_bytes([pdu[10], pdu[11]]),
            param: pdu[12..12 + param_len].to_vec(),
            data: pdu[12 + param_len..].to_vec(),
        });
    }
    // Ack_Data(0x03) 只有 12B 一种头形态:即使 10B 算式碰巧匹配也必须拒绝,否则 param 整体错位
    if rosctr == ROSCTR_ACK_DATA {
        return Err(err(
            "S7_PDU_INVALID",
            format!("Ack_Data 长度不自洽:12+参数{param_len}+数据{data_len} ≠ 帧长{}", pdu.len()),
        ));
    }
    if 10 + param_len + data_len == pdu.len() {
        return Ok(S7Ack {
            rosctr,
            pdu_ref,
            error: 0,
            param: pdu[10..10 + param_len].to_vec(),
            data: pdu[10 + param_len..].to_vec(),
        });
    }
    Err(err(
        "S7_PDU_INVALID",
        format!("长度自校验失败:头10/12+参数{param_len}+数据{data_len} ≠ 帧长{}", pdu.len()),
    ))
}

/// S7 头(10 字节,Job/Userdata 用)。
fn s7_header(rosctr: u8, pdu_ref: u16, reserved: u16, param_len: usize, data_len: usize) -> Vec<u8> {
    let mut h = Vec::with_capacity(10);
    h.push(0x32);
    h.push(rosctr);
    h.extend_from_slice(&reserved.to_be_bytes());
    h.extend_from_slice(&pdu_ref.to_be_bytes());
    h.extend_from_slice(&(param_len as u16).to_be_bytes());
    h.extend_from_slice(&(data_len as u16).to_be_bytes());
    h
}

// ============ Setup Communication(0xF0) ============

/// 构建 PDU 协商请求(python-snap7 3.0 默认 AMQ 1/1、PDU 480;真实抓包同构)。
pub fn build_setup_request(pdu_ref: u16, amq_caller: u16, amq_called: u16, pdu_len: u16) -> Vec<u8> {
    let mut out = s7_header(ROSCTR_JOB, pdu_ref, 0x0000, 8, 0);
    out.extend_from_slice(&[FUN_SETUP, 0x00]);
    out.extend_from_slice(&amq_caller.to_be_bytes());
    out.extend_from_slice(&amq_called.to_be_bytes());
    out.extend_from_slice(&pdu_len.to_be_bytes());
    out
}

/// Setup 响应 → (amq_caller, amq_called, 协商 PDU 长度)。
pub fn parse_setup_response(ack: &S7Ack) -> Result<(u16, u16, u16), CoreError> {
    if ack.param.len() != 8 || ack.param[0] != FUN_SETUP {
        return Err(err("S7_PDU_INVALID", "Setup 响应参数区不是 8 字节 0xF0 结构"));
    }
    Ok((
        u16::from_be_bytes([ack.param[2], ack.param[3]]),
        u16::from_be_bytes([ack.param[4], ack.param[5]]),
        u16::from_be_bytes([ack.param[6], ack.param[7]]),
    ))
}

// ============ Read Var(0x04) ============

/// 一个读写请求项(地址 + 数量)。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct S7Item {
    pub addr: S7Address,
    /// 元素个数(按 [`S7Kind`] 的元素语义:位→位数;字节流→字节数;T/C→元素数)
    pub count: u16,
}

impl S7Item {
    pub fn new(address: &str, count: u16) -> Result<Self, CoreError> {
        Ok(Self { addr: parse_s7_address(address)?, count })
    }

    /// 编码 12 字节 AnyPointer 地址项(请求侧 TS)。
    ///
    /// Length 语义按 TS 换算,保证与  一致:
    /// - BIT/TIMER/COUNTER(0x01/0x1C/0x1D):Length = 元素个数
    /// - 字节流(0x02):Length = 总字节数 = 元素数 × 元素宽度
    pub fn encode_any_item(&self) -> [u8; 12] {
        let ts = self.addr.kind.transport_size();
        let wire_count: u16 = match self.addr.kind {
            S7Kind::Bit | S7Kind::Timer | S7Kind::Counter => self.count,
            _ => self.count.saturating_mul(self.addr.kind.elem_bytes() as u16),
        };
        let db = self.addr.db;
        let addr_bytes = self.addr.encode_any_address();
        [
            0x12,
            0x0A,
            0x10,
            ts,
            (wire_count >> 8) as u8,
            wire_count as u8,
            (db >> 8) as u8,
            db as u8,
            self.addr.area,
            addr_bytes[0],
            addr_bytes[1],
            addr_bytes[2],
        ]
    }

    /// 本项数据字节数。
    pub fn data_bytes(&self) -> usize {
        if self.addr.kind == S7Kind::Bit {
            ((self.count as u32 + 7) / 8) as usize
        } else {
            self.addr.kind.elem_bytes() as usize * self.count as usize
        }
    }
}

/// 构建读请求。
pub fn build_read_request(pdu_ref: u16, items: &[S7Item]) -> Result<Vec<u8>, CoreError> {
    if items.is_empty() || items.len() > MAX_ITEMS {
        return Err(err("S7_ITEM_COUNT", format!("Item 数量应为 1-{MAX_ITEMS},实际 {}", items.len())));
    }
    let param_len = 2 + items.len() * 12;
    let mut out = s7_header(ROSCTR_JOB, pdu_ref, 0x0000, param_len, 0);
    out.push(FUN_READ);
    out.push(items.len() as u8);
    for item in items {
        out.extend_from_slice(&item.encode_any_item());
    }
    Ok(out)
}

/// 读响应中的单项数据。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReadItemData {
    pub return_code: u8,
    pub data: Vec<u8>,
}

/// Item 返回码 → 人话(§3.6 + deep-dive §4.3)。
pub fn item_return_code_message(rc: u8) -> &'static str {
    match rc {
        0xFF => "成功",
        0x01 => "硬件错误",
        0x03 => "访问被拒绝(保护等级/权限不足——S7-1200/1500 检查 PUT/GET 与保护等级设置)",
        0x05 => "地址无效(超出范围,或目标 DB 是优化块——需在 TIA 中改为标准访问)",
        0x06 => "数据类型不支持",
        0x07 => "数据类型不一致(长度/类型不匹配)",
        0x0A => "对象不存在(DB 未下载,或目标 DB 是优化块)",
        0x00 => "保留(写请求占位)",
        _ => "未知返回码",
    }
}

/// 头级 Error Code → 人话(deep-dive §4.4 snap7 Code7* 表)。
pub fn header_error_message(code: u16) -> &'static str {
    match code {
        0x0000 => "成功",
        0x0005 => "地址越界",
        0x0006 => "TransportSize 无效",
        0x0007 => "写数据长度不匹配",
        0x000A => "资源/对象不存在",
        0x8104 => "功能不支持(常见于对 S7-1200/1500 发送其不支持的老功能)",
        0x8500 => "数据量超过 PDU 限制(需分片)",
        0xD209 => "资源项不存在",
        0xD241 => "需要密码(在连接参数中提供密码)",
        0xD602 => "密码错误",
        0xD604 => "无密码可清除",
        0xD605 => "无密码可设置",
        0xDC01 => "值无效",
        _ => "未知错误码",
    }
}

/// 解析读响应数据区(python-snap7 3.0 同款规则)。
///
/// 布局:每项 `RC(1)+TS(1)+Length(2)+data[+奇数填充]`。
/// TS=`0x04` 时 Length 单位是 bit(data 字节 = Length/8);其余按元素宽度换算。
pub fn parse_read_response(ack: &S7Ack) -> Result<Vec<ReadItemData>, CoreError> {
    if ack.param.first() != Some(&FUN_READ) {
        return Err(err("S7_PDU_INVALID", "不是 Read 响应(参数区首字节非 0x04)"));
    }
    let item_count = *ack.param.get(1).unwrap_or(&0) as usize;
    let mut results = Vec::with_capacity(item_count);
    let mut off = 0usize;
    for i in 0..item_count {
        if off + 4 > ack.data.len() {
            return Err(err("S7_RESPONSE_TRUNCATED", format!("读响应第 {i} 项数据不完整")));
        }
        let rc = ack.data[off];
        let ts = ack.data[off + 1];
        let length = u16::from_be_bytes([ack.data[off + 2], ack.data[off + 3]]) as u32;
        off += 4;
        // TS 0x04(以及 deep-dive 的 0x03/0x05)→ bit 单位;0x06/0x07/0x09 → byte 单位
        let byte_len = match ts {
            0x04 | 0x05 | 0x03 => (length + 7) / 8, // bit 单位换算(错误项 length=0 → 0 字节)
            _ => length,
        } as usize;
        if off + byte_len > ack.data.len() {
            return Err(err("S7_RESPONSE_TRUNCATED", format!("读响应第 {i} 项数据体不足")));
        }
        results.push(ReadItemData { return_code: rc, data: ack.data[off..off + byte_len].to_vec() });
        off += byte_len;
        // 奇数长度且非末项 → 1 字节偶数填充
        if i + 1 < item_count && byte_len % 2 != 0 {
            off += 1;
        }
    }
    Ok(results)
}

// ============ Write Var(0x05) ============

/// 构建写请求。
///
/// `data_blocks[i]` 是第 i 项的数据(字节数须与 item 对齐:
/// 位访问 → (count+7)/8 字节;其余 → count*elem_bytes)。
/// 数据项:`0x00(占位) + TS(响应侧编码) + Length(bit) + data`。
pub fn build_write_request(
    pdu_ref: u16,
    items: &[S7Item],
    data_blocks: &[Vec<u8>],
) -> Result<Vec<u8>, CoreError> {
    if items.is_empty() || items.len() > MAX_ITEMS {
        return Err(err("S7_ITEM_COUNT", format!("Item 数量应为 1-{MAX_ITEMS},实际 {}", items.len())));
    }
    if items.len() != data_blocks.len() {
        return Err(err("S7_WRITE_MISMATCH", "items 与 data 数量不一致"));
    }
    // 数据区:每项 4B 头 + 数据(奇数补齐)
    let mut data_sec = Vec::new();
    for (i, (item, data)) in items.iter().zip(data_blocks).enumerate() {
        let expected = item.data_bytes();
        if data.len() != expected {
            return Err(err(
                "S7_WRITE_MISMATCH",
                format!("第 {i} 项数据长度 {} 与地址 {} 需要的 {expected} 不符", data.len(), item.addr.display()),
            ));
        }
        let ts = data_transport_size(item.addr.kind);
        // Length 单位随 TS(snap7 opWriteArea:非 Octet/Real/Bit 才 ×8):
        // 0x03(BIT)=位数;0x04(B/W/DW)=bit 数;0x09(OCTET,Timer/Counter)=字节数
        let bit_len: u16 = match item.addr.kind {
            S7Kind::Bit => item.count,
            S7Kind::Timer | S7Kind::Counter => data.len() as u16,
            _ => (data.len() as u16) * 8,
        };
        data_sec.extend_from_slice(&[0x00, ts]);
        data_sec.extend_from_slice(&bit_len.to_be_bytes());
        data_sec.extend_from_slice(data);
        if i + 1 < data_blocks.len() && data.len() % 2 != 0 {
            data_sec.push(0x00);
        }
    }
    let param_len = 2 + items.len() * 12;
    let mut out = s7_header(ROSCTR_JOB, pdu_ref, 0x0000, param_len, data_sec.len());
    out.push(FUN_WRITE);
    out.push(items.len() as u8);
    for item in items {
        out.extend_from_slice(&item.encode_any_item());
    }
    out.extend_from_slice(&data_sec);
    Ok(out)
}

/// 解析写响应:每项 1 字节返回码。
pub fn parse_write_response(ack: &S7Ack) -> Result<Vec<u8>, CoreError> {
    if ack.param.first() != Some(&FUN_WRITE) {
        return Err(err("S7_PDU_INVALID", "不是 Write 响应(参数区首字节非 0x05)"));
    }
    let item_count = *ack.param.get(1).unwrap_or(&0) as usize;
    if ack.data.len() < item_count {
        return Err(err("S7_RESPONSE_TRUNCATED", "写响应返回码不完整"));
    }
    Ok(ack.data[..item_count].to_vec())
}

// ============ CPU 控制 / 状态 / 密码(SW1,字节依据 deep-dive §6 抓包) ============

/// ROSCTR=0x07(Userdata)头(10B)+参数区
fn userdata_header(pdu_ref: u16, param_len: usize, data_len: usize) -> Vec<u8> {
    let mut out = s7_header(ROSCTR_USERDATA, pdu_ref, 0x0000, param_len, data_len);
    out
}

/// 停止 CPU(Job + Fun 0x29 + 'P_PROGRAM')。
/// golden: `29 00 00 00 00 00 09 50 5F 50 52 4F 47 52 41 4D`(deep-dive §6.2 与 STEP7 同构帧)。
pub fn build_stop_job(pdu_ref: u16) -> Vec<u8> {
    let param: [u8; 16] = [
        0x29, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, b'P', b'_', b'P', b'R', b'O', b'G', b'R', b'A',
        b'M',
    ];
    let mut out = s7_header(ROSCTR_JOB, pdu_ref, 0x0000, param.len(), 0);
    out.extend_from_slice(&param);
    out
}

/// 启动 CPU:hot=true 暖/热启动(Fun 0x28),false 冷启动(SFun 0x4320='C ')。
/// golden(deep-dive §6.3):hot = `28 00*6 FD 00 00 09 P_PROGRAM`(param 0x14);
/// cold = `28 00*6 FD 00 02 43 20 09 P_PROGRAM`(param 0x16)。
pub fn build_start_job(pdu_ref: u16, hot: bool) -> Vec<u8> {
    let mut param: Vec<u8> = vec![0x28, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFD];
    if hot {
        param.extend_from_slice(&[0x00, 0x00, 0x09]);
    } else {
        param.extend_from_slice(&[0x00, 0x02, 0x43, 0x20, 0x09]);
    }
    param.extend_from_slice(b"P_PROGRAM");
    let mut out = s7_header(ROSCTR_JOB, pdu_ref, 0x0000, param.len(), 0);
    out.extend_from_slice(&param);
    out
}

/// 控制作业响应解析:para 首 byte(0x07=already STOP / 0x03=already RUN / 0x02=无法启动)。
pub fn parse_control_response(ack: &S7Ack) -> Result<u8, CoreError> {
    Ok(*ack.param.first().unwrap_or(&0))
}

pub fn control_result_message(code: u8) -> &'static str {
    match code {
        0x07 => "CPU 已处于 STOP(无需再停)",
        0x03 => "CPU 已处于 RUN(无需再启动)",
        0x02 => "CPU 无法启动(钥匙开关在 STOP/保护模式?)",
        0x00 => "成功",
        _ => "未知控制结果码",
    }
}

/// SZL(System Status List)请求(Userdata;ID 0x0424 = CPU 工作模式)。
/// golden(deep-dive §6.4 TIA 抓包):param `00 01 12 04 11 44 01 00` + data `FF 09 00 04 04 24 00 00`。
pub fn build_szl_request(pdu_ref: u16, szl_id: u16, index: u16) -> Vec<u8> {
    let param = [0x00u8, 0x01, 0x12, 0x04, 0x11, 0x44, 0x01, 0x00];
    let data = [0xFFu8, 0x09, 0x00, 0x04, (szl_id >> 8) as u8, szl_id as u8, (index >> 8) as u8, index as u8];
    let mut out = userdata_header(pdu_ref, param.len(), data.len());
    out.extend_from_slice(&param);
    out.extend_from_slice(&data);
    out
}

/// SZL 响应 → 数据负载(去掉 Ret/TS/DLen 头 4B)。
pub fn parse_szl_response(ack: &S7Ack) -> Result<Vec<u8>, CoreError> {
    if ack.data.len() < 8 || ack.data[0] != 0xFF {
        return Err(err("S7_PDU_INVALID", "SZL 响应数据区格式无效"));
    }
    let dlen = u16::from_be_bytes([ack.data[2], ack.data[3]]) as usize;
    Ok(ack.data[4..(4 + dlen).min(ack.data.len())].to_vec())
}

/// SZL 0x0424 → 工作模式字符串。
/// 记录:SZL-ID(2) Index(2) ListLen(2) ListCount(2) + 记录20B,状态字节=记录第4字节(opData[7] 全局)。
pub fn szl_0424_mode(payload: &[u8]) -> &'static str {
    // payload = 04 24 00 00 00 14 00 01 <20B 记录>;记录从 payload[8] 起,状态字节 = 记录第4字节 = payload[11]
    if payload.len() < 12 {
        return "未知(响应不完整)";
    }
    match payload[11] {
        0x08 => "RUN",
        0x04 => "STOP",
        0x03 => "STOP(旧 CPU 编码)",
        other => match other {
            _ if other & 0x08 != 0 => "RUN(带标志)",
            _ if other & 0x04 != 0 => "STOP(带标志)",
            _ => "未知模式字节",
        },
    }
}

/// 密码登录(S7-300/400;Userdata Tg=0x45)。
/// 编码(XOR 0x55 链式,snap7 opSetPassword):Pwd[0]=p[0]^0x55; Pwd[c]=p[c]^0x55^Pwd[c-2];
/// 不足 8 字节以 0x20 补齐。
pub fn build_password_job(pdu_ref: u16, password: &str) -> Vec<u8> {
    let mut raw = password.as_bytes().to_vec();
    raw.resize(8, 0x20);
    let mut pwd = [0u8; 8];
    pwd[0] = raw[0] ^ 0x55;
    pwd[1] = raw[1] ^ 0x55;
    for c in 2..8 {
        pwd[c] = raw[c] ^ 0x55 ^ pwd[c - 2];
    }
    let param = [0x00u8, 0x01, 0x12, 0x04, 0x11, 0x45, 0x01, 0x00];
    let data = [0xFFu8, 0x09, 0x00, 0x08, pwd[0], pwd[1], pwd[2], pwd[3], pwd[4], pwd[5], pwd[6], pwd[7]];
    let mut out = userdata_header(pdu_ref, param.len(), data.len());
    out.extend_from_slice(&param);
    out.extend_from_slice(&data);
    out
}

// ============ 分片预算(§3.8 / deep-dive §3.3) ============

/// Read 单 Item 最大字节数(保守公式 (PDU-31))。
pub fn max_read_bytes(pdu_size: u16) -> usize {
    (pdu_size as i32 - 31).max(1) as usize
}

/// Write 单 Item 最大字节数(公式 (PDU-28))。
pub fn max_write_bytes(pdu_size: u16) -> usize {
    (pdu_size as i32 - 28).max(1) as usize
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::s7_address::{area, transport};

    /// 真实抓包 Setup 请求(python-snap7 3.0 结构,TIA V13 请求 480 的同构帧):
    /// TPKT+COTP 由 s7_cotp 测试;此处为 S7 PDU 段。
    #[test]
    fn builds_setup_request_vector() {
        // param: F0 00 00 01 00 01 01 E0(AMQ 1/1,PDU 480)
        let pdu = build_setup_request(0x0001, 1, 1, 480);
        assert_eq!(
            pdu,
            vec![
                0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0xF0, 0x00, 0x00,
                0x01, 0x00, 0x01, 0x01, 0xE0
            ]
        );
    }

    /// deep-dive §3.1 真实抓包:snap7→S7-300 协商,响应 PDU=0x00F0(240)。
    #[test]
    fn parses_setup_response_from_real_capture() {
        let pdu = vec![
            0x32, 0x03, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x00,
            0x00, 0x01, 0x00, 0x01, 0x00, 0xF0,
        ];
        let ack = parse_ack(&pdu).unwrap();
        assert_eq!(ack.rosctr, ROSCTR_ACK_DATA);
        assert_eq!(ack.error, 0);
        let (amq1, amq2, pdu_len) = parse_setup_response(&ack).unwrap();
        assert_eq!((amq1, amq2, pdu_len), (1, 1, 240));
    }

    /// 读 4 字节 DB1(snap7 字节流风格 TS=0x02)——修正后的示例 3。
    #[test]
    fn builds_read_request_db_bytes() {
        let items = [S7Item::new("DB1.DBB0", 4).unwrap()];
        let pdu = build_read_request(0x0002, &items).unwrap();
        assert_eq!(
            pdu,
            vec![
                0x32, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x0E, 0x00, 0x00, // 头
                0x04, 0x01, // Read,1 item
                0x12, 0x0A, 0x10, 0x02, 0x00, 0x04, 0x00, 0x01, 0x84, 0x00, 0x00, 0x00, // item
            ]
        );
    }

    /// 读位 deep-dive §4.1 抓包向量:DB1.DBX2.0 → item `12 0A 10 01 00 01 00 01 84 00 00 10`。
    #[test]
    fn builds_bit_read_item_from_wincc_capture() {
        let item = S7Item::new("DB1.DBX2.0", 1).unwrap();
        assert_eq!(
            item.encode_any_item(),
            [0x12, 0x0A, 0x10, 0x01, 0x00, 0x01, 0x00, 0x01, 0x84, 0x00, 0x00, 0x10]
        );
    }

    /// deep-dive §4.2 真实抓包(WinCC→S7-300 8-Item 读响应的数据区)。
    /// 验证:无 0x00 保留头、TS=0x04 时 Length=bit、TS=0x03/0x07/0x09 换算、奇数项填充。
    /// 填充规则:奇数字节数据项后跳 1 字节(snap7 C 与 python-snap7 3.0 双源一致,
    /// deep-dive 的文字罗列省略了填充字节)。
    #[test]
    fn parses_wincc_8_item_read_response() {
        let data: Vec<u8> = vec![
            0xFF, 0x03, 0x00, 0x01, 0x01, // Item1 BIT len=1bit data=01
            0x00, // ← 奇数填充
            0xFF, 0x07, 0x00, 0x04, 0xAA, 0xBB, 0xCC, 0xDD, // Item2 REAL len=4byte
            0xFF, 0x04, 0x00, 0x20, 0xFE, 0xAD, 0xBE, 0xEF, // Item3 B/W/DW len=32bit=4B
            0xFF, 0x04, 0x00, 0x10, 0xBA, 0xBE, // Item4 len=16bit=2B
            0xFF, 0x03, 0x00, 0x01, 0x01, // Item5 BIT
            0x00, // ← 奇数填充
            0xFF, 0x03, 0x00, 0x01, 0x01, // Item6 BIT
            0x00, // ← 奇数填充
            0xFF, 0x09, 0x00, 0x02, 0x25, 0x04, // Item7 OCTET len=2B(S5TIME)
            0xFF, 0x09, 0x00, 0x02, 0x00, 0x11, // Item8 OCTET len=2B(Counter)
        ];
        let mut full = vec![
            0x32, ROSCTR_ACK_DATA, 0x00, 0x00, 0x00, 0x07, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00,
            0x04, 0x08,
        ];
        let data_len = (data.len() as u16).to_be_bytes();
        full[8..10].copy_from_slice(&data_len);
        full.extend_from_slice(&data);

        let ack = parse_ack(&full).unwrap();
        assert_eq!(ack.error, 0);
        let items = parse_read_response(&ack).unwrap();
        assert_eq!(items.len(), 8);
        assert_eq!(items[0].data, vec![0x01]);
        assert_eq!(items[1].data, vec![0xAA, 0xBB, 0xCC, 0xDD]);
        assert_eq!(items[2].data, vec![0xFE, 0xAD, 0xBE, 0xEF]);
        assert_eq!(items[3].data, vec![0xBA, 0xBE]);
        assert_eq!(items[4].data, vec![0x01]);
        assert_eq!(items[5].data, vec![0x01]);
        assert_eq!(items[6].data, vec![0x25, 0x04]);
        assert_eq!(items[7].data, vec![0x00, 0x11]);
    }

    /// 修正后的示例 3 响应:读 4 字节 → `FF 04 00 20` + 4 字节数据。
    #[test]
    fn parses_4byte_read_response_bit_length() {
        let pdu = vec![
            0x32, ROSCTR_ACK_DATA, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00, 0x08, 0x00, 0x00,
            0x04, 0x01, 0xFF, 0x04, 0x00, 0x20, 0x12, 0x34, 0x56, 0x78,
        ];
        let ack = parse_ack(&pdu).unwrap();
        let items = parse_read_response(&ack).unwrap();
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].return_code, 0xFF);
        assert_eq!(items[0].data, vec![0x12, 0x34, 0x56, 0x78]);
    }

    /// 错误返回码路径:读不存在的 DB(deep-dive §3.1 抓包回 `0A`)。
    #[test]
    fn read_item_error_code_surface() {
        let pdu = vec![
            0x32, ROSCTR_ACK_DATA, 0x00, 0x00, 0x00, 0x05, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00,
            0x04, 0x01, 0x0A, 0x00, 0x00, 0x00,
        ];
        let ack = parse_ack(&pdu).unwrap();
        let items = parse_read_response(&ack).unwrap();
        assert_eq!(items[0].return_code, 0x0A);
        assert!(items[0].data.is_empty());
        assert!(item_return_code_message(0x0A).contains("优化块"));
    }

    /// 写请求:4 字节 DB1(修正后的示例 4——数据项 `00 04 00 20`)。
    #[test]
    fn builds_write_request_vector() {
        let items = [S7Item::new("DB1.DBB0", 4).unwrap()];
        let data = [vec![0x12, 0x34, 0x56, 0x78u8]];
        let pdu = build_write_request(0x0003, &items, &data).unwrap();
        assert_eq!(
            pdu,
            vec![
                0x32, 0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0x0E, 0x00, 0x08, // 头
                0x05, 0x01, // Write,1 item
                0x12, 0x0A, 0x10, 0x02, 0x00, 0x04, 0x00, 0x01, 0x84, 0x00, 0x00, 0x00, // item
                0x00, 0x04, 0x00, 0x20, // data item:占位 + TS=0x04 + 32bit
                0x12, 0x34, 0x56, 0x78,
            ]
        );
    }

    /// deep-dive §4.2 真实抓包:写 M1.0=0 → 数据项 `00 03 00 01 00`。
    #[test]
    fn builds_bit_write_from_capture() {
        let items = [S7Item::new("M1.0", 1).unwrap()];
        let data = [vec![0x00u8]];
        let pdu = build_write_request(0x0001, &items, &data).unwrap();
        // 参数项:TS=01,len=1,DB=0,Area=83(M),Addr=0x08(1*8)
        let expect_item = [0x12u8, 0x0A, 0x10, 0x01, 0x00, 0x01, 0x00, 0x00, 0x83, 0x00, 0x00, 0x08];
        assert_eq!(&pdu[12..24], &expect_item);
        // 数据项
        assert_eq!(&pdu[24..29], &[0x00, 0x03, 0x00, 0x01, 0x00]);
    }

    /// 写响应:每项 1 字节 RC(deep-dive §3.7 示例 4 响应同构)。
    #[test]
    fn parses_write_response() {
        let pdu = vec![
            0x32, ROSCTR_ACK_DATA, 0x00, 0x00, 0x00, 0x03, 0x00, 0x02, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x01, 0xFF,
        ];
        let ack = parse_ack(&pdu).unwrap();
        let rcs = parse_write_response(&ack).unwrap();
        assert_eq!(rcs, vec![0xFF]);
    }

    /// Timer 项:item TS=0x1D、Address 直接编号。
    #[test]
    fn builds_timer_item() {
        let item = S7Item::new("T5", 1).unwrap();
        assert_eq!(item.addr.area, area::TIMER);
        assert_eq!(item.encode_any_item()[3], transport::TIMER);
        // Address 字段 = 5(不乘 8)
        assert_eq!(&item.encode_any_item()[9..12], &[0x00, 0x00, 0x05]);
        // Timer 数据项 TS=0x09(OCTET)
        let data = [vec![0x25u8, 0x04]];
        let pdu = build_write_request(1, &[item], &data).unwrap();
        // TS=0x09(OCTET) 的 Length 单位是字节(snap7:仅非 Octet/Real/Bit 才 ×8)
        assert_eq!(&pdu[24..28], &[0x00, 0x09, 0x00, 0x02]);
    }

    /// 多 Item 请求 + Item 上限。
    #[test]
    fn multi_item_and_limit() {
        let items: Vec<S7Item> = (0..20)
            .map(|i| S7Item::new(&format!("DB1.DBW{}", i * 2), 1).unwrap())
            .collect();
        assert!(build_read_request(1, &items).is_ok());
        let too_many: Vec<S7Item> = (0..21).map(|i| S7Item::new(&format!("M{}", i), 1).unwrap()).collect();
        assert!(build_read_request(1, &too_many).is_err());
    }

    /// 数据长度不匹配被拒绝。
    #[test]
    fn write_length_mismatch_rejected() {
        let items = [S7Item::new("MW0", 1).unwrap()];
        let bad = [vec![0x12u8, 0x34, 0x56]]; // 3 字节 ≠ 2
        assert!(build_write_request(1, &items, &bad).is_err());
    }

    /// 头部长度自校验:字段与实际不符报错。
    #[test]
    fn header_length_self_check() {
        let mut pdu = build_setup_request(1, 1, 1, 480);
        pdu[7] = 0x09; // param_len 改坏
        assert!(parse_ack(&pdu).is_err());
    }

    /// 头级错误码 → 人话。
    #[test]
    fn header_error_messages() {
        assert!(header_error_message(0x8500).contains("PDU"));
        assert!(header_error_message(0xD241).contains("密码"));
        assert!(header_error_message(0x8104).contains("1200/1500"));
    }

    /// 分片预算。
    // === SW1:控制/SZL/密码 golden 向量(deep-dive §6 抓包) ===

    #[test]
    fn stop_job_matches_capture() {
        let pdu = build_stop_job(0x0002);
        // TPKT+COTP 后:32 01 00 00 00 02 00 10 00 00 + 29 ...
        assert_eq!(&pdu[..10], &[0x32, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00, 0x00]);
        assert_eq!(&pdu[10..26], &[0x29, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, b'P', b'_', b'P', b'R', b'O', b'G', b'R', b'A', b'M']);
    }

    #[test]
    fn start_jobs_match_capture() {
        let hot = build_start_job(1, true);
        assert_eq!(&hot[6..8], &[0x00, 0x14]); // param len 20
        assert_eq!(&hot[10..20], &[0x28, 0, 0, 0, 0, 0, 0, 0xFD, 0x00, 0x00]);
        assert_eq!(&hot[20], &0x09);
        let cold = build_start_job(1, false);
        assert_eq!(&cold[6..8], &[0x00, 0x16]); // param len 22
        assert_eq!(&cold[20..22], &[0x43, 0x20]); // SFun 'C '(param 偏移10)
    }

    #[test]
    fn szl_request_matches_tia_capture() {
        let pdu = build_szl_request(3, 0x0424, 0);
        // golden(TIA goOnline[4]):param 00 01 12 04 11 44 01 00 + data FF 09 00 04 04 24 00 00
        assert_eq!(&pdu[10..18], &[0x00, 0x01, 0x12, 0x04, 0x11, 0x44, 0x01, 0x00]);
        assert_eq!(&pdu[18..26], &[0xFF, 0x09, 0x00, 0x04, 0x04, 0x24, 0x00, 0x00]);
        assert_eq!(pdu[1], ROSCTR_USERDATA);
    }

    #[test]
    fn szl_response_parse_and_mode() {
        // 构造完整 SZL 响应数据区(golden 结构 §6.4):FF 09 00 1C + 04 24 00 00 00 14 00 01 + 20B 记录(RUN)
        let mut data = vec![0xFFu8, 0x09, 0x00, 0x1C, 0x04, 0x24, 0x00, 0x00, 0x00, 0x14, 0x00, 0x01];
        data.extend_from_slice(&[0x51, 0x44, 0xFF, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        let full = {
            let mut h = vec![0x32u8, ROSCTR_USERDATA, 0x00, 0x00, 0x00, 0x03, 0x00, 0x0C, 0x00, 0x20, 0x00, 0x00];
            h.extend_from_slice(&[0x00, 0x01, 0x12, 0x08, 0x12, 0x84, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00]);
            h.extend_from_slice(&data);
            h
        };
        let ack = parse_ack(&full).unwrap();
        let payload = parse_szl_response(&ack).unwrap();
        assert_eq!(szl_0424_mode(&payload), "RUN");
    }

    #[test]
    fn password_encoding_matches_snap7() {
        // 编码规则自 snap7 opSetPassword:校验链式 XOR 的自洽性与帧结构
        let pdu = build_password_job(6, "abc");
        assert_eq!(pdu[1], ROSCTR_USERDATA);
        assert_eq!(&pdu[10..18], &[0x00, 0x01, 0x12, 0x04, 0x11, 0x45, 0x01, 0x00]); // Tg=0x45
        assert_eq!(&pdu[18..22], &[0xFF, 0x09, 0x00, 0x08]);
        // 手工推 pwd:raw = ['a','b','c',0x20*5]
        let raw = [b'a', b'b', b'c', 0x20, 0x20, 0x20, 0x20, 0x20];
        let mut expect = [0u8; 8];
        expect[0] = raw[0] ^ 0x55;
        expect[1] = raw[1] ^ 0x55;
        for c in 2..8 {
            expect[c] = raw[c] ^ 0x55 ^ expect[c - 2];
        }
        assert_eq!(&pdu[22..30], &expect);
    }

    #[test]
    fn pdu_budgets() {
        assert_eq!(max_read_bytes(240), 209);
        assert_eq!(max_read_bytes(480), 449);
        assert_eq!(max_write_bytes(240), 212);
        assert_eq!(max_write_bytes(480), 452);
    }

    /// 奇数字节项(非末项)在写请求里补 1 字节,读响应解析跳过。
    #[test]
    fn odd_byte_padding_roundtrip() {
        let items = [S7Item::new("DB1.DBB0", 1).unwrap(), S7Item::new("DB1.DBB1", 2).unwrap()];
        let data = [vec![0xAAu8], vec![0xBB, 0xCC]];
        let pdu = build_write_request(1, &items, &data).unwrap();
        // 头 10 + 参数(2+24) = 36 之后是数据区;数据区 = 4+1+1 + 4+2 = 12
        assert_eq!(pdu[8..10], [0x00, 0x0C]);
        assert_eq!(&pdu[36..41], &[0x00u8, 0x04, 0x00, 0x08, 0xAA]);
        assert_eq!(pdu[41], 0x00); // 填充
        assert_eq!(&pdu[42..48], &[0x00u8, 0x04, 0x00, 0x10, 0xBB, 0xCC]);
    }

    #[test]
    fn smart_v_read_via_db1() {
        // SMART VW100 = DB1.DBW100:Item 应指向 DB1、偏移 100
        let item = S7Item::new("VW100", 2).unwrap();
        assert_eq!(item.addr.db, 1);
        assert_eq!(&item.encode_any_item()[9..12], &[0x00, 0x03, 0x20]); // 100<<3 = 800 = 0x320
    }
}
