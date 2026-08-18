//! 西门子 S7comm 地址解析器。
//!
//! 解析富文本地址(如 "M0.0"、"DB1.DBW20"、"VW100")为结构化地址,
//! 供 AnyPointer(12 字节地址项)编码器使用。
//!
//! 规范来源:《西门子全协议设计文档.md》§5(存储区/绝对寻址/DB 寻址/SMART V 区映射)。
//!
//! 关键规则:
//! - Area 码:I=0x81 Q=0x82 M=0x83 DB=0x84 T=0x1D C=0x1C P(外设)=0x80
//! - AnyPointer 地址字段 = `(byte << 3) | bit`(3 字节**大端**);
//!   Timer/Counter 区例外:直接填编号(**不乘 8**)
//! - 西门子地址编号一律十进制(无 MC 协议的八进制/十六进制区)
//! - S7-200 SMART:V 区映射 DB1(VB100=DB1.DBB100, VW100=DB1.DBW100, V100.3=DB1.DBX100.3)

use crate::error::CoreError;

/// 存储区代码(AnyPointer 第 8 字节)。
pub mod area {
    pub const PERIPHERAL: u8 = 0x80;
    pub const INPUTS: u8 = 0x81;
    pub const OUTPUTS: u8 = 0x82;
    pub const MARKERS: u8 = 0x83;
    pub const DB: u8 = 0x84;
    pub const COUNTER: u8 = 0x1C;
    pub const TIMER: u8 = 0x1D;
    // S7-200 家族专属区(deep-dive §1.2 / Wireshark S7COMM_AREA_*)
    pub const SYS_FLAGS_SM: u8 = 0x05;
    pub const ANALOG_INPUT_AI: u8 = 0x06;
    pub const ANALOG_OUTPUT_AQ: u8 = 0x07;
}

/// 请求侧 TransportSize(deep-dive §4.1 真值表,snap7 S7WL* 一致)。
pub mod transport {
    pub const BIT: u8 = 0x01;
    pub const BYTE: u8 = 0x02;
    pub const WORD: u8 = 0x04;
    pub const DWORD: u8 = 0x06;
    pub const REAL: u8 = 0x08;
    pub const COUNTER: u8 = 0x1C;
    pub const TIMER: u8 = 0x1D;
    // S7-200 家族专属区(deep-dive §1.2 / Wireshark S7COMM_AREA_*)
    pub const SYS_FLAGS_SM: u8 = 0x05;
    pub const ANALOG_INPUT_AI: u8 = 0x06;
    pub const ANALOG_OUTPUT_AQ: u8 = 0x07;
}

/// 地址访问粒度(由语法中的宽度字母决定)。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum S7Kind {
    /// 单位:1 位(M0.0 / DB1.DBX0.0 / V100.3)
    Bit,
    /// 单位:1 字节(MB10 / VB100)
    Byte,
    /// 单位:2 字节(MW10 / VW100)
    Word,
    /// 单位:4 字节(MD10 / VD100)
    Dword,
    /// S5TIME,2 字节,Address=编号(T5)
    Timer,
    /// 计数值,2 字节,Address=编号(C5)
    Counter,
}

impl S7Kind {
    /// 单元素位宽(bit)。
    pub fn elem_bits(self) -> u32 {
        match self {
            S7Kind::Bit => 1,
            S7Kind::Byte => 8,
            S7Kind::Word | S7Kind::Timer | S7Kind::Counter => 16,
            S7Kind::Dword => 32,
        }
    }

    /// 单元素字节数(位访问向上取整为 1 字节)。
    pub fn elem_bytes(self) -> u32 {
        (self.elem_bits() + 7) / 8
    }

