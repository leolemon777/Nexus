//! 三菱 MC 协议 3E/4E 帧层——把指令数据区包装成完整线路帧,并解析响应帧。
//!
//! 规范来源:《三菱全协议设计文档.md》§2.1(3E 帧字段表)、§2.2(4E 帧)。
//!
//! 3E 请求帧:
//! `[50 00][网络号][PLC号][模块IO 2B][站号][数据长度 2B][监视定时器 2B][指令数据区...]`
//! 3E 响应帧:
//! `[D0 00][路由回显 5B][响应长度 2B][结束代码 2B][响应数据...]`
//! 4E 帧在副帧头后多 2B 序列号(请求 54 00 / 响应 D4 00)。

use crate::error::CoreError;

/// 副帧头
pub const SUBHEADER_3E_REQUEST: u16 = 0x0050; // 线路字节 50 00
pub const SUBHEADER_3E_RESPONSE: u16 = 0x00D0; // 线路字节 D0 00
pub const SUBHEADER_4E_REQUEST: u16 = 0x0054; // 线路字节 54 00
pub const SUBHEADER_4E_RESPONSE: u16 = 0x00D4; // 线路字节 D4 00

/// 帧类型
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FrameType {
    Type3E,
    Type4E,
}

/// 访问路径(路由字段,§2.1.2 字段 2~5)
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AccessRoute {
    /// 网络编号(直连 0x00)
    pub network_no: u8,
    /// PLC 编号(直连 CPU 0xFF)
    pub pc_no: u8,
    /// 请求目标模块 I/O 号(内置以太网口 0x03FF)
    pub module_io: u16,
    /// 请求目标站号(0x00)
    pub station_no: u8,
}

impl Default for AccessRoute {
    fn default() -> Self {
        Self {
            network_no: 0x00,
            pc_no: 0xFF,
            module_io: 0x03FF,
            station_no: 0x00,
        }
    }
}

/// 构建完整请求帧。
///
/// `req_data` 是 mc_pdu 产出的 `[指令][子命令]...` 数据区。
/// 监视定时器单位 250ms,默认 0x0010 = 4 秒。
///
/// 示例(读 D100,文档 §2.1.4-(2) 完整 21 字节帧):
/// `50 00 00 FF FF 03 00 0C 00 10 00 01 04 01 00 64 00 00 A8 01 00`
pub fn build_request_frame(
    frame: FrameType,
    route: &AccessRoute,
    watchdog: u16,
    req_data: &[u8],
    sequence: u16,
) -> Vec<u8> {
    // 请求数据长度 = 监视定时器(2) + 指令数据区
    let data_len = (2 + req_data.len()) as u16;
    let mut buf = Vec::with_capacity(11 + req_data.len() + 2);
    match frame {
        FrameType::Type3E => buf.extend_from_slice(&SUBHEADER_3E_REQUEST.to_le_bytes()),
        FrameType::Type4E => {
            buf.extend_from_slice(&SUBHEADER_4E_REQUEST.to_le_bytes());
            buf.extend_from_slice(&sequence.to_le_bytes());
        }
    }
    buf.push(route.network_no);
    buf.push(route.pc_no);
    buf.extend_from_slice(&route.module_io.to_le_bytes());
    buf.push(route.station_no);
    buf.extend_from_slice(&data_len.to_le_bytes());
    buf.extend_from_slice(&watchdog.to_le_bytes());
    buf.extend_from_slice(req_data);
    buf
}

/// 解析后的 MC 响应。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McResponse {
    pub frame_type: FrameType,
    pub sequence: u16,
    /// 结束代码:0x0000 正常
    pub end_code: u16,
    /// 结束代码之后的应答数据
    pub data: Vec<u8>,
}

