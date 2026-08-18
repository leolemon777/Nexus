//! PROFINET DCP(Discovery and basic Configuration Protocol)帧构建与解析。
//!
//! 以太网二层,EtherType 0x8892,组播 MAC 01:0E:CF:00:00:00。
//! 用于:扫描发现 PROFINET 设备 / 读写设备名与 IP / 闪灯定位。
//!
//! DCP 帧(Identify Multicast):
//! EtherHeader(14B) + DCP(服务ID 1B + 服务类型 1B + XID 4B + 响应延迟 2B + 数据长度 2B + 数据块…)
//!
//! 数据块(DataBlock): Option 1B + SubOption 1B + Length 2B + Data…(可选 padding)
//!
//! 关键 Option/SubOption:
//! - 0xFF 0xFF: 全部(Identify All 请求)
//! - 0x02 0x01: 设备名(NameOfStation)
//! - 0x01 0x02: IP 参数(IP+掩码+网关)
//! - 0x02 0x03: 设备 ID(厂商+型号)
//! - 0x01 0x01: MAC 地址
//! - 0x05 0x02: 设备角色(IO Device / IO Controller / IO Supervisor)
//!
//! ServiceID: 0x05=Identify, 0x04=Set, 0x03=Get, 0x06=Hello
//! ServiceType: 0x00=请求, 0x01=成功响应

use crate::error::CoreError;

pub const ETHERTYPE_PROFINET: u16 = 0x8892;
pub const ETHERTYPE_LLDP: u16 = 0x88CC;
pub const DCP_MULTICAST_MAC: [u8; 6] = [0x01, 0x0E, 0xCF, 0x00, 0x00, 0x00];

// ServiceID
pub const SERVICE_IDENTIFY: u8 = 0x05;
pub const SERVICE_SET: u8 = 0x04;
pub const SERVICE_GET: u8 = 0x03;
pub const SERVICE_HELLO: u8 = 0x06;

// ServiceType
pub const SERVICE_TYPE_REQUEST: u8 = 0x00;
pub const SERVICE_TYPE_SUCCESS: u8 = 0x01;

fn dcp_err(msg: impl Into<String>) -> CoreError {
    CoreError::Modbus { code: "DCP_INVALID", message: msg.into(), details: None }
}

/// DCP 帧头(不含 Ethernet 头,即 DCP 协议体)
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DcpHeader {
    pub service_id: u8,
    pub service_type: u8,
    pub xid: u32,
    pub response_delay: u16,
    pub data_length: u16,
}

impl DcpHeader {
    pub fn encode(&self) -> [u8; 10] {
        let mut out = [0u8; 10];
        out[0] = self.service_id;
        out[1] = self.service_type;
        out[2..6].copy_from_slice(&self.xid.to_be_bytes());
        out[6..8].copy_from_slice(&self.response_delay.to_be_bytes());
        out[8..10].copy_from_slice(&self.data_length.to_be_bytes());
        out
    }
}

/// DCP 数据块(Option + SubOption + Length + Data)
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DcpBlock {
    pub option: u8,
    pub sub_option: u8,
    pub data: Vec<u8>,
}

impl DcpBlock {
    pub fn encode(&self) -> Vec<u8> {
        let mut out = Vec::with_capacity(4 + self.data.len());
        out.push(self.option);
        out.push(self.sub_option);
        out.extend_from_slice(&(self.data.len() as u16).to_be_bytes());
        out.extend_from_slice(&self.data);
        // Padding to even length
        if out.len() % 2 != 0 {
            out.push(0);
        }
        out
    }
}

/// 构建 Identify All 多播请求(扫描全网 PROFINET 设备)。
pub fn build_identify_all(xid: u32) -> Vec<u8> {
    let block = DcpBlock { option: 0xFF, sub_option: 0xFF, data: vec![] };
    let block_bytes = block.encode();
    let header = DcpHeader {
        service_id: SERVICE_IDENTIFY,
        service_type: SERVICE_TYPE_REQUEST,
        xid,
        response_delay: 0, // 多播请求不延迟
        data_length: block_bytes.len() as u16,
    };
    let mut out = Vec::with_capacity(10 + block_bytes.len());
    out.extend_from_slice(&header.encode());
    out.extend_from_slice(&block_bytes);
    out
}

/// 构建 Set 设备名请求(需指定 MAC)。
pub fn build_set_name(xid: u32, name: &str) -> Result<Vec<u8>, CoreError> {
    if name.is_empty() || name.len() > 240 {
        return Err(dcp_err(format!("设备名长度 {} 不合法(1-240)", name.len())));
    }
    let block = DcpBlock {
        option: 0x02,
        sub_option: 0x01,
        data: name.as_bytes().to_vec(),
    };
    let block_bytes = block.encode();
    let header = DcpHeader {
        service_id: SERVICE_SET,
        service_type: SERVICE_TYPE_REQUEST,
        xid,
        response_delay: 0,
        data_length: block_bytes.len() as u16,
    };
    let mut out = Vec::with_capacity(10 + block_bytes.len());
    out.extend_from_slice(&header.encode());
    out.extend_from_slice(&block_bytes);
    Ok(out)
}