    /// 请求侧 TransportSize 代码。
    ///
    /// 协议层统一走「位 / 字节流」两种粒度(对齐 snap7 read_area/write_area 语义):
    /// 宽度解释(WORD/DWORD/REAL)留给值解码层,最大限度规避两套 TS 编码的坑。
    pub fn transport_size(self) -> u8 {
        match self {
            S7Kind::Bit => transport::BIT,
            S7Kind::Timer => transport::TIMER,
            S7Kind::Counter => transport::COUNTER,
            _ => transport::BYTE,
        }
    }
}

/// 解析后的 S7 地址。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct S7Address {
    /// Area 码(见 [`area`] 模块)
    pub area: u8,
    /// DB 号(非 DB 区为 0)
    pub db: u16,
    /// 字节偏移(Timer/Counter 区为编号)
    pub byte: u32,
    /// 位偏移(0-7,仅 Bit 粒度有意义)
    pub bit: u8,
    /// 访问粒度
    pub kind: S7Kind,
}

impl S7Address {
    /// 编码 AnyPointer 的 3 字节地址字段(大端)。
    ///
    /// 普通区:`(byte << 3) | bit`;Timer/Counter 区:直接编号。
    pub fn encode_any_address(&self) -> [u8; 3] {
        let linear: u32 = if matches!(self.kind, S7Kind::Timer | S7Kind::Counter) {
            self.byte
        } else {
            (self.byte << 3) | self.bit as u32
        };
        [(linear >> 16) as u8, (linear >> 8) as u8, linear as u8]
    }

    /// 用户可读形式(报文面板/日志用)。
    pub fn display(&self) -> String {
        if self.area == area::SYS_FLAGS_SM {
            return match self.kind {
                S7Kind::Bit => format!("SM{}.{}", self.byte, self.bit),
                _ => format!("SM{}{}", width_letter(self.kind), self.byte),
            };
        }
        if self.area == area::ANALOG_INPUT_AI {
            return format!("AIW{}", self.byte);
        }
        if self.area == area::ANALOG_OUTPUT_AQ {
            return format!("AQW{}", self.byte);
        }
        match self.kind {
            S7Kind::Timer => format!("T{}", self.byte),
            S7Kind::Counter => format!("C{}", self.byte),
            S7Kind::Bit if self.area == area::DB => {
                format!("DB{}.DBX{}.{}", self.db, self.byte, self.bit)
            }
            S7Kind::Bit => format!("{}{}.{}", area_letter(self.area), self.byte, self.bit),
            _ if self.area == area::DB => {
                format!("DB{}.DB{}{}", self.db, width_letter(self.kind), self.byte)
            }
            _ => format!("{}{}{}", area_letter(self.area), width_letter(self.kind), self.byte),
        }
    }
}

/// Area 码 → 中文名(JSONL 输出用)。
pub fn area_name(a: u8) -> &'static str {
    match a {
        area::INPUTS => "输入区 I",
        area::OUTPUTS => "输出区 Q",
        area::MARKERS => "位存储区 M",
        area::DB => "数据块 DB",
        area::TIMER => "定时器 T",
        area::COUNTER => "计数器 C",
        area::PERIPHERAL => "外设区 PI/PQ",
        area::SYS_FLAGS_SM => "系统标志 SM(200 家族)",
        area::ANALOG_INPUT_AI => "模拟输入 AI(200 家族)",
        area::ANALOG_OUTPUT_AQ => "模拟输出 AQ(200 家族)",
        _ => "未知区域",
    }
}

fn area_letter(a: u8) -> &'static str {
    match a {
        area::INPUTS => "I",
        area::OUTPUTS => "Q",
        area::MARKERS => "M",
        area::PERIPHERAL => "P",
        _ => "?",
    }
}

fn width_letter(kind: S7Kind) -> char {
    match kind {
        S7Kind::Byte => 'B',
        S7Kind::Word => 'W',
        S7Kind::Dword => 'D',
        _ => 'X',
    }
}

fn invalid(msg: &str) -> CoreError {
    CoreError::Modbus {
        code: "S7_ADDRESS_INVALID",
        message: msg.to_string(),
        details: None,
    }
}

