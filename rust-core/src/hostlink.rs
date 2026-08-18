//! 欧姆龙 HostLink(C-Mode/FINS)串口协议帧层。
//!
//! 帧格式(公开规范,欧姆龙 W342/FINS 手册):
//! - 起始:`@`(0x40)
//! - 站号:2 字符 ASCII 十六进制(00-31)
//! - 头代码(FINS):2 字符 `FA`(计算机→PLC)/`FA`(PLC→计算机)
//! - 数据:FINS 帧(ICF..SID + 服务)的 ASCII hex 表示(每字节 2 字符,大端)
//! - FCS:2 字符 ASCII 十六进制(从 @ 到数据末尾的 XOR)
//! - 结束:`*`(0x2A) + CR(0x0D) + LF(0x0A)
//!
//! 也支持传统 C-Mode 命令(如 `@00RR01000001` 读 DM)——本模块以 FINS-over-HostLink 为主。
//!
//! 串口:RS-232/422/485,默认 9600 7E2 或 115200 7E2(看 CPU 型号)。

use crate::error::CoreError;

fn hl_err(msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code: "HOSTLINK_INVALID", message: msg.into(), details: None }
}

/// FCS:对 ASCII 数据区(从 @ 到 FCS 前)逐字节 XOR。
pub fn fcs(data: &[u8]) -> u8 {
    data.iter().fold(0u8, |a, b| a ^ b)
}

/// 构建 HostLink FINS 请求帧(计算机 → PLC)。
///
/// `station`: PLC 站号(0-31)
/// `fins_frame`: 已构建的 FINS 应用帧(二进制)
pub fn build_hostlink_fins(station: u8, fins_frame: &[u8]) -> Vec<u8> {
    let mut body = Vec::new();
    body.push(b'@');
    body.extend_from_slice(format!("{:02X}", station & 0x1F).as_bytes());
    body.extend_from_slice(b"FA"); // FINS 头代码
    // FINS 帧转 ASCII hex(大端)
    for b in fins_frame {
        body.extend_from_slice(format!("{:02X}", b).as_bytes());
    }
    // FCS(从 @ 到数据末尾的 XOR)转 2 字符 ASCII
    let checksum = fcs(&body);
    body.extend_from_slice(format!("{:02X}", checksum).as_bytes());
    body.push(b'*');
    body.push(0x0D); // CR
    body.push(0x0A); // LF
    body
}

/// 解析 HostLink FINS 应答帧(PLC → 计算机)。
///
/// 返回 FINS 应用帧(二进制)。
pub fn parse_hostlink_fins(frame: &[u8]) -> Result<Vec<u8>, CoreError> {
    // 剥 CR/LF
    let f = if frame.last() == Some(&0x0A) { &frame[..frame.len() - 2] } else { frame };
    let f = if f.last() == Some(&0x0D) { &f[..f.len() - 1] } else { f };

    if f.len() < 8 || f[0] != b'@' {
        return Err(hl_err("不是 HostLink 帧(起始非 @)"));
    }
    if f.last() != Some(&b'*') {
        return Err(hl_err("帧尾非 *"));
    }

    // @站号(2) + 头代码(2) + FINS数据 + FCS(2) + *
    let data_end = f.len() - 3; // 减去 FCS(2) + *(1)
    let fcs_bytes = &f[data_end..data_end + 2];
    let data_area = &f[..data_end];
    let calc = fcs(data_area);

    let fcs_str = String::from_utf8_lossy(fcs_bytes).to_string();
    let expected = u8::from_str_radix(&fcs_str, 16)
        .map_err(|_| hl_err(format!("FCS「{fcs_str}」非十六进制")))?;
    if calc != expected {
        return Err(hl_err(format!("FCS 校验失败:帧内 0x{expected:02X},计算 0x{calc:02X}")));
    }

    // 头代码检查(响应也是 FA)
    let hdr = String::from_utf8_lossy(&f[3..5]).to_string();
    if hdr != "FA" {
        return Err(hl_err(format!("头代码「{hdr}」不是 FINS(FA)")));
    }

    // FINS 数据区(ASCII hex → 二进制)
    let fins_ascii = &f[5..data_end];
    if fins_ascii.len() % 2 != 0 {
        return Err(hl_err("FINS 数据区长度为奇数(非合法 ASCII hex)"));
    }
    let mut fins = Vec::with_capacity(fins_ascii.len() / 2);
    for pair in fins_ascii.chunks_exact(2) {
        let hex = String::from_utf8_lossy(pair).to_string();
        let byte = u8::from_str_radix(&hex, 16)
            .map_err(|_| hl_err(format!("FINS 数据「{hex}」非十六进制")))?;
        fins.push(byte);
    }
    Ok(fins)
}

/// 构建 C-Mode 读 DM 命令(传统 HostLink,非 FINS)。
///
/// `station`: 站号; `dm_start`: DM 起始字; `word_count`: 读字数
pub fn build_cmode_read_dm(station: u8, dm_start: u16, word_count: u16) -> Vec<u8> {
    let mut body = Vec::new();
    body.push(b'@');
    body.extend_from_slice(format!("{:02X}", station & 0x1F).as_bytes());
    body.extend_from_slice(b"RR"); // 读 DM 区
    body.extend_from_slice(format!("{:04X}", dm_start).as_bytes());
    body.extend_from_slice(format!("{:04X}", word_count).as_bytes());
    let checksum = fcs(&body);
    body.extend_from_slice(format!("{:02X}", checksum).as_bytes());
    body.push(b'*');
    body.push(0x0D);
    body.push(0x0A);
    body
}