/// 解析响应帧。
///
/// 校验副帧头、长度字段一致性,提取结束代码与数据区。
/// 返回 `Ok(Err(end_code))` 语义用 end_code != 0 表达,调用方自行判断。
pub fn parse_response_frame(buf: &[u8]) -> Result<McResponse, CoreError> {
    // 4E 响应头 11B(2+2+5+2),3E 响应头 9B(2+5+2),再加结束码 2B
    if buf.len() < 11 {
        return Err(CoreError::Modbus {
            code: "MC_RESPONSE_TOO_SHORT",
            message: format!("响应帧 {} 字节,短于最小长度 11", buf.len()),
            details: None,
        });
    }

    let subheader = u16::from_le_bytes([buf[0], buf[1]]);
    let (frame_type, seq, route_len) = match subheader {
        s if s == SUBHEADER_3E_RESPONSE => (FrameType::Type3E, 0u16, 0usize),
        s if s == SUBHEADER_4E_RESPONSE => {
            let seq = u16::from_le_bytes([buf[2], buf[3]]);
            (FrameType::Type4E, seq, 2)
        }
        other => {
            return Err(CoreError::Modbus {
                code: "MC_BAD_SUBHEADER",
                message: format!("响应副帧头 {other:#06x} 不是 D000/D400"),
                details: None,
            })
        }
    };

    // 布局:[副帧头 2][4E序列号 2][路由 5][长度 2][结束代码 2][数据]
    let len_off = 2 + route_len + 5;
    let resp_len = u16::from_le_bytes([buf[len_off], buf[len_off + 1]]) as usize;
    let end_code = u16::from_le_bytes([buf[len_off + 2], buf[len_off + 3]]);
    let data_start = len_off + 4;
    let data = buf[data_start..].to_vec();

    // 长度自校验:长度字段应 = 结束代码(2) + 数据区
    if resp_len != 2 + data.len() {
        return Err(CoreError::Modbus {
            code: "MC_LENGTH_MISMATCH",
            message: format!("响应长度字段 {resp_len} 与实际(2+{}={})不符", data.len(), 2 + data.len()),
            details: None,
        });
    }

    Ok(McResponse {
        frame_type,
        sequence: seq,
        end_code,
        data,
    })
}

/// 结束代码 → 人类可读错误(§8 常见码)。
pub fn end_code_message(end_code: u16) -> String {
    match end_code {
        0x0000 => "正常".into(),
        0xC059 => "监视定时器超时/请求不可接受".into(),
        0xC016 => "重复指令异常".into(),
        0x0004 => "异常结束".into(),
        0x0005 => "无法接收(串口链路异常)".into(),
        0x0006 => "CPU 无法执行(运行中受限)".into(),
        0x0007 => "无法识别指令".into(),
        0x0008 => "软元件编号/代码越界".into(),
        0x0009 => "CPU 响应超时".into(),
        0x00D0 => "软元件代码非法".into(),
        0x00D1 => "点数设置错误".into(),
        0x00D2 => "头软元件编号非法".into(),
        other => format!("未知错误 {other:#06x}"),
    }
}

// ============================================================================
// 请求帧解析(供虚拟从站使用)与响应帧构建
// ============================================================================

/// 解析后的 MC 请求帧(原始视图)。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McRequestFrame {
    pub frame_type: FrameType,
    pub sequence: u16,
    pub route: AccessRoute,
    /// 监视定时器
    pub watchdog: u16,
    /// 指令(如 0x0401)
    pub command: u16,
    /// 子命令(0000 位 / 0001 字)
    pub subcommand: u16,
    /// 指令数据区(指令+子命令之后的部分)
    pub data: Vec<u8>,
}