/// 解析富文本地址为结构化 S7 地址。
///
/// 支持语法(§5.2/§5.3 + SMART §12):
/// - 位:`M0.0` `I10.3` `Q4.1` `DB1.DBX0.0` `V100.3`(SMART)
/// - 字节:`MB10` `IB0` `QB4` `DB1.DBB10` `VB100`(SMART)
/// - 字:`MW10` `IW0` `QW4` `DB1.DBW20` `VW100`(SMART)
/// - 双字:`MD10` `ID0` `QD4` `DB1.DBD30` `VD100`(SMART)
/// - 定时器/计数器:`T5` `C12`(编号,十进制)
/// - 外设区:`PIW256` `PID256` `PQW256` `PQD256`(Area=0x80)
/// - 宽容:`M10`(无宽度字母与位后缀 → M10.0 位)
pub fn parse_s7_address(input: &str) -> Result<S7Address, CoreError> {
    let s = input.trim();
    if s.is_empty() {
        return Err(invalid("地址为空"));
    }
    let upper = s.to_ascii_uppercase();

    // --- DB 语法:DBn.DB[X|B|W|D]m[.b] ---
    if let Some(rest) = upper.strip_prefix("DB") {
        let (db_str, tail) = rest
            .split_once(".")
            .ok_or_else(|| invalid(&format!("「{s}」缺少 DB 成员(应形如 DB1.DBW20)")))?;
        let db: u16 = parse_u32(db_str, 65535, s)? as u16;
        let tail = tail.strip_prefix("DB").unwrap_or(tail);
        // DBX0.0 位形式:X 宽度字母 → 位访问
        let tail = match tail.strip_prefix('X') {
            Some(rest) if rest.starts_with(|c: char| c.is_ascii_digit()) => rest,
            _ => tail,
        };
        return parse_member(tail, area::DB, db, s);
    }

    // --- SMART V 区:V[B|W|D]m 或 Vm.b → 映射 DB1 ---
    if let Some(rest) = upper.strip_prefix('V') {
        if rest.starts_with(|c: char| c.is_ascii_digit() || c == 'B' || c == 'W' || c == 'D') {
            return parse_member(rest, area::DB, 1, s);
        }
    }

    // --- 外设区 PI/PQ[B|W|D]m ---
    for (prefix, code) in [("PI", area::PERIPHERAL), ("PQ", area::PERIPHERAL)] {
        if let Some(rest) = upper.strip_prefix(prefix) {
            if rest.starts_with(|c: char| c.is_ascii_digit() || c == 'B' || c == 'W' || c == 'D') {
                return parse_member(rest, code, 0, s);
            }
        }
    }

    // --- M/I/Q 区:[M|I|Q]m.b / [M|I|Q][B|W|D]m ---
    for (prefix, code) in [("M", area::MARKERS), ("I", area::INPUTS), ("Q", area::OUTPUTS)] {
        if let Some(rest) = upper.strip_prefix(prefix) {
            if rest.starts_with(|c: char| c.is_ascii_digit() || c == 'B' || c == 'W' || c == 'D') {
                return parse_member(rest, code, 0, s);
            }
        }
    }

    // --- S7-200 家族:SM(系统标志,位+字)/AI/AQ(模拟量,仅字) ---
    if let Some(rest) = upper.strip_prefix("SM") {
        if rest.starts_with(|c: char| c.is_ascii_digit() || c == 'B' || c == 'W' || c == 'D') {
            return parse_member(rest, area::SYS_FLAGS_SM, 0, s);
        }
    }
    for (prefix, code) in [("AIW", area::ANALOG_INPUT_AI), ("AI", area::ANALOG_INPUT_AI),
                           ("AQW", area::ANALOG_OUTPUT_AQ), ("AQ", area::ANALOG_OUTPUT_AQ)] {
        if let Some(rest) = upper.strip_prefix(prefix) {
            if !rest.is_empty() && rest.chars().all(|c| c.is_ascii_digit()) {
                // 模拟量仅支持字访问(AIW0/AQW4;带宽度字母的 AIW 是语法噪声,直接按字)
                let n = parse_u32(rest, 0xFFFFF, s)?;
                return Ok(S7Address { area: code, db: 0, byte: n, bit: 0, kind: S7Kind::Word });
            }
        }
    }

    // --- Timer/Counter:Tn / Cn ---
    for (prefix, code, kind) in [
        ("T", area::TIMER, S7Kind::Timer),
        ("C", area::COUNTER, S7Kind::Counter),
    ] {
        if let Some(rest) = upper.strip_prefix(prefix) {
            if !rest.is_empty() && rest.chars().all(|c| c.is_ascii_digit()) {
                let num = parse_u32(rest, 65535, s)?;
                return Ok(S7Address { area: code, db: 0, byte: num, bit: 0, kind });
            }
        }
    }

    Err(invalid(&format!(
        "无法识别的 S7 地址「{s}」(示例:M0.0 / MW10 / DB1.DBW20 / T5 / VW100)"
    )))
}