/// 解析 C-Mode 读 DM 应答(返回数据字列表)。
pub fn parse_cmode_read_dm(frame: &[u8]) -> Result<Vec<u16>, CoreError> {
    let f = if frame.last() == Some(&0x0A) { &frame[..frame.len() - 2] } else { frame };
    let f = if f.last() == Some(&0x0D) { &f[..f.len() - 1] } else { f };
    if f.len() < 9 || f[0] != b'@' {
        return Err(hl_err("不是 C-Mode 应答帧"));
    }
    // @站号(2) + 头代码RR(2) + 结束码(2) + 数据 + FCS(2) + *
    let end_code = String::from_utf8_lossy(&f[5..7]).to_string();
    if end_code != "00" {
        return Err(hl_err(format!("C-Mode 结束码 {end_code}(非 00)")));
    }
    let data_end = f.len() - 3;
    let data_ascii = &f[7..data_end];
    if data_ascii.len() % 4 != 0 {
        return Err(hl_err("数据区长度非 4 的倍数"));
    }
    let mut words = Vec::with_capacity(data_ascii.len() / 4);
    for quad in data_ascii.chunks_exact(4) {
        let hex = String::from_utf8_lossy(quad).to_string();
        let val = u16::from_str_radix(&hex, 16)
            .map_err(|_| hl_err(format!("数据「{hex}」非十六进制")))?;
        words.push(val);
    }
    Ok(words)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fins_roundtrip() {
        // FINS 帧:ICF=0x80 RSV=0 GCT=2 DNA=0 DA1=0 DA2=0 SNA=0 SA1=0 SA2=0 SID=1 + 0101(读)+82(DM)+00 00 64(100)+00 02(2字)
        let fins = vec![0x80, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
                        0x01, 0x01, 0x01, 0x82, 0x00, 0x00, 0x64, 0x00, 0x02];
        let frame = build_hostlink_fins(0, &fins);
        assert_eq!(frame[0], b'@');
        assert_eq!(frame[1], b'0'); assert_eq!(frame[2], b'0'); // 站 00
        assert_eq!(frame[3], b'F'); assert_eq!(frame[4], b'A'); // FINS 头
        // FINS 数据是 ASCII hex(每字节 2 字符)
        assert_eq!(frame.len(), 1 + 2 + 2 + fins.len() * 2 + 2 + 1 + 2); // @+站+FA+FINShex+FCS+*+CRLF
        // 解析回来
        let parsed = parse_hostlink_fins(&frame).unwrap();
        assert_eq!(parsed, fins);
    }

    #[test]
    fn fcs_xor() {
        let data = b"@00FA80000200000000000001";
        let checksum = fcs(data);
        // 手工验算
        let expected: u8 = data.iter().fold(0u8, |a, b| a ^ b);
        assert_eq!(checksum, expected);
    }

    #[test]
    fn cmode_read_dm_roundtrip() {
        let req = build_cmode_read_dm(0, 100, 2);
        assert_eq!(req[0], b'@');
        assert_eq!(&req[3..5], b"RR");
        assert_eq!(&req[5..9], b"0064"); // DM 100
        assert_eq!(&req[9..13], b"0002"); // 2 字
        // 构造应答
        let mut resp = Vec::new();
        resp.push(b'@');
        resp.extend_from_slice(b"00"); // 站
        resp.extend_from_slice(b"RR"); // 命令回显
        resp.extend_from_slice(b"00"); // 结束码=成功
        resp.extend_from_slice(b"1234"); // DM100=0x1234
        resp.extend_from_slice(b"ABCD"); // DM101=0xABCD
        let ck = fcs(&resp);
        resp.extend_from_slice(format!("{:02X}", ck).as_bytes());
        resp.push(b'*');
        resp.push(0x0D);
        resp.push(0x0A);
        let words = parse_cmode_read_dm(&resp).unwrap();
        assert_eq!(words, vec![0x1234, 0xABCD]);
    }

    #[test]
    fn corrupted_fcs_rejected() {
        let fins = vec![0x80, 0x00, 0x02, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0x82, 0, 0, 0, 0, 2];
        let mut frame = build_hostlink_fins(0, &fins);
        // 篡改 FCS(倒数第 4、3 字节)
        let n = frame.len();
        frame[n - 4] = b'F'; frame[n - 3] = b'F';
        assert!(parse_hostlink_fins(&frame).is_err());
    }

    #[test]
    fn station_and_header_validation() {
        let fins = vec![0x80, 0x00, 0x02, 0, 0, 0, 0, 0, 0, 1];
        let frame = build_hostlink_fins(5, &fins);
        assert_eq!(&frame[1..3], b"05"); // 站号 05
        // 头代码改坏 → 解析报错
        let mut bad = frame.clone();
        bad[4] = b'B'; // FB 而非 FA
        assert!(parse_hostlink_fins(&bad).is_err());
    }
}