/// 解析 3E/4E 请求帧(虚拟从站入口)。
pub fn parse_request_frame(buf: &[u8]) -> Result<McRequestFrame, CoreError> {
    if buf.len() < 11 {
        return Err(CoreError::Modbus {
            code: "MC_REQUEST_TOO_SHORT",
            message: format!("请求帧 {} 字节,短于最小长度", buf.len()),
            details: None,
        });
    }
    let subheader = u16::from_le_bytes([buf[0], buf[1]]);
    let (frame_type, seq, route_off) = match subheader {
        s if s == SUBHEADER_3E_REQUEST => (FrameType::Type3E, 0u16, 2usize),
        s if s == SUBHEADER_4E_REQUEST => {
            let seq = u16::from_le_bytes([buf[2], buf[3]]);
            (FrameType::Type4E, seq, 4)
        }
        other => {
            return Err(CoreError::Modbus {
                code: "MC_BAD_SUBHEADER",
                message: format!("请求副帧头 {other:#06x} 不是 5000/5400"),
                details: None,
            })
        }
    };

    // [路由 5B][长度 2B][监视定时器 2B][指令 2B][子命令 2B][数据...]
    let mut off = route_off;
    let route = AccessRoute {
        network_no: buf[off],
        pc_no: buf[off + 1],
        module_io: u16::from_le_bytes([buf[off + 2], buf[off + 3]]),
        station_no: buf[off + 4],
    };
    off += 5;
    let data_len = u16::from_le_bytes([buf[off], buf[off + 1]]) as usize;
    off += 2;
    let watchdog = u16::from_le_bytes([buf[off], buf[off + 1]]);
    off += 2;
    let command = u16::from_le_bytes([buf[off], buf[off + 1]]);
    off += 2;
    let subcommand = u16::from_le_bytes([buf[off], buf[off + 1]]);
    off += 2;
    let data = buf[off..].to_vec();

    // 长度自校验:长度字段 = 监视定时器(2) + 指令(2) + 子命令(2) + 数据区
    let expected_len = 6 + data.len();
    if data_len != expected_len {
        return Err(CoreError::Modbus {
            code: "MC_LENGTH_MISMATCH",
            message: format!("请求长度字段 {data_len} 与实际({expected_len})不符"),
            details: None,
        });
    }

    Ok(McRequestFrame {
        frame_type,
        sequence: seq,
        route,
        watchdog,
        command,
        subcommand,
        data,
    })
}

/// 构建响应帧(虚拟从站出口)。
pub fn build_response_frame(
    frame_type: FrameType,
    sequence: u16,
    end_code: u16,
    data: &[u8],
) -> Vec<u8> {
    // 响应路由 = 请求路由回显(虚拟从站用默认值,与文档抓包一致:00 FF FF 03 00)
    let resp_len = (2 + data.len()) as u16;
    let mut buf = Vec::with_capacity(11 + data.len());
    match frame_type {
        FrameType::Type3E => buf.extend_from_slice(&SUBHEADER_3E_RESPONSE.to_le_bytes()),
        FrameType::Type4E => {
            buf.extend_from_slice(&crate::mc_frame::SUBHEADER_4E_RESPONSE.to_le_bytes());
            buf.extend_from_slice(&sequence.to_le_bytes());
        }
    }
    // 路由回显(默认直连路由)
    buf.extend_from_slice(&[0x00, 0xFF, 0xFF, 0x03, 0x00]);
    buf.extend_from_slice(&resp_len.to_le_bytes());
    buf.extend_from_slice(&end_code.to_le_bytes());
    buf.extend_from_slice(data);
    buf
}

// ⚠️ ASCII 帧编码在 `mc_ascii.rs`——不是逐字节转写:
// 多字节字段按逻辑值大端呈现(长度 0C 00 → "000C"),软元件地址 6 字符高位对齐。
// 详见文档 §2.3.1/§2.3.2。

#[cfg(test)]
mod tests {
    use super::*;
    use crate::mc_address::parse_mc_address;
    use crate::mc_pdu::build_read_batch_pdu;

    /// 文档 §2.1.4-(2) 完整请求帧:读 D100 1 字 = 21 字节
    #[test]
    fn full_read_d100_frame_matches_doc() {
        let addr = parse_mc_address("D100").unwrap();
        let req_data = build_read_batch_pdu(&addr, 1).unwrap();
        let frame = build_request_frame(
            FrameType::Type3E,
            &AccessRoute::default(),
            0x0010,
            &req_data,
            0,
        );
        assert_eq!(
            frame,
            [0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x0C, 0x00, 0x10, 0x00,
             0x01, 0x04, 0x01, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00]
        );
    }