/// 解析区前缀之后的成员部分:`[B|W|D]数字[.位]` 或 `数字[.位]`。
fn parse_member(tail: &str, area_code: u8, db: u16, original: &str) -> Result<S7Address, CoreError> {
    // 宽度字母(可缺省 → 位访问)
    let (kind, num_part) = if let Some(rest) = tail.strip_prefix('B') {
        (S7Kind::Byte, rest)
    } else if let Some(rest) = tail.strip_prefix('W') {
        (S7Kind::Word, rest)
    } else if let Some(rest) = tail.strip_prefix('D') {
        (S7Kind::Dword, rest)
    } else {
        (S7Kind::Bit, tail)
    };

    let (num_str, bit_str) = match num_part.split_once('.') {
        Some((n, b)) => (n, Some(b)),
        None => (num_part, None),
    };
    if num_str.is_empty() || !num_str.chars().all(|c| c.is_ascii_digit()) {
        return Err(invalid(&format!("「{original}」地址编号「{num_str}」不合法(应为十进制数字)")));
    }
    let byte = parse_u32(num_str, 0xFFFFF, original)?;

    // 位后缀:仅位访问合法(字/双字带位后缀是语法错误)
    let bit = match bit_str {
        Some(b) if kind == S7Kind::Bit => {
            if b.len() != 1 || !b.chars().all(|c| c.is_ascii_digit()) || b.parse::<u8>().unwrap() > 7 {
                return Err(invalid(&format!("「{original}」位偏移「{b}」不合法(应为 0-7)")));
            }
            b.parse::<u8>().unwrap()
        }
        Some(_) => return Err(invalid(&format!("「{original}」字节/字/双字访问不能带位后缀"))),
        None => 0,
    };

    Ok(S7Address { area: area_code, db, byte, bit, kind })
}