/// 构建 Set IP 参数请求(静态 IP)。
pub fn build_set_ip(xid: u32, ip: [u8; 4], mask: [u8; 4], gateway: [u8; 4]) -> Vec<u8> {
    let mut data = vec![0x00]; // 0=静态
    data.extend_from_slice(&ip);
    data.extend_from_slice(&mask);
    data.extend_from_slice(&gateway);
    let block = DcpBlock { option: 0x01, sub_option: 0x02, data };
    let block_bytes = block.encode();
    let header = DcpHeader {
        service_id: SERVICE_SET,
        service_type: SERVICE_TYPE_REQUEST,
        xid,
        response_delay: 0,
        data_length: block_bytes.len() as u16,
    };
    let mut out = Vec::with_capacity(10 + block_bytes.len());
    out.extend_from_slice(&header.encode());
    out.extend_from_slice(&block_bytes);
    out
}

/// 构建闪灯(Factory Reset / Blink)请求。
pub fn build_blink_led(xid: u32) -> Vec<u8> {
    let block = DcpBlock { option: 0x05, sub_option: 0x03, data: vec![0x00, 0x00, 0x00, 0x01] };
    let block_bytes = block.encode();
    let header = DcpHeader {
        service_id: SERVICE_SET,
        service_type: SERVICE_TYPE_REQUEST,
        xid,
        response_delay: 0,
        data_length: block_bytes.len() as u16,
    };
    let mut out = Vec::with_capacity(10 + block_bytes.len());
    out.extend_from_slice(&header.encode());
    out.extend_from_slice(&block_bytes);
    out
}

/// 解析 DCP Identify 响应(多个数据块)。
#[derive(Debug, Clone, Default)]
pub struct DcpDevice {
    pub name: Option<String>,
    pub ip: Option<[u8; 4]>,
    pub mask: Option<[u8; 4]>,
    pub gateway: Option<[u8; 4]>,
    pub mac: Option<[u8; 6]>,
    pub vendor_id: Option<u16>,
    pub device_id: Option<u16>,
    pub role: Option<String>,
    pub blocks: Vec<(u8, u8, Vec<u8>)>, // (option, sub_option, raw_data)
}

pub fn parse_identify_response(frame: &[u8]) -> Result<DcpDevice, CoreError> {
    if frame.len() < 12 {
        return Err(dcp_err("DCP 帧过短"));
    }
    let mut dev = DcpDevice::default();
    let mut off = 10; // 跳过 DCP 头
    while off + 4 <= frame.len() {
        let option = frame[off];
        let sub = frame[off + 1];
        let len = u16::from_be_bytes([frame[off + 2], frame[off + 3]]) as usize;
        let data_start = off + 4;
        let data_end = (data_start + len).min(frame.len());
        let data = &frame[data_start..data_end];

        match (option, sub) {
            (0x02, 0x01) => {
                dev.name = String::from_utf8(data.to_vec()).ok().filter(|s| !s.is_empty());
            }
            (0x01, 0x02) if data.len() >= 13 => {
                // data[0]=IP 方式(0=静态,1=DHCP,2=自动,3=本地)
                dev.ip = Some([data[1], data[2], data[3], data[4]]);
                dev.mask = Some([data[5], data[6], data[7], data[8]]);
                dev.gateway = Some([data[9], data[10], data[11], data[12]]);
            }
            (0x01, 0x01) if data.len() >= 6 => {
                dev.mac = Some([data[0], data[1], data[2], data[3], data[4], data[5]]);
            }
            (0x02, 0x03) if data.len() >= 4 => {
                dev.vendor_id = Some(u16::from_be_bytes([data[0], data[1]]));
                dev.device_id = Some(u16::from_be_bytes([data[2], data[3]]));
            }
            (0x05, 0x02) if data.len() >= 1 => {
                dev.role = match data[0] {
                    0x00 => Some("PNIO Controller".into()),
                    0x01 => Some("PNIO Device".into()),
                    0x02 => Some("PNIO Supervisor".into()),
                    _ => Some(format!("Unknown(0x{:02X})", data[0])),
                };
            }
            _ => {}
        }
        dev.blocks.push((option, sub, data.to_vec()));
        // 对齐到偶数
        let padded = if len % 2 != 0 { len + 1 } else { len };
        off = data_start + padded;
    }
    Ok(dev)
}

// ============ LLDP 解析(邻居发现) ============