    /// 文档 §2.1.4-(2) 响应帧:D100 = 0x1234
    #[test]
    fn parse_response_d100_1234() {
        let resp = [
            0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x04, 0x00, 0x00, 0x00, 0x34, 0x12,
        ];
        let parsed = parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.frame_type, FrameType::Type3E);
        assert_eq!(parsed.end_code, 0x0000);
        assert_eq!(parsed.data, vec![0x34, 0x12]);
    }

    /// 文档 §2.1.4-(3) 写响应:仅结束码,无数据
    #[test]
    fn parse_write_response_no_data() {
        let resp = [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00];
        let parsed = parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.end_code, 0x0000);
        assert!(parsed.data.is_empty());
    }

    /// 文档 §2.1.4-(1) 抓包响应:读 D7000 起返回 5 字
    #[test]
    fn parse_capture_response_d7000() {
        let resp = [
            0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x0C, 0x00, 0x00, 0x00,
            0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        let parsed = parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.data.len(), 10); // 5 字 = 10 字节
        // 第一个字 = 0x000C(小端 0C 00)
        assert_eq!(parsed.data[0], 0x0C);
        assert_eq!(parsed.data[1], 0x00);
    }

    #[test]
    fn error_end_code_is_extracted() {
        // C059:超时
        let resp = [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x59, 0xC0];
        let parsed = parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.end_code, 0xC059);
        assert!(end_code_message(0xC059).contains("超时"));
    }

    #[test]
    fn rejects_bad_subheader() {
        let resp = [0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00];
        assert!(parse_response_frame(&resp).is_err());
    }

    #[test]
    fn rejects_short_frame() {
        assert!(parse_response_frame(&[0xD0, 0x00]).is_err());
    }

    #[test]
    fn length_field_mismatch_detected() {
        // 长度字段说 2 但带了数据 → 报错(防粘包错位)
        let resp = [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00, 0x34];
        assert!(parse_response_frame(&resp).is_err());
    }

    /// 4E 帧:副帧头 5400 + 序列号,往返一致
    #[test]
    fn frame_4e_roundtrip_with_sequence() {
        let addr = parse_mc_address("D0").unwrap();
        let req_data = build_read_batch_pdu(&addr, 1).unwrap();
        let frame = build_request_frame(
            FrameType::Type4E,
            &AccessRoute::default(),
            0x0010,
            &req_data,
            0x1234,
        );
        // 副帧头 54 00 + 序列号 34 12
        assert_eq!(&frame[..4], &[0x54, 0x00, 0x34, 0x12]);
        // 4E 帧长度偏移后移 2:副帧头(2)+序列号(2)+路由(5)=9,长度在 [9..11]
        let len = u16::from_le_bytes([frame[9], frame[10]]);
        assert_eq!(len, 0x0C);

        // 构造 4E 响应并解析
        let mut resp = vec![0xD4, 0x00, 0x34, 0x12, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x04, 0x00, 0x00, 0x00, 0x34, 0x12];
        let parsed = parse_response_frame(&resp).unwrap();
        assert_eq!(parsed.sequence, 0x1234);
        assert_eq!(parsed.end_code, 0x0000);
        resp.clear();
    }

    #[test]
    fn custom_route_is_encoded() {
        let route = AccessRoute {
            network_no: 0x01,
            pc_no: 0x02,
            module_io: 0x0000,
            station_no: 0x03,
        };
        let frame = build_request_frame(FrameType::Type3E, &route, 0x0010, &[0x01, 0x04], 0);
        // [50 00] 01 02 00 00 03 [长度]...
        assert_eq!(&frame[2..7], &[0x01, 0x02, 0x00, 0x00, 0x03]);
    }
}