fn parse_u32(s: &str, max: u32, original: &str) -> Result<u32, CoreError> {
    match s.parse::<u32>() {
        Ok(v) if v <= max => Ok(v),
        Ok(v) => Err(invalid(&format!(
            "「{original}」编号 {v} 超出上限 {max}"
        ))),
        Err(_) => Err(invalid(&format!("「{original}」编号「{s}」不是合法十进制数"))),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_marker_area() {
        let a = parse_s7_address("M0.0").unwrap();
        assert_eq!(a.area, area::MARKERS);
        assert_eq!(a.kind, S7Kind::Bit);
        assert_eq!(a.bit, 0);

        let w = parse_s7_address("MW10").unwrap();
        assert_eq!(w.kind, S7Kind::Word);
        assert_eq!(w.byte, 10);
        assert_eq!(w.bit, 0);

        let d = parse_s7_address("MD10").unwrap();
        assert_eq!(d.kind, S7Kind::Dword);

        // 文档 §3.4b:M10.0 → Address = 0x000050(10×8=80)
        assert_eq!(parse_s7_address("M10.0").unwrap().encode_any_address(), [0x00, 0x00, 0x50]);
        // M10.3 → 0x000053
        assert_eq!(parse_s7_address("M10.3").unwrap().encode_any_address(), [0x00, 0x00, 0x53]);
    }

    #[test]
    fn bare_number_defaults_to_bit() {
        let a = parse_s7_address("M10").unwrap();
        assert_eq!(a.kind, S7Kind::Bit);
        assert_eq!(a.byte, 10);
        assert_eq!(a.bit, 0);
        let i = parse_s7_address("I5").unwrap();
        assert_eq!(i.area, area::INPUTS);
        assert_eq!(i.kind, S7Kind::Bit);
    }

    #[test]
    fn parses_db_area() {
        let x = parse_s7_address("DB1.DBX0.0").unwrap();
        assert_eq!((x.area, x.db, x.kind), (area::DB, 1, S7Kind::Bit));
        assert_eq!(x.encode_any_address(), [0x00, 0x00, 0x00]);

        let b = parse_s7_address("DB1.DBB10").unwrap();
        assert_eq!(b.kind, S7Kind::Byte);
        // §5.3:DB1.DBB10 → Addr = 10<<3 = 80 = 0x50
        assert_eq!(b.encode_any_address(), [0x00, 0x00, 0x50]);

        let w = parse_s7_address("DB100.DBW20").unwrap();
        assert_eq!((w.db, w.kind), (100, S7Kind::Word));
        // §5.3:DB1.DBW20 → Addr = 20<<3 = 160 = 0xA0
        assert_eq!(w.encode_any_address(), [0x00, 0x00, 0xA0]);

        let d = parse_s7_address("DB2.DBD30").unwrap();
        assert_eq!(d.kind, S7Kind::Dword);
        // §5.3:DB1.DBD30 → Addr = 30<<3 = 240 = 0xF0
        assert_eq!(d.encode_any_address(), [0x00, 0x00, 0xF0]);

        // db 成员允许省略 DB 前缀(DB1.W20)
        let short = parse_s7_address("DB1.W20").unwrap();
        assert_eq!(short.kind, S7Kind::Word);
    }

    #[test]
    fn smart_v_area_maps_to_db1() {
        let b = parse_s7_address("VB100").unwrap();
        assert_eq!((b.area, b.db, b.kind, b.byte), (area::DB, 1, S7Kind::Byte, 100));
        let w = parse_s7_address("VW100").unwrap();
        assert_eq!((w.area, w.db, w.kind), (area::DB, 1, S7Kind::Word));
        let d = parse_s7_address("VD100").unwrap();
        assert_eq!(d.kind, S7Kind::Dword);
        let x = parse_s7_address("V100.3").unwrap();
        assert_eq!((x.area, x.db, x.kind, x.byte, x.bit), (area::DB, 1, S7Kind::Bit, 100, 3));
        let bare = parse_s7_address("V100").unwrap();
        assert_eq!(bare.kind, S7Kind::Bit);
    }

    #[test]
    fn parses_timer_counter_with_raw_number_address() {
        let t = parse_s7_address("T5").unwrap();
        assert_eq!((t.area, t.kind, t.byte), (area::TIMER, S7Kind::Timer, 5));
        // T/C 区地址不乘 8(deep-dive §4.1)
        assert_eq!(t.encode_any_address(), [0x00, 0x00, 0x05]);

        let c = parse_s7_address("C12").unwrap();
        assert_eq!(c.area, area::COUNTER);
        assert_eq!(c.encode_any_address(), [0x00, 0x00, 0x0C]);
    }

    #[test]
    fn parses_peripheral_area() {
        let w = parse_s7_address("PIW256").unwrap();
        assert_eq!((w.area, w.kind, w.byte), (area::PERIPHERAL, S7Kind::Word, 256));
        let d = parse_s7_address("PQD256").unwrap();
        assert_eq!((d.area, d.kind), (area::PERIPHERAL, S7Kind::Dword));
        // PI/PQ 无宽度字母 → 位访问(P10.0)也允许(宽容)
        let x = parse_s7_address("PQ4.1").unwrap();
        assert_eq!(x.kind, S7Kind::Bit);
    }

    #[test]
    fn io_areas() {
        let ib = parse_s7_address("IB0").unwrap();
        assert_eq!((ib.area, ib.kind), (area::INPUTS, S7Kind::Byte));
        let qw = parse_s7_address("QW4").unwrap();
        assert_eq!((qw.area, qw.kind), (area::OUTPUTS, S7Kind::Word));
        let id_ = parse_s7_address("ID0").unwrap();
        assert_eq!(id_.kind, S7Kind::Dword);
        let bit = parse_s7_address("I0.0").unwrap();
        assert_eq!(bit.encode_any_address(), [0, 0, 0]);
    }

    #[test]
    fn s7_200_family_sm_ai_aq() {
        // SM 位/字节/字
        let b = parse_s7_address("SMB10").unwrap();
        assert_eq!((b.area, b.kind), (area::SYS_FLAGS_SM, S7Kind::Byte));
        let w = parse_s7_address("SMW10").unwrap();
        assert_eq!(w.kind, S7Kind::Word);
        let x = parse_s7_address("SM1.6").unwrap();
        assert_eq!((x.kind, x.byte, x.bit), (S7Kind::Bit, 1, 6));
        // AI/AQ 仅字(AIW0 与 AI0 等价)
        let ai = parse_s7_address("AIW0").unwrap();
        assert_eq!((ai.area, ai.kind), (area::ANALOG_INPUT_AI, S7Kind::Word));
        assert_eq!(parse_s7_address("AI4").unwrap().byte, 4);
        let aq = parse_s7_address("AQW4").unwrap();
        assert_eq!(aq.area, area::ANALOG_OUTPUT_AQ);
        assert_eq!(aq.display(), "AQW4");
        // display 往返
        assert_eq!(parse_s7_address("SM1.6").unwrap().display(), "SM1.6");
    }

    #[test]
    fn case_insensitive_and_whitespace() {
        assert_eq!(
            parse_s7_address("  db1.dbw20 ").unwrap(),
            parse_s7_address("DB1.DBW20").unwrap()
        );
    }

    #[test]
    fn rejects_bad_input() {
        for bad in ["", "X10", "M", "DB1", "DB1.ZZ", "M1.9", "MW10.3", "D", "V", "T", "PIW", "DBX0.0"] {
            assert!(parse_s7_address(bad).is_err(), "应拒绝「{bad}」");
        }
    }

    #[test]
    fn transport_size_mapping() {
        // 协议层统一:位 0x01 / 字节流 0x02 / T 0x1D / C 0x1C
        assert_eq!(S7Kind::Bit.transport_size(), 0x01);
        assert_eq!(S7Kind::Byte.transport_size(), 0x02);
        assert_eq!(S7Kind::Word.transport_size(), 0x02);
        assert_eq!(S7Kind::Dword.transport_size(), 0x02);
        assert_eq!(S7Kind::Timer.transport_size(), 0x1D);
        assert_eq!(S7Kind::Counter.transport_size(), 0x1C);
        // 元素宽度
        assert_eq!(S7Kind::Dword.elem_bytes(), 4);
        assert_eq!(S7Kind::Timer.elem_bytes(), 2);
    }

    #[test]
    fn display_roundtrip() {
        for input in ["M10.3", "MB0", "MW10", "MD10", "DB1.DBX0.0", "DB5.DBD30", "T5", "C12", "IB0"] {
            let a = parse_s7_address(input).unwrap();
            assert_eq!(a.display(), input.to_ascii_uppercase(), "display 应与输入一致");
        }
        // SMART V 地址显示为 DB 形式
        assert_eq!(parse_s7_address("VW100").unwrap().display(), "DB1.DBW100");
    }
}