#[derive(Debug, Clone, Default)]
pub struct LldpInfo {
    pub system_name: Option<String>,
    pub port_id: Option<String>,
    pub port_description: Option<String>,
    pub system_description: Option<String>,
}

pub fn parse_lldp(frame: &[u8]) -> Result<LldpInfo, CoreError> {
    if frame.len() < 2 {
        return Err(dcp_err("LLDP 帧过短"));
    }
    let mut info = LldpInfo::default();
    let mut off = 0;
    while off + 2 <= frame.len() {
        let type_hi = frame[off];
        let type_lo = frame[off + 1];
        let tlv_type = (type_hi >> 1) & 0x7F;
        let tlv_len = (((type_hi & 0x01) as usize) << 8) | type_lo as usize;
        let data = &frame[off + 2..(off + 2 + tlv_len).min(frame.len())];
        match tlv_type {
            0x00 => break,                   // End of LLDPDU
            0x05 => {                        // System Name
                info.system_name = String::from_utf8(data.to_vec()).ok();
            }
            0x04 => {                        // Port Description
                info.port_description = String::from_utf8(data.to_vec()).ok();
            }
            0x06 => {                        // System Description
                info.system_description = String::from_utf8(data.to_vec()).ok();
            }
            0x02 => {                        // Port ID (subtype + value)
                if data.len() > 1 {
                    info.port_id = String::from_utf8(data[1..].to_vec()).ok();
                }
            }
            _ => {}
        }
        off += 2 + tlv_len;
    }
    Ok(info)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn identify_all_request_structure() {
        let frame = build_identify_all(0x00000001);
        // DCP 头 10B + FF FF 00 00 = 14B
        assert_eq!(frame.len(), 14);
        assert_eq!(frame[0], SERVICE_IDENTIFY);
        assert_eq!(frame[1], SERVICE_TYPE_REQUEST);
        assert_eq!(&frame[2..6], &[0, 0, 0, 1]); // XID
        // 数据块:FF FF 00 00(全部,长度 0)
        assert_eq!(&frame[10..14], &[0xFF, 0xFF, 0x00, 0x00]);
    }

    #[test]
    fn set_name_request() {
        let frame = build_set_name(1, "my-plc").unwrap();
        assert_eq!(frame[0], SERVICE_SET);
        // 数据块:02 01 00 05 "my-plc"
        assert_eq!(&frame[10..12], &[0x02, 0x01]); // Option=设备名
        assert_eq!(&frame[14..20], b"my-plc"); // 6 字节名字
    }

    #[test]
    fn set_ip_request() {
        let frame = build_set_ip(1, [192, 168, 1, 10], [255, 255, 255, 0], [192, 168, 1, 1]);
        assert_eq!(frame[0], SERVICE_SET);
        // 数据:01 02 00 0D(块长13) 00(静态) 192.168.1.10 255.255.255.0 192.168.1.1
        let dl = u16::from_be_bytes([frame[8], frame[9]]);
        assert_eq!(dl, 18); // 块头 4 + 数据 13 + padding 1(奇数补偶)
        assert_eq!(frame[14], 0x00); // 静态
    }

    #[test]
    fn parse_identify_response_with_name_and_ip() {
        // 构造响应:头(10B) + 块1 设备名(02 01 00 05 "test1") + 块2 IP(01 02 00 0D 00 192.168.0.1 255.255.255.0 192.168.0.254)
        let mut f = vec![SERVICE_IDENTIFY, SERVICE_TYPE_SUCCESS, 0, 0, 0, 1, 0, 0, 0, 0];
        f.extend_from_slice(&[0x02, 0x01, 0x00, 0x05]);
        f.extend_from_slice(b"test1");
        f.push(0x00); // padding(name 长度 5 是奇数)
        f.extend_from_slice(&[0x01, 0x02, 0x00, 0x0D]);
        f.extend_from_slice(&[0x00, 192, 168, 0, 1, 255, 255, 255, 0, 192, 168, 0, 254]);
        f.push(0x00); // padding(13 是奇数,补 1B)
        let dev = parse_identify_response(&f).unwrap();
        assert_eq!(dev.name, Some("test1".into()));
        assert_eq!(dev.ip, Some([192, 168, 0, 1]));
        assert_eq!(dev.mask, Some([255, 255, 255, 0]));
        assert_eq!(dev.gateway, Some([192, 168, 0, 254]));
    }

    #[test]
    fn lldp_parse_system_name() {
        // TLV: System Name(type=5, len=4, "PLC1") + End(type=0, len=0)
        let mut f = vec![];
        f.push((5 << 1) as u8 & 0xFE); // type=5, no length high bit
        f.push(4); // length low
        f.extend_from_slice(b"PLC1");
        f.push(0); f.push(0); // End TLV
        let info = parse_lldp(&f).unwrap();
        assert_eq!(info.system_name, Some("PLC1".into()));
    }
}
