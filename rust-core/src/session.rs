//! 会话状态 —— 管理 TCP/UDP 连接的生命周期。
//!
//! 引入此模块是阶段 1 的架构转折点:Rust core 从纯 codec 升级为协议引擎,
//! 持有 socket 并执行端到端事务。串口路径仍由 Electron 持有句柄(不在此管理)。

use std::collections::HashMap;
use std::io::{Read, Write};
use std::net::{TcpStream, UdpSocket};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde_json::{Value, json};

use crate::error::CoreError;
use crate::modbus_slave::SlaveMemory;
use crate::modbus_tcp::{self, MBAP_HEADER_LEN, MbapHeader, TransactionIdGenerator};

/// TCP 默认读取超时。
const TCP_READ_TIMEOUT: Duration = Duration::from_secs(5);

/// TCP 帧包装模式。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TcpFraming {
    /// 标准 Modbus TCP:MBAP 头(7B,含 unit_id)+ PDU,无 CRC。
    Standard,
    /// RTU over TCP:完整 RTU ADU(unit_id + PDU + CRC16),无 MBAP 头。
    RtuOverTcp,
    /// ASCII over TCP:ASCII 帧(:hex(unit_id + PDU + LRC)CRLF),无 MBAP 头。
    AsciiOverTcp,
}

/// 一条已建立的连接。
pub enum Connection {
    Tcp {
        stream: TcpStream,
        unit_id: u8,
        framing: TcpFraming,
    },
    Udp {
        socket: UdpSocket,
        peer_addr: std::net::SocketAddr,
        unit_id: u8,
        framing: TcpFraming,
    },
    /// 三菱 MC 协议 TCP 连接(3E/4E 帧,Binary 或 ASCII 编码)
    McTcp {
        stream: TcpStream,
        route: crate::mc_frame::AccessRoute,
        frame_type: crate::mc_frame::FrameType,
        /// true = ASCII 编码(TCP 5001);false = Binary(TCP 5000)
        ascii: bool,
        watchdog: u16,
        sequence: u16,
    },
    /// 三菱 A-1E / SLMP-1E over TCP(A 系列 E71 / FX3U-ENET / FX5U 兼容模式,§3.4)
    Mc1eTcp { stream: TcpStream },
    /// 西门子 PPI over TCP(串口服务器透传/仿真;双拍确认)
    PpiTcp {
        stream: TcpStream,
        /// PLC 站号(2 默认)
        station: u8,
        /// 主站(PC)站号 0
        master: u8,
    },
    /// 西门子 Fetch/Write(S5 兼容)裸 TCP 连接
    FwTcp { stream: TcpStream },
    /// 欧姆龙 FINS/TCP 连接(端口 9600;握手后 SEND 帧长连接,SID 递增)
    FinsTcp {
        stream: TcpStream,
        nodes: crate::fins_frame::FinsNodes,
        sid: u8,
    },
    /// 欧姆龙 FINS/UDP 连接(裸应用帧)
    FinsUdp {
        socket: UdpSocket,
        peer_addr: std::net::SocketAddr,
        nodes: crate::fins_frame::FinsNodes,
        sid: u8,
    },
    /// 西门子 S7comm over ISO-on-TCP 连接(TCP 102;握手后长连接,pdu_ref 递增配对)
    S7Tcp {
        stream: TcpStream,
        /// Setup 协商出的 PDU 长度(读写分片预算)
        pdu_size: u16,
        /// PDU Reference 序号(每 Job 递增)
        pdu_ref: u16,
    },
    /// 三菱 MC 协议 UDP 连接(§2.5:SLMP/MC 3E/4E 可直接跑 UDP;4E 序列号用于丢包配对)
    McUdp {
        socket: UdpSocket,
        peer_addr: std::net::SocketAddr,
        route: crate::mc_frame::AccessRoute,
        frame_type: crate::mc_frame::FrameType,
        watchdog: u16,
        sequence: u16,
    },
}

/// GE SRTP TCP session.  It is kept separate from the Modbus/MC connection
/// enum because SRTP has its own fixed 56-byte session handshake and response
/// framing; routing it through a generic Modbus connection would hide those
/// protocol invariants.
struct GeSrtpConnection {
    stream: TcpStream,
    transaction_id: u16,
}

/// Fuji MICREX-SX SPH Loader Command TCP session.  SPH has no separate
/// handshake; the connection is usable after TCP connect and each response
/// is self-described by the 20-byte header payload length.
struct FujiSphConnection {
    stream: TcpStream,
    connection_id: u8,
}

/// FATEK FBs native ASCII TCP session.  There is no separate handshake;
/// responses are terminated by ETX and are validated by the codec.
struct FatekConnection {
    stream: TcpStream,
    station: u8,
}

/// Keyence KV Host Link ASCII TCP session.  The CR/CR NN handshake is
/// completed before the connection is exposed to the read-only methods.
struct KeyenceConnection {
    stream: TcpStream,
    station: Option<u8>,
}

/// LS Electric XGT FEnet TCP session.  XGT has no preamble handshake; the
/// 20-byte application header and InvokeId bind each read response.
struct LsXgtConnection {
    stream: TcpStream,
    cpu: u8,
    base_no: u8,
    slot_no: u8,
    company_id: String,
    invoke_id: u16,
}

/// Beckhoff ADS/AMS TCP session.  AMS Route creation remains outside this
/// boundary; the peer must already expose a routable ADS endpoint.
struct AdsConnection {
    stream: TcpStream,
    target_net_id: [u8; crate::ads::AMS_NET_ID_BYTES],
    target_port: u16,
    source_net_id: [u8; crate::ads::AMS_NET_ID_BYTES],
    source_port: u16,
    invoke_id: u32,
}

/// Allen-Bradley EtherNet/IP explicit-message TCP session.  The live boundary
/// registers an encapsulation session and exposes only unconnected CIP reads.
struct EnipConnection {
    stream: TcpStream,
    session_handle: u32,
    sender_context: u64,
}

/// MQTT 3.1.1 broker TCP session.  The live path only subscribes and decodes
/// broker PUBLISH packets; it never publishes a value or command.
struct MqttConnection {
    stream: TcpStream,
    packet_id: u16,
}

/// IEC 60870-5-104 TCP client/master session.  Only monitoring-direction
/// general interrogation is exposed by the first Rust boundary.
struct Iec104Connection {
    stream: TcpStream,
    send_sequence: u16,
    receive_sequence: u16,
    common_address: u16,
    originator_address: u8,
}

/// DNP3/TCP read-only master association. The first boundary uses
/// unconfirmed link user data and serializes application transactions.
struct Dnp3Connection {
    stream: TcpStream,
    master_address: u16,
    outstation_address: u16,
    application_sequence: u8,
    transport_sequence: u8,
}

/// BACnet/IPv4 UDP read-only session. The socket is connected to a single
/// peer; Who-Is is sent as Original-Unicast and each ReadProperty response
/// must echo the active Invoke ID and object/property reference.
struct BacnetConnection {
    socket: UdpSocket,
    invoke_id: u8,
}

/// KNXnet/IP Tunneling v1 UDP client session. The first live boundary only
/// exposes connection setup, GroupValueRead, and disconnect; writes and scene
/// control remain absent.
struct KnxConnection {
    socket: UdpSocket,
    local_ip: [u8; 4],
    local_port: u16,
    channel_id: u8,
    outgoing_sequence: u8,
    expected_incoming_sequence: u8,
    keepalive_enabled: bool,
    keepalive_interval_ms: u32,
    next_keepalive: std::time::Instant,
}

/// 轮询流配置。
pub struct PollStream {
    pub stream_id: String,
    pub connection_id: String,
    pub fc: u8,
    pub start_address: u16,
    pub quantity: u16,
    pub interval_ms: u32,
    pub next_due: std::time::Instant,
}

/// 会话状态 —— 所有 JSONL 命令共享此结构。
pub struct Session {
    connections: HashMap<String, Connection>,
    ge_srtp_connections: HashMap<String, GeSrtpConnection>,
    fuji_sph_connections: HashMap<String, FujiSphConnection>,
    fatek_connections: HashMap<String, FatekConnection>,
    keyence_connections: HashMap<String, KeyenceConnection>,
    ls_xgt_connections: HashMap<String, LsXgtConnection>,
    ads_connections: HashMap<String, AdsConnection>,
    enip_connections: HashMap<String, EnipConnection>,
    mqtt_connections: HashMap<String, MqttConnection>,
    iec104_connections: HashMap<String, Iec104Connection>,
    dnp3_connections: HashMap<String, Dnp3Connection>,
    bacnet_connections: HashMap<String, BacnetConnection>,
    knx_connections: HashMap<String, KnxConnection>,
    tid_gen: TransactionIdGenerator,
    // 从站管理:slave_id → (内存区, 停止标志)
    slaves: HashMap<String, (Arc<Mutex<SlaveMemory>>, Arc<Mutex<bool>>)>,
    // 轮询流管理:stream_id → PollStream
    poll_streams: HashMap<String, PollStream>,
    // 串口从站:slave_id → 内存区(串口从站不需要停止标志,因为 Electron 驱动收发)
    serial_slaves: HashMap<String, Arc<Mutex<SlaveMemory>>>,
    // 三菱 MC 虚拟从站:slave_id → (内存, 停止标志)
    mc_slaves: HashMap<String, (Arc<Mutex<crate::mc_slave::McSlaveMemory>>, Arc<Mutex<bool>>)>,
    // 西门子 S7 虚拟从站:slave_id → (内存, 停止标志)
    s7_slaves: HashMap<String, (Arc<Mutex<crate::s7_slave::S7SlaveMemory>>, Arc<Mutex<bool>>)>,
    // 欧姆龙 FINS 虚拟从站
    fins_slaves: HashMap<String, (Arc<Mutex<crate::fins_slave::FinsMemory>>, Arc<Mutex<bool>>)>,
    // 西门子 Fetch/Write 虚拟从站
    fw_slaves: HashMap<String, (Arc<Mutex<crate::s7_fetchwrite::FwMemory>>, Arc<Mutex<bool>>)>,
    // 西门子 PPI 虚拟从站
    ppi_slaves: HashMap<String, (Arc<Mutex<crate::s7_slave::S7SlaveMemory>>, Arc<Mutex<bool>>)>,
}

impl Session {
    pub fn new() -> Self {
        Self {
            connections: HashMap::new(),
            ge_srtp_connections: HashMap::new(),
            fuji_sph_connections: HashMap::new(),
            fatek_connections: HashMap::new(),
            keyence_connections: HashMap::new(),
            ls_xgt_connections: HashMap::new(),
            ads_connections: HashMap::new(),
            enip_connections: HashMap::new(),
            mqtt_connections: HashMap::new(),
            iec104_connections: HashMap::new(),
            dnp3_connections: HashMap::new(),
            bacnet_connections: HashMap::new(),
            knx_connections: HashMap::new(),
            tid_gen: TransactionIdGenerator::new(),
            slaves: HashMap::new(),
            poll_streams: HashMap::new(),
            serial_slaves: HashMap::new(),
            mc_slaves: HashMap::new(),
            s7_slaves: HashMap::new(),
            fins_slaves: HashMap::new(),
            fw_slaves: HashMap::new(),
            ppi_slaves: HashMap::new(),
        }
    }

    /// 打开 TCP 连接。`framing` 决定帧包装方式(Standard/RtuOverTcp/AsciiOverTcp)。
    pub fn open_tcp(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        unit_id: u8,
        framing: TcpFraming,
    ) -> Result<(), CoreError> {
        let addr = format!("{host}:{port}");
        let stream = TcpStream::connect_timeout(
            &addr
                .parse()
                .map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.connections.insert(
            id.to_string(),
            Connection::Tcp {
                stream,
                unit_id,
                framing,
            },
        );
        Ok(())
    }

    /// Opens a GE Series 90 / PACSystems SRTP TCP session and performs the
    /// mandatory 56-byte zero handshake before exposing the connection.
    pub fn open_ge_srtp(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
    ) -> Result<crate::ge_srtp::HandshakeResponse, CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "GE_SRTP_CONNECTION_ID_INVALID",
                message: "GE SRTP connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "GE_SRTP_PORT_INVALID",
                message: "GE SRTP TCP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr: std::net::SocketAddr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let mut stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;

        let handshake = crate::ge_srtp::build_handshake();
        stream
            .write_all(&handshake)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_ge_srtp_frame(&mut stream, id, true)?;
        let validated = crate::ge_srtp::parse_handshake(&response)?;

        // Replacing an existing id is intentionally explicit and clean: the
        // old socket is dropped only after the new SRTP handshake succeeds.
        self.ge_srtp_connections.insert(
            id.to_string(),
            GeSrtpConnection {
                stream,
                transaction_id: 0,
            },
        );
        Ok(validated)
    }

    /// Executes one GE SRTP read over an already-initialized TCP session.
    /// Writes are intentionally not exposed by this live path until a
    /// separate safety gate and real-device evidence are approved.
    pub fn ge_srtp_read(
        &mut self,
        id: &str,
        address: &str,
        element_count: u16,
        bit_access: bool,
    ) -> Result<(Vec<u8>, crate::ge_srtp::Response), CoreError> {
        let expected =
            crate::ge_srtp::expected_read_data_bytes(address, element_count, bit_access)?;
        let connection = self
            .ge_srtp_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        connection.transaction_id = connection.transaction_id.wrapping_add(1);
        let transaction_id = connection.transaction_id;
        let request =
            crate::ge_srtp::build_read(transaction_id, address, element_count, bit_access)?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_ge_srtp_frame(&mut connection.stream, id, false)?;
        let parsed = crate::ge_srtp::parse_response(&response, transaction_id, expected)?;
        Ok((request, parsed))
    }

    /// Opens a Fuji SPH Loader Command TCP session.  SPH does not define a
    /// preamble handshake; the first request is sent only after TCP connect.
    pub fn open_fuji_sph(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        connection_id: u8,
    ) -> Result<(), CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "FUJI_SPH_CONNECTION_ID_INVALID",
                message: "Fuji SPH connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "FUJI_SPH_PORT_INVALID",
                message: "Fuji SPH TCP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.fuji_sph_connections.insert(
            id.to_string(),
            FujiSphConnection {
                stream,
                connection_id,
            },
        );
        Ok(())
    }

    /// Executes one Fuji SPH word read over an already-connected TCP session.
    /// Writes remain available only through the offline builder until a
    /// separate real-device safety gate is approved.
    pub fn fuji_sph_read(
        &mut self,
        id: &str,
        address: &str,
        words: u16,
    ) -> Result<(Vec<u8>, crate::fuji_sph::Response), CoreError> {
        if words == 0 {
            return Err(CoreError::Modbus {
                code: "FUJI_SPH_PARAM_INVALID",
                message: "Fuji SPH 读取字数必须大于 0".into(),
                details: Some(json!({ "words": words })),
            });
        }
        let connection = self
            .fuji_sph_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let request = crate::fuji_sph::build_read(connection.connection_id, address, words)?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_fuji_sph_frame(&mut connection.stream, id)?;
        let expected_data_bytes = usize::from(words) * 2;
        let parsed = crate::fuji_sph::parse_response(
            &response,
            connection.connection_id,
            crate::fuji_sph::READ,
            expected_data_bytes,
        )?;
        if response.len() < crate::fuji_sph::FRAME_BYTES || response[20..26] != request[20..26] {
            return Err(CoreError::Modbus {
                code: "FUJI_SPH_ECHO_MISMATCH",
                message: "Fuji SPH 响应地址/类型/字数回显与请求不一致".into(),
                details: Some(json!({ "connectionId": id })),
            });
        }
        Ok((request, parsed))
    }

    /// Opens a FATEK FBs native ASCII TCP session.  The protocol has no
    /// handshake; the station is carried in every request/response frame.
    pub fn open_fatek(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        station: u8,
    ) -> Result<(), CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "FATEK_CONNECTION_ID_INVALID",
                message: "FATEK connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "FATEK_PORT_INVALID",
                message: "FATEK TCP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        if station == 0 || station == 0xFF {
            return Err(CoreError::Modbus {
                code: "FATEK_STATION_INVALID",
                message: "FATEK 站号必须为 01H..FEH".into(),
                details: Some(json!({ "station": station })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.fatek_connections
            .insert(id.to_string(), FatekConnection { stream, station });
        Ok(())
    }

    /// Executes one FATEK native ASCII word read on an existing TCP session.
    pub fn fatek_read_words(
        &mut self,
        id: &str,
        address: &str,
        count: u16,
    ) -> Result<(Vec<u8>, crate::fatek::Response), CoreError> {
        let connection = self
            .fatek_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let request = crate::fatek::build_read_words(connection.station, address, count)?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_fatek_frame(&mut connection.stream, id)?;
        let parsed = crate::fatek::parse_response(&response, connection.station, "46")?;
        let expected = usize::from(count) * 4;
        if parsed.data.len() != expected {
            return Err(CoreError::Modbus {
                code: "FATEK_DATA_LENGTH_MISMATCH",
                message: format!(
                    "FATEK 字读取响应长度不符，期望 {expected}，实际 {}",
                    parsed.data.len()
                ),
                details: Some(json!({ "connectionId": id, "count": count })),
            });
        }
        Ok((request, parsed))
    }

    /// Executes one FATEK native ASCII discrete read on an existing TCP session.
    pub fn fatek_read_discrete(
        &mut self,
        id: &str,
        address: &str,
        count: u16,
    ) -> Result<(Vec<u8>, crate::fatek::Response), CoreError> {
        let connection = self
            .fatek_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let request = crate::fatek::build_read_discrete(connection.station, address, count)?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_fatek_frame(&mut connection.stream, id)?;
        let parsed = crate::fatek::parse_response(&response, connection.station, "44")?;
        if parsed.data.len() != usize::from(count)
            || parsed
                .data
                .iter()
                .any(|value| *value != b'0' && *value != b'1')
        {
            return Err(CoreError::Modbus {
                code: "FATEK_DATA_INVALID",
                message: "FATEK 位读取响应必须是 count 个 ASCII 0/1".into(),
                details: Some(json!({ "connectionId": id, "count": count })),
            });
        }
        Ok((request, parsed))
    }

    /// Opens a Keyence KV Host Link ASCII TCP session and validates the CC
    /// response to the CR/CR NN handshake before storing the stream.
    pub fn open_keyence(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        station: Option<u8>,
    ) -> Result<(), CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "KEYENCE_CONNECTION_ID_INVALID",
                message: "Keyence connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "KEYENCE_PORT_INVALID",
                message: "Keyence Host Link TCP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        if station.is_some_and(|value| value > 31) {
            return Err(CoreError::Modbus {
                code: "KEYENCE_STATION_INVALID",
                message: "Keyence Host Link 站号必须是 0..31".into(),
                details: Some(json!({ "station": station })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let mut stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;

        let handshake = crate::keyence::build_connect(station);
        stream
            .write_all(&handshake)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_keyence_line(&mut stream, id)?;
        let response = std::str::from_utf8(&response).map_err(|_| CoreError::Modbus {
            code: "KEYENCE_FRAME_INVALID",
            message: "Keyence Host Link 响应不是 UTF-8/ASCII".into(),
            details: Some(json!({ "connectionId": id })),
        })?;
        crate::keyence::parse_connect_response(response)?;

        self.keyence_connections
            .insert(id.to_string(), KeyenceConnection { stream, station });
        Ok(())
    }

    /// Executes one Keyence KV Host Link word read over an initialized TCP
    /// session.  Writes and ST/RS controls remain offline-only.
    pub fn keyence_read_words(
        &mut self,
        id: &str,
        address: &str,
        count: u16,
    ) -> Result<(Vec<u8>, Vec<u16>, Option<u8>), CoreError> {
        let connection = self
            .keyence_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let request = crate::keyence::build_read_words(address, count)?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_keyence_line(&mut connection.stream, id)?;
        let response = std::str::from_utf8(&response).map_err(|_| CoreError::Modbus {
            code: "KEYENCE_FRAME_INVALID",
            message: "Keyence Host Link 响应不是 UTF-8/ASCII".into(),
            details: Some(json!({ "connectionId": id })),
        })?;
        let values = crate::keyence::parse_word_response(response, usize::from(count))?;
        Ok((request, values, connection.station))
    }

    /// Executes one Keyence KV Host Link bit read over an initialized TCP
    /// session.  The session cannot issue ST/RS through this live boundary.
    pub fn keyence_read_bits(
        &mut self,
        id: &str,
        address: &str,
        count: u16,
    ) -> Result<(Vec<u8>, Vec<bool>, Option<u8>), CoreError> {
        let connection = self
            .keyence_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let request = crate::keyence::build_read_bits(address, count)?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_keyence_line(&mut connection.stream, id)?;
        let response = std::str::from_utf8(&response).map_err(|_| CoreError::Modbus {
            code: "KEYENCE_FRAME_INVALID",
            message: "Keyence Host Link 响应不是 UTF-8/ASCII".into(),
            details: Some(json!({ "connectionId": id })),
        })?;
        let values = crate::keyence::parse_bit_response(response, usize::from(count))?;
        Ok((request, values, connection.station))
    }

    /// Opens an LS Electric XGT FEnet TCP session.  There is no separate
    /// handshake; the first XGT read carries the 20-byte header and InvokeId.
    pub fn open_ls_xgt(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        cpu: u8,
        base_no: u8,
        slot_no: u8,
        company_id: &str,
    ) -> Result<(), CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "LS_XGT_CONNECTION_ID_INVALID",
                message: "LS XGT connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "LS_XGT_PORT_INVALID",
                message: "LS XGT TCP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        if !matches!(
            cpu,
            crate::ls_xgt::CPU_XGK
                | crate::ls_xgt::CPU_XGI
                | crate::ls_xgt::CPU_XGR
                | crate::ls_xgt::CPU_XGB_MK
                | crate::ls_xgt::CPU_XGB_IEC
        ) {
            return Err(CoreError::Modbus {
                code: "LS_XGT_CPU_INVALID",
                message: format!("不支持的 LS XGT CPU: 0x{cpu:02X}"),
                details: Some(json!({ "cpu": cpu })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        // Reuse the codec's strict validation before exposing the socket.
        let _ = crate::ls_xgt::build_individual_read(
            "%DW0",
            crate::ls_xgt::INDIVIDUAL_WORD,
            1,
            cpu,
            base_no,
            slot_no,
            company_id,
        )?;
        self.ls_xgt_connections.insert(
            id.to_string(),
            LsXgtConnection {
                stream,
                cpu,
                base_no,
                slot_no,
                company_id: company_id.to_string(),
                invoke_id: 0,
            },
        );
        Ok(())
    }

    /// Executes one LS XGT individual variable read on an initialized TCP
    /// session.  InvokeId is allocated monotonically per connection.
    pub fn ls_xgt_read(
        &mut self,
        id: &str,
        variable_name: &str,
        data_type: u8,
    ) -> Result<(Vec<u8>, crate::ls_xgt::XgtResponse), CoreError> {
        let connection = self
            .ls_xgt_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        connection.invoke_id = connection.invoke_id.wrapping_add(1);
        let invoke_id = connection.invoke_id;
        let request = crate::ls_xgt::build_individual_read(
            variable_name,
            data_type,
            invoke_id,
            connection.cpu,
            connection.base_no,
            connection.slot_no,
            &connection.company_id,
        )?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_ls_xgt_frame(&mut connection.stream, id)?;
        let parsed = crate::ls_xgt::parse_response(&response, Some(invoke_id))?;
        if parsed.command != crate::ls_xgt::READ_RESPONSE {
            return Err(CoreError::Modbus {
                code: "LS_XGT_COMMAND_MISMATCH",
                message: "LS XGT 读会话收到的响应不是 Read response".into(),
                details: Some(json!({ "connectionId": id, "command": parsed.command })),
            });
        }
        Ok((request, parsed))
    }

    /// Executes one LS XGT continuous byte read on an initialized TCP session.
    pub fn ls_xgt_read_continuous(
        &mut self,
        id: &str,
        variable_name: &str,
        byte_count: u16,
    ) -> Result<(Vec<u8>, crate::ls_xgt::XgtResponse), CoreError> {
        let connection = self
            .ls_xgt_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        connection.invoke_id = connection.invoke_id.wrapping_add(1);
        let invoke_id = connection.invoke_id;
        let request = crate::ls_xgt::build_continuous_read(
            variable_name,
            byte_count,
            invoke_id,
            connection.cpu,
            connection.base_no,
            connection.slot_no,
            &connection.company_id,
        )?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_ls_xgt_frame(&mut connection.stream, id)?;
        let parsed = crate::ls_xgt::parse_response(&response, Some(invoke_id))?;
        if parsed.command != crate::ls_xgt::READ_RESPONSE {
            return Err(CoreError::Modbus {
                code: "LS_XGT_COMMAND_MISMATCH",
                message: "LS XGT 连续读取收到的响应不是 Read response".into(),
                details: Some(json!({ "connectionId": id, "command": parsed.command })),
            });
        }
        Ok((request, parsed))
    }

    /// Opens an ADS/TCP connection.  ADS routers and AMS routes are not
    /// created implicitly; the caller supplies the target/source AMS
    /// endpoints and the first transaction proves the peer's frame boundary.
    pub fn open_ads(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        target_net_id: [u8; crate::ads::AMS_NET_ID_BYTES],
        target_port: u16,
        source_net_id: [u8; crate::ads::AMS_NET_ID_BYTES],
        source_port: u16,
    ) -> Result<(), CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "ADS_CONNECTION_ID_INVALID",
                message: "ADS connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 || target_port == 0 || source_port == 0 {
            return Err(CoreError::Modbus {
                code: "ADS_PORT_INVALID",
                message: "ADS/TCP、目标 AMS Port 和源 AMS Port 必须大于 0".into(),
                details: Some(
                    json!({ "port": port, "targetPort": target_port, "sourcePort": source_port }),
                ),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.ads_connections.insert(
            id.to_string(),
            AdsConnection {
                stream,
                target_net_id,
                target_port,
                source_net_id,
                source_port,
                invoke_id: 0,
            },
        );
        Ok(())
    }

    /// Executes one ADS Read transaction over an initialized TCP connection.
    /// The response must swap AMS endpoints, echo InvokeId and satisfy the
    /// codec's Result/DataLength checks.
    pub fn ads_read(
        &mut self,
        id: &str,
        index_group: u32,
        index_offset: u32,
        read_length: u32,
    ) -> Result<(Vec<u8>, Vec<u8>, crate::ads::AdsResponse), CoreError> {
        let connection = self
            .ads_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        connection.invoke_id = connection.invoke_id.wrapping_add(1);
        let invoke_id = connection.invoke_id;
        let request = crate::ads::build_read(
            &connection.target_net_id,
            connection.target_port,
            &connection.source_net_id,
            connection.source_port,
            invoke_id,
            index_group,
            index_offset,
            read_length,
        )?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_ads_frame(&mut connection.stream, id)?;
        let expected_target = crate::ads::EndpointExpectation {
            net_id: Some(connection.source_net_id),
            port: Some(connection.source_port),
        };
        let expected_source = crate::ads::EndpointExpectation {
            net_id: Some(connection.target_net_id),
            port: Some(connection.target_port),
        };
        let parsed = crate::ads::parse_response_with_endpoints(
            &response,
            Some(invoke_id),
            Some(crate::ads::ADS_READ),
            Some(&expected_target),
            Some(&expected_source),
        )?;
        Ok((request, response, parsed))
    }

    /// Executes ADS ReadDeviceInfo as a read-only endpoint probe.
    pub fn ads_read_device_info(
        &mut self,
        id: &str,
    ) -> Result<(Vec<u8>, Vec<u8>, crate::ads::AdsResponse), CoreError> {
        self.ads_read_control(id, crate::ads::ADS_READ_DEVICE_INFO)
    }

    /// Executes ADS ReadState without exposing WriteControl or remote control.
    pub fn ads_read_state(
        &mut self,
        id: &str,
    ) -> Result<(Vec<u8>, Vec<u8>, crate::ads::AdsResponse), CoreError> {
        self.ads_read_control(id, crate::ads::ADS_READ_STATE)
    }

    fn ads_read_control(
        &mut self,
        id: &str,
        command: u16,
    ) -> Result<(Vec<u8>, Vec<u8>, crate::ads::AdsResponse), CoreError> {
        let connection = self
            .ads_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        connection.invoke_id = connection.invoke_id.wrapping_add(1);
        let invoke_id = connection.invoke_id;
        let request = match command {
            crate::ads::ADS_READ_DEVICE_INFO => crate::ads::build_read_device_info(
                &connection.target_net_id,
                connection.target_port,
                &connection.source_net_id,
                connection.source_port,
                invoke_id,
            )?,
            crate::ads::ADS_READ_STATE => crate::ads::build_read_state(
                &connection.target_net_id,
                connection.target_port,
                &connection.source_net_id,
                connection.source_port,
                invoke_id,
            )?,
            _ => {
                return Err(CoreError::Modbus {
                    code: "ADS_COMMAND_INVALID",
                    message: format!("不支持的 ADS 只读探测命令: 0x{command:04X}"),
                    details: None,
                });
            }
        };
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_ads_frame(&mut connection.stream, id)?;
        let expected_target = crate::ads::EndpointExpectation {
            net_id: Some(connection.source_net_id),
            port: Some(connection.source_port),
        };
        let expected_source = crate::ads::EndpointExpectation {
            net_id: Some(connection.target_net_id),
            port: Some(connection.target_port),
        };
        let parsed = crate::ads::parse_response_with_endpoints(
            &response,
            Some(invoke_id),
            Some(command),
            Some(&expected_target),
            Some(&expected_source),
        )?;
        Ok((request, response, parsed))
    }

    /// Opens an EtherNet/IP explicit-message session using RegisterSession.
    pub fn open_enip(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
    ) -> Result<crate::enip::RegisterSessionResponse, CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "ENIP_CONNECTION_ID_INVALID",
                message: "EtherNet/IP connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "ENIP_PORT_INVALID",
                message: "EtherNet/IP TCP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let mut stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        let sender_context = 1u64;
        let request = crate::enip::build_register_session(0, sender_context);
        stream
            .write_all(&request)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_enip_frame(&mut stream, id)?;
        let registered =
            crate::enip::parse_register_session_response(&response, Some(sender_context))?;
        self.enip_connections.insert(
            id.to_string(),
            EnipConnection {
                stream,
                session_handle: registered.session_handle,
                sender_context: registered.sender_context,
            },
        );
        Ok(registered)
    }

    /// Executes one unconnected CIP Read Tag request over a registered
    /// EtherNet/IP session.  ForwardOpen, writes and connected I/O remain out
    /// of this live boundary.
    pub fn enip_read_tag(
        &mut self,
        id: &str,
        tag: &str,
        elements: u16,
    ) -> Result<(Vec<u8>, crate::enip::CipResponse, u64), CoreError> {
        let connection = self
            .enip_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        connection.sender_context = connection.sender_context.wrapping_add(1);
        let sender_context = connection.sender_context;
        let request =
            crate::enip::build_read_tag(connection.session_handle, sender_context, tag, elements)?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_enip_frame(&mut connection.stream, id)?;
        let parsed = crate::enip::parse_cip_response(&response)?;
        if parsed.service != crate::enip::CIP_READ_TAG_REPLY {
            return Err(CoreError::Modbus {
                code: "ENIP_CIP_SERVICE_MISMATCH",
                message: format!(
                    "CIP Read Tag 响应 service 必须为 0x{:02X}，收到 0x{:02X}",
                    crate::enip::CIP_READ_TAG_REPLY,
                    parsed.service
                ),
                details: Some(json!({ "connectionId": id, "service": parsed.service })),
            });
        }
        if parsed.general_status != 0 {
            return Err(CoreError::Modbus {
                code: "ENIP_CIP_STATUS",
                message: format!(
                    "CIP Read Tag 返回错误状态 0x{:02X}: {}",
                    parsed.general_status,
                    crate::enip::cip_status_message(parsed.general_status)
                ),
                details: Some(json!({ "connectionId": id, "status": parsed.general_status })),
            });
        }
        Ok((request, parsed, sender_context))
    }

    /// Opens an MQTT 3.1.1 broker session and completes CONNECT/CONNACK.
    /// Username/password, TLS and will-message policy remain outside this
    /// first read-only boundary.
    pub fn open_mqtt(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        client_id: &str,
        keep_alive: u16,
        clean_session: bool,
    ) -> Result<crate::mqtt::ConnAck, CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "MQTT_CONNECTION_ID_INVALID",
                message: "MQTT connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "MQTT_PORT_INVALID",
                message: "MQTT TCP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        let request = crate::mqtt::build_connect(client_id, keep_alive, clean_session)?;
        let addr = format!("{host}:{port}");
        let socket_addr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let mut stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .write_all(&request)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_mqtt_frame(&mut stream, id)?;
        let connack = crate::mqtt::parse_connack(&response)?;
        if connack.return_code != 0 {
            return Err(CoreError::Modbus {
                code: "MQTT_CONNACK_REJECTED",
                message: format!(
                    "MQTT CONNACK 被 broker 拒绝: {} (0x{:02X})",
                    crate::mqtt::return_code_message(connack.return_code),
                    connack.return_code
                ),
                details: Some(json!({
                    "connectionId": id,
                    "returnCode": connack.return_code,
                    "returnCodeMessage": crate::mqtt::return_code_message(connack.return_code)
                })),
            });
        }
        self.mqtt_connections.insert(
            id.to_string(),
            MqttConnection {
                stream,
                packet_id: 0,
            },
        );
        Ok(connack)
    }

    /// Subscribes to one topic filter at QoS 0/1/2 and returns the raw frames.
    /// The UI first-round path requests QoS 0 and never publishes commands.
    pub fn mqtt_subscribe(
        &mut self,
        id: &str,
        topic_filter: &str,
        qos: u8,
    ) -> Result<(Vec<u8>, Vec<u8>, crate::mqtt::SubAck), CoreError> {
        let connection = self
            .mqtt_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        connection.packet_id = connection.packet_id.wrapping_add(1);
        if connection.packet_id == 0 {
            connection.packet_id = 1;
        }
        let packet_id = connection.packet_id;
        let request = crate::mqtt::build_subscribe(packet_id, topic_filter, qos)?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_mqtt_frame(&mut connection.stream, id)?;
        let suback = crate::mqtt::parse_suback(&response)?;
        if suback.packet_id != packet_id {
            return Err(CoreError::Modbus {
                code: "MQTT_SUBACK_PACKET_ID_MISMATCH",
                message: "MQTT SUBACK packetId 与请求不匹配".into(),
                details: Some(
                    json!({ "connectionId": id, "expected": packet_id, "actual": suback.packet_id }),
                ),
            });
        }
        if suback.return_code != qos.min(2) {
            return Err(CoreError::Modbus {
                code: "MQTT_SUBACK_REJECTED",
                message: format!(
                    "MQTT SUBSCRIBE 被 broker 拒绝: {} (0x{:02X})",
                    crate::mqtt::return_code_message(suback.return_code),
                    suback.return_code
                ),
                details: Some(
                    json!({ "connectionId": id, "topicFilter": topic_filter, "returnCode": suback.return_code }),
                ),
            });
        }
        Ok((request, response, suback))
    }

    /// Reads one broker PUBLISH packet after a successful subscription.
    pub fn mqtt_read_publish(
        &mut self,
        id: &str,
    ) -> Result<(Vec<u8>, crate::mqtt::Publish), CoreError> {
        let connection = self
            .mqtt_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let frame = read_mqtt_frame(&mut connection.stream, id)?;
        let parsed_frame = crate::mqtt::parse_frame(&frame)?;
        if parsed_frame.packet_type != crate::mqtt::PACKET_PUBLISH {
            return Err(CoreError::Modbus {
                code: "MQTT_PACKET_UNEXPECTED",
                message: format!(
                    "MQTT 订阅读取期望 PUBLISH，收到 packet type {}",
                    parsed_frame.packet_type
                ),
                details: Some(
                    json!({ "connectionId": id, "packetType": parsed_frame.packet_type }),
                ),
            });
        }
        let publish = crate::mqtt::parse_publish(&frame)?;
        if publish.qos != 0 {
            return Err(CoreError::Modbus {
                code: "MQTT_QOS_UNSUPPORTED",
                message: "首轮 MQTT 只读会话只接受 QoS 0 PUBLISH，不自动发送 PUBACK/PUBREC".into(),
                details: Some(json!({ "connectionId": id, "qos": publish.qos })),
            });
        }
        Ok((frame, publish))
    }

    /// Sends MQTT PINGREQ and validates PINGRESP for keep-alive diagnostics.
    pub fn mqtt_ping(&mut self, id: &str) -> Result<(Vec<u8>, Vec<u8>), CoreError> {
        let connection = self
            .mqtt_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let request = crate::mqtt::build_pingreq();
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_mqtt_frame(&mut connection.stream, id)?;
        crate::mqtt::parse_pingresp(&response)?;
        Ok((request, response))
    }

    /// Opens an IEC 60870-5-104 client/master session and completes
    /// STARTDT_ACT/STARTDT_CON before exposing the connection.
    pub fn open_iec104(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        common_address: u16,
        originator_address: u8,
    ) -> Result<(Vec<u8>, Vec<u8>), CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "IEC104_CONNECTION_ID_INVALID",
                message: "IEC104 connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "IEC104_PORT_INVALID",
                message: "IEC104 TCP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let mut stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;

        let request =
            crate::iec104::build_u_frame(crate::iec104::UFunction::StartDataTransferActivation);
        stream
            .write_all(&request)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_iec104_frame(&mut stream, id)?;
        match crate::iec104::parse_apdu(&response)? {
            crate::iec104::Apdu::U {
                function: crate::iec104::UFunction::StartDataTransferConfirmation,
            } => {}
            apdu => {
                return Err(CoreError::Modbus {
                    code: "IEC104_STARTDT_REJECTED",
                    message: "IEC104 建链期望 STARTDT_CON，收到其他 APDU".into(),
                    details: Some(json!({
                        "connectionId": id,
                        "format": apdu.format(),
                        "responseHex": crate::iec104::frame_hex(&response)
                    })),
                });
            }
        }
        self.iec104_connections.insert(
            id.to_string(),
            Iec104Connection {
                stream,
                send_sequence: 0,
                receive_sequence: 0,
                common_address,
                originator_address,
            },
        );
        Ok((request, response))
    }

    /// Sends TESTFR_ACT and requires the matching TESTFR_CON link response.
    pub fn iec104_test_frame(&mut self, id: &str) -> Result<(Vec<u8>, Vec<u8>), CoreError> {
        let connection = self
            .iec104_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let request = crate::iec104::build_u_frame(crate::iec104::UFunction::TestFrameActivation);
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = read_iec104_frame(&mut connection.stream, id)?;
        match crate::iec104::parse_apdu(&response)? {
            crate::iec104::Apdu::U {
                function: crate::iec104::UFunction::TestFrameConfirmation,
            } => Ok((request, response)),
            apdu => Err(CoreError::Modbus {
                code: "IEC104_TESTFR_REJECTED",
                message: "IEC104 链路测试期望 TESTFR_CON，收到其他 APDU".into(),
                details: Some(json!({
                    "connectionId": id,
                    "format": apdu.format(),
                    "responseHex": crate::iec104::frame_hex(&response)
                })),
            }),
        }
    }

    /// Performs station (group=0) or group (1..16) interrogation and waits
    /// for ACT_CON, monitoring ASDUs and ACT_TERM. Every inbound I frame is
    /// acknowledged immediately with an S frame.
    pub fn iec104_general_interrogation(
        &mut self,
        id: &str,
        group: u8,
    ) -> Result<crate::iec104::InterrogationResult, CoreError> {
        let expected_cause = crate::iec104::expected_interrogation_cause(group)?;
        let connection = self
            .iec104_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let asdu = crate::iec104::build_general_interrogation_asdu(
            connection.common_address,
            group,
            connection.originator_address,
        )?;
        let request = crate::iec104::build_i_frame(
            connection.send_sequence,
            connection.receive_sequence,
            &asdu,
        )?;
        connection
            .stream
            .write_all(&request)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        connection.send_sequence = crate::iec104::next_sequence(connection.send_sequence);

        let qualifier = 20 + group;
        let mut received_frames = Vec::new();
        let mut acknowledgement_frames = Vec::new();
        let mut points = Vec::new();
        let mut activation_confirmed = false;
        let mut activation_terminated = false;

        for _ in 0..crate::iec104::MAX_INTERROGATION_FRAMES {
            let frame = read_iec104_frame(&mut connection.stream, id)?;
            received_frames.push(frame.clone());
            match crate::iec104::parse_apdu(&frame)? {
                crate::iec104::Apdu::I {
                    send_sequence,
                    receive_sequence,
                    asdu,
                } => {
                    if send_sequence != connection.receive_sequence {
                        return Err(CoreError::Modbus {
                            code: "IEC104_RECEIVE_SEQUENCE_MISMATCH",
                            message: "IEC104 对端 I 帧 N(S) 与本地期望不一致".into(),
                            details: Some(json!({
                                "connectionId": id,
                                "expected": connection.receive_sequence,
                                "actual": send_sequence
                            })),
                        });
                    }
                    if receive_sequence != connection.send_sequence {
                        return Err(CoreError::Modbus {
                            code: "IEC104_SEND_SEQUENCE_UNACKNOWLEDGED",
                            message: "IEC104 对端 N(R) 未确认当前发送序号".into(),
                            details: Some(json!({
                                "connectionId": id,
                                "expected": connection.send_sequence,
                                "actual": receive_sequence
                            })),
                        });
                    }
                    connection.receive_sequence =
                        crate::iec104::next_sequence(connection.receive_sequence);
                    let acknowledgement =
                        crate::iec104::build_s_frame(connection.receive_sequence)?;
                    connection
                        .stream
                        .write_all(&acknowledgement)
                        .and_then(|_| connection.stream.flush())
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    acknowledgement_frames.push(acknowledgement);

                    let parsed = crate::iec104::parse_asdu(&asdu)?;
                    if parsed.common_address != connection.common_address {
                        return Err(CoreError::Modbus {
                            code: "IEC104_COMMON_ADDRESS_MISMATCH",
                            message: "IEC104 响应公共地址与连接配置不一致".into(),
                            details: Some(json!({
                                "connectionId": id,
                                "expected": connection.common_address,
                                "actual": parsed.common_address
                            })),
                        });
                    }
                    if parsed.is_negative {
                        return Err(CoreError::Modbus {
                            code: "IEC104_NEGATIVE_CONFIRMATION",
                            message: "IEC104 站端返回否定确认".into(),
                            details: Some(json!({
                                "connectionId": id,
                                "typeId": parsed.type_id,
                                "cause": parsed.cause
                            })),
                        });
                    }

                    if parsed.type_id == crate::iec104::TYPE_C_IC_NA_1 {
                        let valid_object = parsed.objects.len() == 1
                            && parsed.objects[0].address == 0
                            && parsed.objects[0].data == [qualifier];
                        if !valid_object {
                            return Err(CoreError::Modbus {
                                code: "IEC104_INTERROGATION_CONFIRMATION_MISMATCH",
                                message: "IEC104 总召确认的 IOA/QOI 与请求不一致".into(),
                                details: Some(json!({
                                    "connectionId": id,
                                    "qualifier": qualifier,
                                    "asdu": parsed
                                })),
                            });
                        }
                        match parsed.cause {
                            crate::iec104::COT_ACTIVATION_CONFIRMATION => {
                                if activation_confirmed {
                                    return Err(CoreError::Modbus {
                                        code: "IEC104_INTERROGATION_DUPLICATE_CONFIRMATION",
                                        message: "IEC104 总召收到重复 ACT_CON".into(),
                                        details: Some(json!({ "connectionId": id })),
                                    });
                                }
                                activation_confirmed = true;
                            }
                            crate::iec104::COT_ACTIVATION_TERMINATION => {
                                if !activation_confirmed {
                                    return Err(CoreError::Modbus {
                                        code: "IEC104_INTERROGATION_ORDER_INVALID",
                                        message: "IEC104 总召在 ACT_CON 前收到 ACT_TERM".into(),
                                        details: Some(json!({ "connectionId": id })),
                                    });
                                }
                                activation_terminated = true;
                                break;
                            }
                            cause => {
                                return Err(CoreError::Modbus {
                                    code: "IEC104_INTERROGATION_CAUSE_INVALID",
                                    message: "IEC104 总召控制 ASDU 的传送原因无效".into(),
                                    details: Some(json!({
                                        "connectionId": id,
                                        "cause": cause,
                                        "causeName": crate::iec104::cause_name(cause)
                                    })),
                                });
                            }
                        }
                    } else if crate::iec104::is_monitoring_type(parsed.type_id) {
                        if !activation_confirmed {
                            return Err(CoreError::Modbus {
                                code: "IEC104_INTERROGATION_ORDER_INVALID",
                                message: "IEC104 总召在 ACT_CON 前收到监视数据".into(),
                                details: Some(
                                    json!({ "connectionId": id, "typeId": parsed.type_id }),
                                ),
                            });
                        }
                        if !matches!(
                            parsed.cause,
                            crate::iec104::COT_PERIODIC
                                | crate::iec104::COT_BACKGROUND
                                | crate::iec104::COT_SPONTANEOUS
                        ) && parsed.cause != expected_cause
                        {
                            return Err(CoreError::Modbus {
                                code: "IEC104_INTERROGATION_DATA_CAUSE_INVALID",
                                message: "IEC104 总召监视数据的传送原因不匹配".into(),
                                details: Some(json!({
                                    "connectionId": id,
                                    "expected": expected_cause,
                                    "actual": parsed.cause
                                })),
                            });
                        }
                        points.extend(crate::iec104::decode_telemetry(&parsed)?);
                    } else {
                        return Err(CoreError::Modbus {
                            code: "IEC104_ASDU_UNSUPPORTED",
                            message: "IEC104 总召收到首轮边界外的 ASDU".into(),
                            details: Some(json!({
                                "connectionId": id,
                                "typeId": parsed.type_id
                            })),
                        });
                    }
                }
                crate::iec104::Apdu::S { receive_sequence } => {
                    if receive_sequence != connection.send_sequence {
                        return Err(CoreError::Modbus {
                            code: "IEC104_SEND_SEQUENCE_UNACKNOWLEDGED",
                            message: "IEC104 S 帧 N(R) 与当前发送序号不一致".into(),
                            details: Some(json!({
                                "connectionId": id,
                                "expected": connection.send_sequence,
                                "actual": receive_sequence
                            })),
                        });
                    }
                }
                crate::iec104::Apdu::U {
                    function: crate::iec104::UFunction::TestFrameActivation,
                } => {
                    let confirmation = crate::iec104::build_u_frame(
                        crate::iec104::UFunction::TestFrameConfirmation,
                    );
                    connection
                        .stream
                        .write_all(&confirmation)
                        .and_then(|_| connection.stream.flush())
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    acknowledgement_frames.push(confirmation);
                }
                crate::iec104::Apdu::U { function } => {
                    return Err(CoreError::Modbus {
                        code: "IEC104_U_FRAME_UNEXPECTED",
                        message: format!("IEC104 总召期间收到意外 U 帧 {}", function.name()),
                        details: Some(json!({ "connectionId": id, "function": function })),
                    });
                }
            }
        }

        if !activation_terminated {
            return Err(CoreError::Modbus {
                code: "IEC104_INTERROGATION_NOT_TERMINATED",
                message: format!(
                    "IEC104 总召在 {} 帧软件上限内未收到 ACT_TERM",
                    crate::iec104::MAX_INTERROGATION_FRAMES
                ),
                details: Some(json!({
                    "connectionId": id,
                    "receivedFrames": received_frames.len()
                })),
            });
        }

        Ok(crate::iec104::InterrogationResult {
            group,
            qualifier,
            request_frame: request,
            received_frames,
            acknowledgement_frames,
            points,
            activation_confirmed,
            activation_terminated,
            final_send_sequence: connection.send_sequence,
            final_receive_sequence: connection.receive_sequence,
        })
    }

    /// Opens a stateful DNP3/TCP master association. DNP3/TCP does not have a
    /// separate application handshake, so the connection becomes usable after
    /// TCP connect and strict address/configuration validation.
    pub fn open_dnp3(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        master_address: u16,
        outstation_address: u16,
    ) -> Result<(), CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "DNP3_CONNECTION_ID_INVALID",
                message: "DNP3 connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "DNP3_PORT_INVALID",
                message: "DNP3 TCP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        if master_address >= 0xFFF0 || outstation_address >= 0xFFF0 {
            return Err(CoreError::Modbus {
                code: "DNP3_LINK_ADDRESS_RESERVED",
                message: "DNP3 首轮点对点会话不允许使用 FFF0H..FFFFH 保留/广播地址".into(),
                details: Some(json!({
                    "masterAddress": master_address,
                    "outstationAddress": outstation_address
                })),
            });
        }
        if master_address == outstation_address {
            return Err(CoreError::Modbus {
                code: "DNP3_LINK_ADDRESS_CONFLICT",
                message: "DNP3 Master 与 Outstation 链路地址必须不同".into(),
                details: Some(json!({ "address": master_address })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let stream = TcpStream::connect_timeout(&socket_addr, TCP_READ_TIMEOUT)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_nodelay(true)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.dnp3_connections.insert(
            id.to_string(),
            Dnp3Connection {
                stream,
                master_address,
                outstation_address,
                application_sequence: 0,
                transport_sequence: 0,
            },
        );
        Ok(())
    }

    /// Executes an integrity poll (Class 0 plus Class 1/2/3 events).
    pub fn dnp3_integrity_poll(&mut self, id: &str) -> Result<crate::dnp3::ScanResult, CoreError> {
        self.dnp3_class_scan(id, true, true, true, true, "integrityPoll")
    }

    /// Executes one selected Class 0/1/2/3 READ transaction. Application
    /// confirms are emitted automatically when the peer sets CON.
    pub fn dnp3_class_scan(
        &mut self,
        id: &str,
        class0: bool,
        class1: bool,
        class2: bool,
        class3: bool,
        operation: &str,
    ) -> Result<crate::dnp3::ScanResult, CoreError> {
        let classes = [class0, class1, class2, class3]
            .into_iter()
            .enumerate()
            .filter_map(|(index, enabled)| enabled.then_some(index as u8))
            .collect::<Vec<_>>();
        let exchange = {
            let connection = self
                .dnp3_connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let sequence = connection.application_sequence;
            let request = crate::dnp3::build_class_scan(sequence, class0, class1, class2, class3)?;
            connection.application_sequence = crate::dnp3::next_application_sequence(sequence);
            dnp3_exchange(connection, id, request, sequence, operation, classes)
        };
        match exchange {
            Ok(result) if result.iin.request_error => Err(CoreError::Modbus {
                code: "DNP3_IIN_REQUEST_ERROR",
                message: format!(
                    "DNP3 Outstation 用 IIN 拒绝 Class 扫描: {}",
                    result.iin.labels.join(", ")
                ),
                details: Some(json!({
                    "connectionId": id,
                    "iin": result.iin,
                    "responseFrames": result.response_frames
                })),
            }),
            Ok(result) => Ok(result),
            Err(error) => {
                self.dnp3_connections.remove(id);
                Err(error)
            }
        }
    }

    /// Executes one bounded object-variation READ, either an all-objects scan
    /// or an inclusive 16-bit range. Function codes other than READ are never
    /// constructed by this live path.
    pub fn dnp3_read(
        &mut self,
        id: &str,
        group: u8,
        variation: u8,
        start: Option<u16>,
        stop: Option<u16>,
    ) -> Result<crate::dnp3::ScanResult, CoreError> {
        if start.is_some() != stop.is_some() {
            return Err(CoreError::Modbus {
                code: "DNP3_RANGE_INCOMPLETE",
                message: "DNP3 start/stop 必须同时提供，或同时省略以读取 all objects".into(),
                details: Some(json!({ "start": start, "stop": stop })),
            });
        }
        let exchange = {
            let connection = self
                .dnp3_connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let sequence = connection.application_sequence;
            let request = match (start, stop) {
                (Some(start), Some(stop)) => {
                    crate::dnp3::build_read_request(sequence, group, variation, start, stop)?
                }
                (None, None) => crate::dnp3::build_read_all_request(sequence, group, variation)?,
                _ => unreachable!(),
            };
            connection.application_sequence = crate::dnp3::next_application_sequence(sequence);
            dnp3_exchange(
                connection,
                id,
                request,
                sequence,
                &format!("readG{group}V{variation}"),
                Vec::new(),
            )
        };
        match exchange {
            Ok(result) if result.iin.request_error => Err(CoreError::Modbus {
                code: "DNP3_IIN_REQUEST_ERROR",
                message: format!(
                    "DNP3 Outstation 用 IIN 拒绝读取: {}",
                    result.iin.labels.join(", ")
                ),
                details: Some(json!({
                    "connectionId": id,
                    "group": group,
                    "variation": variation,
                    "iin": result.iin,
                    "responseFrames": result.response_frames
                })),
            }),
            Ok(result) => Ok(result),
            Err(error) => {
                self.dnp3_connections.remove(id);
                Err(error)
            }
        }
    }

    /// Opens a BACnet/IPv4 UDP read-only session to one explicit peer. No
    /// BBMD registration, foreign-device registration, or broadcast socket is
    /// performed by this boundary.
    pub fn open_bacnet_ip(&mut self, id: &str, host: &str, port: u16) -> Result<(), CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "BACNET_CONNECTION_ID_INVALID",
                message: "BACnet connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "BACNET_PORT_INVALID",
                message: "BACnet/IPv4 UDP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr: std::net::SocketAddr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let socket =
            UdpSocket::bind("0.0.0.0:0").map_err(|e| connection_failed(&addr, &e.to_string()))?;
        socket
            .connect(socket_addr)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        socket
            .set_read_timeout(Some(Duration::from_secs(2)))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.bacnet_connections.insert(
            id.to_string(),
            BacnetConnection {
                socket,
                invoke_id: 1,
            },
        );
        Ok(())
    }

    /// Sends one directed Who-Is and collects I-Am datagrams until the bounded
    /// timeout expires. This is a discovery read-only operation.
    pub fn bacnet_whois(
        &mut self,
        id: &str,
        low_limit: Option<u32>,
        high_limit: Option<u32>,
        timeout_ms: u64,
    ) -> Result<crate::bacnet::WhoisResult, CoreError> {
        let request = crate::bacnet::build_whois_request(low_limit, high_limit, false)?;
        let exchange: Result<crate::bacnet::WhoisResult, CoreError> = (|| {
            let connection = self
                .bacnet_connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            connection
                .socket
                .set_read_timeout(Some(Duration::from_millis(timeout_ms.max(100))))
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            connection
                .socket
                .send(&request)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            let mut response_frames = Vec::new();
            let mut responses = Vec::new();
            let mut buffer = [0u8; 2048];
            loop {
                match connection.socket.recv(&mut buffer) {
                    Ok(size) => {
                        let frame = buffer[..size].to_vec();
                        responses.push(crate::bacnet::parse_frame(&frame)?);
                        response_frames.push(frame);
                        if responses.len() >= 64 {
                            break;
                        }
                    }
                    Err(error)
                        if matches!(
                            error.kind(),
                            std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                        ) =>
                    {
                        break;
                    }
                    Err(error) => {
                        return Err(connection_io_error(id, &error.to_string()));
                    }
                }
            }
            if response_frames.is_empty() {
                return Err(CoreError::Modbus {
                    code: "BACNET_WHOIS_TIMEOUT",
                    message: format!("BACnet Who-Is 在 {timeout_ms}ms 内未收到 I-Am"),
                    details: Some(json!({ "connectionId": id, "timeoutMs": timeout_ms })),
                });
            }
            Ok(crate::bacnet::WhoisResult {
                request_frame: request,
                response_count: response_frames.len(),
                response_frames,
                responses,
                read_only: true,
                transport: "udp",
            })
        })();
        match exchange {
            Ok(result) => Ok(result),
            Err(error) => {
                self.bacnet_connections.remove(id);
                Err(error)
            }
        }
    }

    /// Executes one bounded ReadProperty transaction over the explicit UDP
    /// peer and verifies the ComplexACK against the request reference.
    pub fn bacnet_read_property(
        &mut self,
        id: &str,
        object_type: u16,
        object_instance: u32,
        property_identifier: u32,
        property_array_index: Option<u32>,
        timeout_ms: u64,
    ) -> Result<crate::bacnet::ReadPropertyResult, CoreError> {
        let exchange: Result<crate::bacnet::ReadPropertyResult, CoreError> = (|| {
            let connection = self
                .bacnet_connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let invoke_id = connection.invoke_id;
            let request = crate::bacnet::ReadPropertyRequest {
                object_type,
                object_instance,
                property_identifier,
                property_array_index,
                invoke_id,
            };
            let request_frame = crate::bacnet::build_read_property_request(
                object_type,
                object_instance,
                property_identifier,
                property_array_index,
                invoke_id,
            )?;
            connection
                .socket
                .set_read_timeout(Some(Duration::from_millis(timeout_ms.max(100))))
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            connection
                .socket
                .send(&request_frame)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            let mut buffer = [0u8; 2048];
            let size = connection
                .socket
                .recv(&mut buffer)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            let response_frame = buffer[..size].to_vec();
            let ack = crate::bacnet::parse_read_property_ack(&response_frame, Some(&request))?;
            Ok(crate::bacnet::ReadPropertyResult {
                request,
                request_frame,
                response_frame,
                ack,
                read_only: true,
                transport: "udp",
            })
        })();
        match exchange {
            Ok(result) => {
                if let Some(connection) = self.bacnet_connections.get_mut(id) {
                    connection.invoke_id = connection.invoke_id.wrapping_add(1);
                }
                Ok(result)
            }
            Err(error) => {
                self.bacnet_connections.remove(id);
                Err(error)
            }
        }
    }

    /// Opens an unsecured KNXnet/IP Tunneling v1 UDP session and completes the
    /// Connect Request/Response handshake with an explicit gateway.
    pub fn open_knx_tunnel(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        timeout_ms: u64,
    ) -> Result<crate::knx::ConnectResult, CoreError> {
        if id.trim().is_empty() {
            return Err(CoreError::Modbus {
                code: "KNX_CONNECTION_ID_INVALID",
                message: "KNX connectionId 不能为空".into(),
                details: None,
            });
        }
        if port == 0 {
            return Err(CoreError::Modbus {
                code: "KNX_PORT_INVALID",
                message: "KNX UDP 端口必须大于 0".into(),
                details: Some(json!({ "port": port })),
            });
        }
        let addr = format!("{host}:{port}");
        let socket_addr: std::net::SocketAddr = addr
            .parse()
            .map_err(|_| connection_failed(&addr, "地址解析失败"))?;
        let socket =
            UdpSocket::bind("0.0.0.0:0").map_err(|e| connection_failed(&addr, &e.to_string()))?;
        socket
            .connect(socket_addr)
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        let local = socket
            .local_addr()
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        let std::net::SocketAddr::V4(local_v4) = local else {
            return Err(CoreError::Modbus {
                code: "KNX_IPV6_UNSUPPORTED",
                message: "KNXnet/IP Tunneling 首轮只支持 UDP/IPv4 本机端点".into(),
                details: Some(json!({ "localAddress": local.to_string() })),
            });
        };
        let local_ip = local_v4.ip().octets();
        let local_port = local_v4.port();
        let request = crate::knx::build_connect_request(
            &std::net::Ipv4Addr::from(local_ip).to_string(),
            local_port,
        )?;
        socket
            .set_read_timeout(Some(Duration::from_millis(timeout_ms.max(100))))
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        socket
            .send(&request)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let mut buffer = [0u8; 2048];
        let size = socket
            .recv(&mut buffer)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let response = crate::knx::parse_connect_response(&buffer[..size])?;
        if response.status != 0 {
            return Err(CoreError::Modbus {
                code: "KNX_CONNECT_REJECTED",
                message: format!("KNX 网关拒绝连接，status=0x{:02X}", response.status),
                details: Some(json!({
                    "connectionId": id,
                    "status": response.status
                })),
            });
        }
        if response.channel_id == 0 {
            return Err(CoreError::Modbus {
                code: "KNX_CHANNEL_INVALID",
                message: "KNX 网关返回 Channel 0".into(),
                details: Some(json!({ "connectionId": id })),
            });
        }
        self.knx_connections.insert(
            id.to_string(),
            KnxConnection {
                socket,
                local_ip,
                local_port,
                channel_id: response.channel_id,
                outgoing_sequence: 0,
                expected_incoming_sequence: 0,
                keepalive_enabled: false,
                keepalive_interval_ms: 60_000,
                next_keepalive: std::time::Instant::now(),
            },
        );
        Ok(crate::knx::ConnectResult {
            request_frame: request,
            response,
            transport: "udp",
        })
    }

    /// Executes a bounded GroupValueRead transaction: request, gateway ACK,
    /// inbound GroupValueResponse, and the required client Tunneling ACK.
    pub fn knx_group_read(
        &mut self,
        id: &str,
        address_text: &str,
        timeout_ms: u64,
    ) -> Result<crate::knx::GroupReadResult, CoreError> {
        let address = crate::knx::parse_group_address(address_text)?;
        let exchange: Result<crate::knx::GroupReadResult, CoreError> = (|| {
            let connection = self
                .knx_connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let sequence = connection.outgoing_sequence;
            let request =
                crate::knx::build_group_read_request(connection.channel_id, sequence, address)?;
            connection
                .socket
                .set_read_timeout(Some(Duration::from_millis(timeout_ms.max(100))))
                .map_err(|e| connection_io_error(id, &e.to_string()))?;

            let mut acknowledgement_frame = Vec::new();
            let mut attempts = 0u8;
            while attempts < 3 {
                attempts += 1;
                connection
                    .socket
                    .send(&request)
                    .map_err(|e| connection_io_error(id, &e.to_string()))?;
                let mut buffer = [0u8; 2048];
                let received = connection.socket.recv(&mut buffer);
                match received {
                    Ok(size) => {
                        acknowledgement_frame = buffer[..size].to_vec();
                        break;
                    }
                    Err(error)
                        if matches!(
                            error.kind(),
                            std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                        ) =>
                    {
                        continue;
                    }
                    Err(error) => return Err(connection_io_error(id, &error.to_string())),
                }
            }
            if acknowledgement_frame.is_empty() {
                return Err(CoreError::Modbus {
                    code: "KNX_TUNNELING_ACK_TIMEOUT",
                    message: format!("KNX Tunneling ACK 在 {timeout_ms}ms × 3 内未收到"),
                    details: Some(json!({
                        "connectionId": id,
                        "sequence": sequence
                    })),
                });
            }
            let acknowledgement = crate::knx::parse_tunneling_ack(&acknowledgement_frame)?;
            if acknowledgement.channel_id != connection.channel_id
                || acknowledgement.sequence != sequence
            {
                return Err(CoreError::Modbus {
                    code: "KNX_TUNNELING_ACK_MISMATCH",
                    message: "KNX Tunneling ACK 的 Channel/Sequence 与请求不一致".into(),
                    details: Some(json!({
                        "connectionId": id,
                        "expectedChannel": connection.channel_id,
                        "expectedSequence": sequence,
                        "actual": acknowledgement
                    })),
                });
            }
            if acknowledgement.status != 0 {
                return Err(CoreError::Modbus {
                    code: "KNX_TUNNELING_ACK_STATUS",
                    message: format!(
                        "KNX 网关用 status=0x{:02X} 拒绝 Tunneling 请求",
                        acknowledgement.status
                    ),
                    details: Some(json!({
                        "connectionId": id,
                        "status": acknowledgement.status
                    })),
                });
            }

            let mut buffer = [0u8; 2048];
            let size = connection
                .socket
                .recv(&mut buffer)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            let response_frame = buffer[..size].to_vec();
            let response = crate::knx::parse_group_value_response(&response_frame)?;
            if response.channel_id != connection.channel_id
                || response.sequence != connection.expected_incoming_sequence
            {
                return Err(CoreError::Modbus {
                    code: "KNX_GROUP_RESPONSE_MISMATCH",
                    message: "KNX GroupValueResponse 的 Channel/Sequence 与当前隧道不一致".into(),
                    details: Some(json!({
                        "connectionId": id,
                        "expectedChannel": connection.channel_id,
                        "expectedSequence": connection.expected_incoming_sequence,
                        "actual": response
                    })),
                });
            }
            if response.destination.value != address.value {
                return Err(CoreError::Modbus {
                    code: "KNX_GROUP_ADDRESS_MISMATCH",
                    message: "KNX GroupValueResponse 组地址与请求不一致".into(),
                    details: Some(json!({
                        "connectionId": id,
                        "expected": address,
                        "actual": response.destination
                    })),
                });
            }
            let response_ack =
                crate::knx::build_tunneling_ack(response.channel_id, response.sequence, 0)?;
            connection
                .socket
                .send(&response_ack)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            Ok(crate::knx::GroupReadResult {
                request_frame: request,
                acknowledgement_frame,
                response_frame,
                response_ack_frame: response_ack,
                address,
                source: response.source,
                payload: response.payload.clone(),
                normalized_small_value: response.normalized_small_value,
                sequence,
                attempts,
                read_only: true,
                transport: "udp",
            })
        })();
        match exchange {
            Ok(result) => {
                if let Some(connection) = self.knx_connections.get_mut(id) {
                    connection.outgoing_sequence = connection.outgoing_sequence.wrapping_add(1);
                    connection.expected_incoming_sequence =
                        connection.expected_incoming_sequence.wrapping_add(1);
                }
                Ok(result)
            }
            Err(error) => {
                self.knx_connections.remove(id);
                Err(error)
            }
        }
    }

    /// Sends Disconnect Request and validates the gateway Disconnect Response.
    /// The local UDP resource is released even when the peer rejects or times
    /// out; the structured error still reports the gateway status.
    pub fn knx_disconnect(
        &mut self,
        id: &str,
        timeout_ms: u64,
    ) -> Result<crate::knx::DisconnectResponse, CoreError> {
        let exchange: Result<(Vec<u8>, crate::knx::DisconnectResponse), CoreError> = (|| {
            let connection = self
                .knx_connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let request = crate::knx::build_disconnect_request(
                connection.channel_id,
                connection.local_ip,
                connection.local_port,
            )?;
            connection
                .socket
                .set_read_timeout(Some(Duration::from_millis(timeout_ms.max(100))))
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            connection
                .socket
                .send(&request)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            let mut buffer = [0u8; 2048];
            let size = connection
                .socket
                .recv(&mut buffer)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            let response = crate::knx::parse_disconnect_response(&buffer[..size])?;
            if response.channel_id != connection.channel_id {
                return Err(CoreError::Modbus {
                    code: "KNX_DISCONNECT_CHANNEL_MISMATCH",
                    message: "KNX Disconnect Response Channel 与当前隧道不一致".into(),
                    details: Some(json!({
                        "connectionId": id,
                        "expected": connection.channel_id,
                        "actual": response.channel_id
                    })),
                });
            }
            Ok((request, response))
        })();
        self.knx_connections.remove(id);
        match exchange {
            Ok((_request, response)) if response.status == 0 => Ok(response),
            Ok((_request, response)) => Err(CoreError::Modbus {
                code: "KNX_DISCONNECT_STATUS",
                message: format!("KNX 断开失败，status=0x{:02X}", response.status),
                details: Some(json!({ "connectionId": id, "status": response.status })),
            }),
            Err(error) => Err(error),
        }
    }

    /// Sends bounded Connection State requests and validates the gateway
    /// response. A non-zero status, channel mismatch, or three timeouts drops
    /// the local session so the UI can explicitly reconnect.
    pub fn knx_connection_state(
        &mut self,
        id: &str,
        timeout_ms: u64,
    ) -> Result<crate::knx::ConnectionStateResult, CoreError> {
        let exchange: Result<crate::knx::ConnectionStateResult, CoreError> = (|| {
            let connection = self
                .knx_connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let request = crate::knx::build_connection_state_request(
                connection.channel_id,
                connection.local_ip,
                connection.local_port,
            )?;
            connection
                .socket
                .set_read_timeout(Some(Duration::from_millis(timeout_ms.max(100))))
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            let mut attempts = 0u8;
            while attempts < 3 {
                attempts += 1;
                connection
                    .socket
                    .send(&request)
                    .map_err(|e| connection_io_error(id, &e.to_string()))?;
                let mut buffer = [0u8; 2048];
                match connection.socket.recv(&mut buffer) {
                    Ok(size) => {
                        let response =
                            crate::knx::parse_connection_state_response(&buffer[..size])?;
                        if response.channel_id != connection.channel_id {
                            return Err(CoreError::Modbus {
                                code: "KNX_CONNECTION_STATE_CHANNEL_MISMATCH",
                                message: "KNX Connection State Response Channel 与当前隧道不一致"
                                    .into(),
                                details: Some(json!({
                                    "connectionId": id,
                                    "expected": connection.channel_id,
                                    "actual": response.channel_id
                                })),
                            });
                        }
                        if response.status != 0 {
                            return Err(CoreError::Modbus {
                                code: "KNX_CONNECTION_STATE_STATUS",
                                message: format!(
                                    "KNX Connection State Response status=0x{:02X}",
                                    response.status
                                ),
                                details: Some(json!({
                                    "connectionId": id,
                                    "status": response.status
                                })),
                            });
                        }
                        return Ok(crate::knx::ConnectionStateResult {
                            request_frame: request,
                            response,
                            attempts,
                            transport: "udp",
                        });
                    }
                    Err(error)
                        if matches!(
                            error.kind(),
                            std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                        ) =>
                    {
                        continue;
                    }
                    Err(error) => return Err(connection_io_error(id, &error.to_string())),
                }
            }
            Err(CoreError::Modbus {
                code: "KNX_CONNECTION_STATE_TIMEOUT",
                message: format!("KNX Connection State 在 {timeout_ms}ms × 3 内未收到响应"),
                details: Some(json!({
                    "connectionId": id,
                    "timeoutMs": timeout_ms,
                    "attempts": attempts
                })),
            })
        })();
        if exchange.is_err() {
            self.knx_connections.remove(id);
        }
        exchange
    }

    /// Enables periodic Connection State keepalive for one KNX tunnel.
    pub fn start_knx_keepalive(&mut self, id: &str, interval_ms: u32) -> Result<(), CoreError> {
        let interval = interval_ms.max(100);
        let connection = self
            .knx_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        connection.keepalive_enabled = true;
        connection.keepalive_interval_ms = interval;
        connection.next_keepalive =
            std::time::Instant::now() + Duration::from_millis(u64::from(interval));
        Ok(())
    }

    /// Disables periodic keepalive without dropping the KNX tunnel.
    pub fn stop_knx_keepalive(&mut self, id: &str) -> Result<(), CoreError> {
        let connection = self
            .knx_connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        connection.keepalive_enabled = false;
        Ok(())
    }

    /// Returns whether periodic keepalive is active. A missing connection is
    /// reported as stopped rather than treated as a healthy active tunnel.
    pub fn knx_keepalive_status(&self, id: &str) -> bool {
        self.knx_connections
            .get(id)
            .is_some_and(|connection| connection.keepalive_enabled)
    }

    /// Executes due KNX Connection State probes. Any failed probe removes the
    /// logical session; recovery is always an explicit new Connect request.
    pub fn due_knx_keepalives(&mut self) {
        let due: Vec<(String, u64)> = self
            .knx_connections
            .iter()
            .filter(|(_, connection)| {
                connection.keepalive_enabled
                    && connection.next_keepalive <= std::time::Instant::now()
            })
            .map(|(id, connection)| {
                (
                    id.clone(),
                    u64::from(connection.keepalive_interval_ms.min(1_000)),
                )
            })
            .collect();
        for (id, timeout_ms) in due {
            let _ = self.knx_connection_state(&id, timeout_ms);
            if let Some(connection) = self.knx_connections.get_mut(&id) {
                connection.next_keepalive = std::time::Instant::now()
                    + Duration::from_millis(u64::from(connection.keepalive_interval_ms));
            }
        }
    }

    /// 打开三菱 MC 协议 TCP 连接(3E/4E 帧,端口通常 5000)。
    pub fn open_mc_tcp(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        route: crate::mc_frame::AccessRoute,
        frame_type: crate::mc_frame::FrameType,
        watchdog: u16,
    ) -> Result<(), CoreError> {
        let addr = format!("{host}:{port}");
        let stream = TcpStream::connect_timeout(
            &addr
                .parse()
                .map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.connections.insert(
            id.to_string(),
            Connection::McTcp {
                stream,
                route,
                frame_type,
                ascii: false,
                watchdog,
                sequence: 0,
            },
        );
        Ok(())
    }

    /// 打开三菱 MC ASCII 模式 TCP 连接(TCP 5001)。
    pub fn open_mc_tcp_ascii(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        route: crate::mc_frame::AccessRoute,
        frame_type: crate::mc_frame::FrameType,
        watchdog: u16,
    ) -> Result<(), CoreError> {
        let addr = format!("{host}:{port}");
        let stream = TcpStream::connect_timeout(
            &addr
                .parse()
                .map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.connections.insert(
            id.to_string(),
            Connection::McTcp {
                stream,
                route,
                frame_type,
                ascii: true,
                watchdog,
                sequence: 0,
            },
        );
        Ok(())
    }

    /// 执行一次 MC 事务:发请求帧,收响应帧,返回解析后的 McResponse。
    ///
    /// Binary 模式:帧边界按「长度字段自描述」重组(§2.1.5)。
    /// ASCII 模式:发送 ASCII 文本帧;响应按长度字段字符数读全。
    pub fn mc_transact(
        &mut self,
        id: &str,
        req_data: &[u8],
    ) -> Result<crate::mc_frame::McResponse, CoreError> {
        let conn = self
            .connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let Connection::McTcp {
            stream,
            route,
            frame_type,
            ascii,
            watchdog,
            sequence,
        } = conn
        else {
            return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 MC 连接,请用 open_mc_tcp 打开"),
                details: None,
            });
        };

        *sequence = sequence.wrapping_add(1);

        if *ascii {
            // ==== ASCII 模式(TCP 5001)====
            // 用 mc_pdu 的逻辑字段重新组装 ASCII 请求太复杂——ASCII 事务层
            // 直接由调用方(protocol.rs)走专用入口 mc_transact_ascii,
            // 此分支报错指路。
            let _ = (route, watchdog);
            return Err(CoreError::Modbus {
                code: "MC_ASCII_NEEDS_DEDICATED_PATH",
                message: "ASCII 事务请用 mc_transact_ascii(传入地址而非裸 PDU)".into(),
                details: None,
            });
        }

        // ==== Binary 模式 ====
        let frame = crate::mc_frame::build_request_frame(
            *frame_type,
            route,
            *watchdog,
            req_data,
            *sequence,
        );
        stream
            .write_all(&frame)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        stream
            .flush()
            .map_err(|e| connection_io_error(id, &e.to_string()))?;

        // 读响应:先读固定头(3E 9B / 4E 11B),其中含长度字段;再按长度读剩余
        let header_len = match frame_type {
            crate::mc_frame::FrameType::Type3E => 9usize,
            crate::mc_frame::FrameType::Type4E => 11usize,
        };
        let mut header = vec![0u8; header_len];
        read_exact(stream, &mut header).map_err(|e| connection_io_error(id, &e.to_string()))?;
        // 长度字段位置:3E 在 [7..9],4E 在 [9..11]
        let len_off = header_len - 2;
        let resp_data_len = u16::from_le_bytes([header[len_off], header[len_off + 1]]) as usize;
        // 剩余 = 结束代码(2) + 数据区(resp_data_len - 2)
        let rest_len = resp_data_len.saturating_sub(2) + 2;
        let mut rest = vec![0u8; rest_len];
        read_exact(stream, &mut rest).map_err(|e| connection_io_error(id, &e.to_string()))?;

        let mut full = header;
        full.extend_from_slice(&rest);
        crate::mc_frame::parse_response_frame(&full)
    }

    /// ASCII 模式成批读事务(0401):地址+点数 → ASCII 帧 → 收发 → 字/位值。
    pub fn mc_transact_ascii_read(
        &mut self,
        id: &str,
        address: &str,
        points: u16,
    ) -> Result<(u16, bool, Vec<u16>), CoreError> {
        let (route, frame_type, watchdog, sequence) = {
            let conn = self
                .connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let Connection::McTcp {
                route,
                frame_type,
                watchdog,
                sequence,
                ascii,
                ..
            } = conn
            else {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 不是 MC 连接"),
                    details: None,
                });
            };
            if !*ascii {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: "连接是 Binary 模式,ASCII 读请用 ASCII 连接(端口 5001)".into(),
                    details: None,
                });
            }
            *sequence = sequence.wrapping_add(1);
            (route.clone(), *frame_type, *watchdog, *sequence) // 复制值出块,杜绝引用逃逸
        };
        let req = crate::mc_ascii::build_ascii_read_request(
            frame_type, sequence, &route, watchdog, address, points,
        )?;
        self.mc_ascii_roundtrip(id, req.as_bytes(), address, points)
    }

    /// ASCII 模式成批写事务(1401)。
    pub fn mc_transact_ascii_write(
        &mut self,
        id: &str,
        address: &str,
        values: &[u16],
    ) -> Result<u16, CoreError> {
        let (route, frame_type, watchdog, sequence, is_bit) = {
            let conn = self
                .connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let Connection::McTcp {
                route,
                frame_type,
                watchdog,
                sequence,
                ascii,
                ..
            } = conn
            else {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 不是 MC 连接"),
                    details: None,
                });
            };
            if !*ascii {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: "连接是 Binary 模式,ASCII 写请用 ASCII 连接(端口 5001)".into(),
                    details: None,
                });
            }
            let is_bit = crate::mc_address::parse_mc_address(address)
                .map(|a| a.is_bit)
                .unwrap_or(false);
            (route.clone(), *frame_type, *watchdog, sequence, is_bit)
        };
        *sequence = sequence.wrapping_add(1);
        let req = crate::mc_ascii::build_ascii_write_request(
            frame_type, *sequence, &route, watchdog, address, values,
        )?;
        let resp_text = self.mc_ascii_send_recv(id, req.as_bytes())?;
        let resp = crate::mc_ascii::parse_ascii_response(&resp_text)?;
        if resp.end_code != 0 {
            return Ok(resp.end_code);
        }
        let _ = is_bit;
        Ok(0)
    }

    /// ASCII 收发底层:发文本,按响应长度字段读全。
    /// 打开三菱 A-1E/SLMP-1E TCP 连接(A 系列 E71 / FX3U-ENET / FX5U)。
    pub fn open_mc_1e_tcp(&mut self, id: &str, host: &str, port: u16) -> Result<(), CoreError> {
        let addr = format!("{host}:{port}");
        let stream = TcpStream::connect_timeout(
            &addr
                .parse()
                .map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.connections
            .insert(id.to_string(), Connection::Mc1eTcp { stream });
        Ok(())
    }

    /// 1E TCP 事务:发 1E 请求帧,按命令/点数预计算响应长度收满,原样返回响应字节。
    pub fn mc_1e_transact(&mut self, id: &str, request: &[u8]) -> Result<Vec<u8>, CoreError> {
        let cmd = *request.first().ok_or_else(|| CoreError::Modbus {
            code: "MC_1E_EMPTY_REQUEST",
            message: "1E 请求为空".into(),
            details: None,
        })?;
        let points = if request.len() >= 12 {
            u16::from_le_bytes([request[10], request[11]])
        } else {
            0
        };
        // 响应长度预推断:81 + 结束码 + 数据;异常(5B)时 +2
        let data_len = match cmd {
            0x00 => (points as usize + 7) / 8, // 位读:每 8 点 1 字节(§3.4.2 位打包)
            0x01 => points as usize * 2,       // 字读
            _ => 0,                            // 写:仅 81 00
        };
        let expected = 2 + data_len;

        let conn = self
            .connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let Connection::Mc1eTcp { stream } = conn else {
            return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 1E TCP 连接,请用 open_mc_1e_tcp 打开"),
                details: None,
            });
        };
        stream
            .write_all(request)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        stream
            .flush()
            .map_err(|e| connection_io_error(id, &e.to_string()))?;

        // 先读 2 字节(副帧头+结束码)判断正常/异常,再按需读数据
        let mut head = [0u8; 2];
        read_exact(stream, &mut head).map_err(|e| connection_io_error(id, &e.to_string()))?;
        if head[1] == 0x5B {
            // 异常详细码:5B <det> 00
            let mut det = [0u8; 2];
            read_exact(stream, &mut det).map_err(|e| connection_io_error(id, &e.to_string()))?;
            return Ok(vec![head[0], head[1], det[0], det[1]]);
        }
        if head[1] != 0x00 {
            return Ok(head.to_vec()); // 其他异常码:无数据
        }
        let mut rest = vec![0u8; expected - 2];
        if !rest.is_empty() {
            read_exact(stream, &mut rest).map_err(|e| connection_io_error(id, &e.to_string()))?;
        }
        let mut resp = head.to_vec();
        resp.extend_from_slice(&rest);
        Ok(resp)
    }

    /// 打开三菱 MC UDP 连接(§2.5:MC/SLMP 3E/4E 可直接跑 UDP,同一端口体系)。
    pub fn open_mc_udp(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        route: crate::mc_frame::AccessRoute,
        frame_type: crate::mc_frame::FrameType,
        watchdog: u16,
    ) -> Result<(), CoreError> {
        let peer_addr: std::net::SocketAddr = format!("{host}:{port}")
            .parse()
            .map_err(|_| connection_failed(&format!("{host}:{port}"), "地址解析失败"))?;
        let socket = UdpSocket::bind("0.0.0.0:0")
            .map_err(|e| connection_failed(&peer_addr.to_string(), &e.to_string()))?;
        socket
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&peer_addr.to_string(), &e.to_string()))?;
        self.connections.insert(
            id.to_string(),
            Connection::McUdp {
                socket,
                peer_addr,
                route,
                frame_type,
                watchdog,
                sequence: 0,
            },
        );
        Ok(())
    }

    /// MC UDP 事务:发 3E/4E Binary 帧,收响应(4E 用序列号丢弃乱序旧包)。
    pub fn mc_udp_transact(
        &mut self,
        id: &str,
        req_data: &[u8],
    ) -> Result<crate::mc_frame::McResponse, CoreError> {
        let (frame_type, seq) = {
            let conn = self
                .connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let Connection::McUdp {
                socket,
                peer_addr,
                route,
                frame_type,
                watchdog,
                sequence,
            } = conn
            else {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 不是 MC UDP 连接,请用 open_mc_udp 打开"),
                    details: None,
                });
            };
            *sequence = sequence.wrapping_add(1);
            let frame = crate::mc_frame::build_request_frame(
                *frame_type,
                route,
                *watchdog,
                req_data,
                *sequence,
            );
            socket
                .send_to(&frame, *peer_addr)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            (*frame_type, *sequence)
        };

        // 收响应:最多重试 10 次丢弃乱序/旧序列号的包
        const MAX_RETRY: u8 = 10;
        let mut buf = [0u8; 2048];
        for _ in 0..MAX_RETRY {
            let (n, _from) = {
                let conn = self
                    .connections
                    .get_mut(id)
                    .ok_or_else(|| connection_not_found(id))?;
                let Connection::McUdp { socket, .. } = conn else {
                    unreachable!()
                };
                socket
                    .recv_from(&mut buf)
                    .map_err(|e| connection_io_error(id, &e.to_string()))?
            };
            let resp = crate::mc_frame::parse_response_frame(&buf[..n])?;
            // 4E:校验序列号配对;不匹配的旧包丢弃继续收
            if frame_type == crate::mc_frame::FrameType::Type4E && resp.sequence != seq {
                continue;
            }
            return Ok(resp);
        }
        Err(connection_io_error(id, "MC UDP 连续收到序列号不匹配的响应"))
    }

    fn mc_ascii_send_recv(&mut self, id: &str, req: &[u8]) -> Result<String, CoreError> {
        let conn = self
            .connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let Connection::McTcp { stream, ascii, .. } = conn else {
            return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 MC 连接"),
                details: None,
            });
        };
        if !*ascii {
            return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: "非 ASCII 连接".into(),
                details: None,
            });
        }
        use std::io::Write;
        stream
            .write_all(req)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        stream
            .flush()
            .map_err(|e| connection_io_error(id, &e.to_string()))?;

        // ASCII 响应:副帧头(4) [+序列号(4)] + 路由(10) + 长度(4) → 然后按长度读
        // 先读前 4 字符判 3E/4E
        let mut head4 = [0u8; 4];
        read_exact(stream, &mut head4).map_err(|e| connection_io_error(id, &e.to_string()))?;
        let head4s = String::from_utf8_lossy(&head4).to_uppercase();
        let (route_chars, seq_chars) = if head4s.starts_with("D000") {
            (10usize, 0usize)
        } else if head4s.starts_with("D400") {
            (10usize, 4usize)
        } else {
            return Err(CoreError::Modbus {
                code: "MC_BAD_SUBHEADER",
                message: format!("ASCII 响应副帧头异常: {head4s}"),
                details: None,
            });
        };
        // 读 序列号 + 路由 + 长度
        let mut mid = vec![0u8; seq_chars + route_chars + 4];
        read_exact(stream, &mut mid).map_err(|e| connection_io_error(id, &e.to_string()))?;
        let mid_s = String::from_utf8_lossy(&mid).to_string();
        let len_str = &mid_s[mid_s.len() - 4..];
        let data_len = usize::from_str_radix(len_str, 16).map_err(|_| CoreError::Modbus {
            code: "MC_ASCII_BAD_CHAR",
            message: format!("长度字段「{len_str}」非法"),
            details: None,
        })?;
        // 剩余 = 结束代码(4 字符) + 数据区。数据字符数在位(单字符)/字(2字符/字节)间
        // 无法从长度字段区分——按字模式上限"尽力读":满上限或 50ms 无新字节即返回。
        let rest_target = 4 + (data_len.saturating_sub(2)) * 2;
        let mut rest: Vec<u8> = Vec::with_capacity(rest_target);
        // 临时把读超时压到 50ms 做空闲判定,读完恢复
        let _ = stream.set_read_timeout(Some(Duration::from_millis(50)));
        let idle_deadline = std::time::Instant::now() + Duration::from_millis(500);
        while rest.len() < rest_target {
            let mut tmp = [0u8; 256];
            let want = (rest_target - rest.len()).min(256);
            match stream.read(&mut tmp[..want]) {
                Ok(0) => break,
                Ok(n) => {
                    rest.extend_from_slice(&tmp[..n]);
                    let _ = stream.set_read_timeout(Some(Duration::from_millis(50)));
                }
                Err(ref e)
                    if e.kind() == std::io::ErrorKind::WouldBlock
                        || e.kind() == std::io::ErrorKind::TimedOut =>
                {
                    if !rest.is_empty() && std::time::Instant::now() > idle_deadline {
                        break;
                    }
                    if !rest.is_empty() {
                        // 已有数据且空闲 50ms → 帧完整(位模式的自然边界)
                        break;
                    }
                    // 还没数据:继续等到总截止
                    if std::time::Instant::now() > idle_deadline {
                        return Err(connection_io_error(id, "ASCII 响应空闲超时"));
                    }
                }
                Err(e) => {
                    let _ = stream.set_read_timeout(Some(TCP_READ_TIMEOUT));
                    return Err(connection_io_error(id, &e.to_string()));
                }
            }
        }
        let _ = stream.set_read_timeout(Some(TCP_READ_TIMEOUT));
        let full = format!("{head4s}{mid_s}{}", String::from_utf8_lossy(&rest));
        Ok(full)
    }

    /// ASCII 读事务的组装+解析。
    fn mc_ascii_roundtrip(
        &mut self,
        id: &str,
        req: &[u8],
        address: &str,
        points: u16,
    ) -> Result<(u16, bool, Vec<u16>), CoreError> {
        let is_bit = crate::mc_address::parse_mc_address(address)
            .map(|a| a.is_bit)
            .unwrap_or(false);
        let resp_text = self.mc_ascii_send_recv(id, req)?;
        let resp = crate::mc_ascii::parse_ascii_response(&resp_text)?;
        if resp.end_code != 0 {
            return Ok((resp.end_code, is_bit, Vec::new()));
        }
        let values = if is_bit {
            crate::mc_ascii::ascii_bits(&resp, points as usize)?
        } else {
            crate::mc_ascii::ascii_words(&resp, points as usize)?
        };
        Ok((0, is_bit, values))
    }

    /// 打开 UDP "连接"(绑定本地,设定 peer)。
    pub fn open_udp(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        unit_id: u8,
        framing: TcpFraming,
    ) -> Result<(), CoreError> {
        let peer_addr: std::net::SocketAddr = format!("{host}:{port}")
            .parse()
            .map_err(|_| connection_failed(&format!("{host}:{port}"), "地址解析失败"))?;
        let socket = UdpSocket::bind("0.0.0.0:0")
            .map_err(|e| connection_failed(&peer_addr.to_string(), &e.to_string()))?;
        socket
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&peer_addr.to_string(), &e.to_string()))?;
        self.connections.insert(
            id.to_string(),
            Connection::Udp {
                socket,
                peer_addr,
                unit_id,
                framing,
            },
        );
        Ok(())
    }

    /// 关闭连接。
    /// 临时调整 TCP/UDP 连接的读超时(ms);0 = 恢复默认(TCP_READ_TIMEOUT)。
    /// 供站号扫描按用户参数缩短每站探测等待(旧实现忽略 timeout_ms,
    /// 247 空站 × 5s 默认超时 = 20 分钟主循环假死)。
    pub fn set_connection_read_timeout_ms(
        &mut self,
        id: &str,
        timeout_ms: u32,
    ) -> Result<(), CoreError> {
        let dur = if timeout_ms == 0 {
            TCP_READ_TIMEOUT
        } else {
            std::time::Duration::from_millis(timeout_ms.max(20) as u64)
        };
        match self.connections.get_mut(id) {
            Some(Connection::Tcp { stream, .. }) => {
                let _ = stream.set_read_timeout(Some(dur));
            }
            Some(Connection::Udp { socket, .. }) => {
                let _ = socket.set_read_timeout(Some(dur));
            }
            _ => {}
        }
        if let Some(connection) = self.ge_srtp_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.fuji_sph_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.fatek_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.keyence_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.ls_xgt_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.ads_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.enip_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.mqtt_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.iec104_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.dnp3_connections.get_mut(id) {
            let _ = connection.stream.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.bacnet_connections.get_mut(id) {
            let _ = connection.socket.set_read_timeout(Some(dur));
        }
        if let Some(connection) = self.knx_connections.get_mut(id) {
            let _ = connection.socket.set_read_timeout(Some(dur));
        }
        Ok(())
    }

    pub fn close_connection(&mut self, id: &str) -> Result<(), CoreError> {
        let enip_removed = if let Some(mut connection) = self.enip_connections.remove(id) {
            // UnregisterSession is best-effort on shutdown: a peer may have
            // already closed, but the local lifecycle must still be released.
            let context = connection.sender_context.wrapping_add(1);
            let frame = crate::enip::build_unregister_session(connection.session_handle, context);
            let _ = connection
                .stream
                .write_all(&frame)
                .and_then(|_| connection.stream.flush());
            true
        } else {
            false
        };
        let mqtt_removed = if let Some(mut connection) = self.mqtt_connections.remove(id) {
            let frame = crate::mqtt::build_disconnect();
            let _ = connection
                .stream
                .write_all(&frame)
                .and_then(|_| connection.stream.flush());
            true
        } else {
            false
        };
        let iec104_removed = if let Some(mut connection) = self.iec104_connections.remove(id) {
            // STOPDT is best-effort during generic teardown. A field peer may
            // already have closed the socket; local resource release must not
            // be blocked by waiting for STOPDT_CON.
            let frame =
                crate::iec104::build_u_frame(crate::iec104::UFunction::StopDataTransferActivation);
            let _ = connection
                .stream
                .write_all(&frame)
                .and_then(|_| connection.stream.flush());
            true
        } else {
            false
        };
        let dnp3_removed = self.dnp3_connections.remove(id).is_some();
        let bacnet_removed = self.bacnet_connections.remove(id).is_some();
        let knx_removed = self.knx_connections.remove(id).is_some();
        if self.connections.remove(id).is_some()
            || self.ge_srtp_connections.remove(id).is_some()
            || self.fuji_sph_connections.remove(id).is_some()
            || self.fatek_connections.remove(id).is_some()
            || self.keyence_connections.remove(id).is_some()
            || self.ls_xgt_connections.remove(id).is_some()
            || self.ads_connections.remove(id).is_some()
            || enip_removed
            || mqtt_removed
            || iec104_removed
            || dnp3_removed
            || bacnet_removed
            || knx_removed
        {
            // 级联清理引用该连接的轮询流:否则死流每 interval 空转且 UI 无感
            let stale: Vec<String> = self
                .poll_streams
                .iter()
                .filter(|(_, s)| s.connection_id == id)
                .map(|(k, _)| k.clone())
                .collect();
            for k in stale {
                self.poll_streams.remove(&k);
            }
            Ok(())
        } else {
            Err(CoreError::Modbus {
                code: "CONNECTION_NOT_FOUND",
                message: format!("连接 {id} 不存在"),
                details: Some(serde_json::json!({ "connectionId": id })),
            })
        }
    }

    /// 在 TCP 连接上执行 Modbus 事务:发送 PDU,接收响应 PDU。
    /// 根据 framing 自动选择 MBAP / RTU / ASCII 包装。
    pub fn transact_tcp(&mut self, id: &str, pdu: &[u8]) -> Result<Vec<u8>, CoreError> {
        match self.connections.get_mut(id) {
            Some(Connection::Tcp {
                stream,
                unit_id,
                framing,
            }) => {
                match framing {
                    TcpFraming::Standard => {
                        // MBAP 模式:TID + 协议头 + PDU
                        let tid = self.tid_gen.next();
                        let frame = modbus_tcp::build_mbap_frame(tid, *unit_id, pdu);
                        stream
                            .write_all(&frame)
                            .map_err(|e| connection_io_error(id, &e.to_string()))?;
                        stream
                            .flush()
                            .map_err(|e| connection_io_error(id, &e.to_string()))?;
                        // TID 不匹配时丢弃该响应并重新读取(迟到响应/多主站串扰),
                        // 而非直接报错——文档 §9 要求"丢弃不匹配响应继续等"。
                        const MAX_TID_RETRIES: u8 = 3;
                        for _ in 0..MAX_TID_RETRIES {
                            let mut header_buf = [0u8; MBAP_HEADER_LEN];
                            read_exact(stream, &mut header_buf)
                                .map_err(|e| connection_io_error(id, &e.to_string()))?;
                            let header = parse_mbap_header(&header_buf)?;
                            let pdu_len = usize::from(header.length).saturating_sub(1);
                            let mut pdu_buf = vec![0u8; pdu_len];
                            read_exact(stream, &mut pdu_buf)
                                .map_err(|e| connection_io_error(id, &e.to_string()))?;
                            if header.transaction_id == tid {
                                return Ok(pdu_buf);
                            }
                            // TID 不匹配:丢弃此响应,继续等待正确 TID
                        }
                        return Err(connection_io_error(
                            id,
                            &format!("连续 {MAX_TID_RETRIES} 次收到 TID 不匹配的响应"),
                        ));
                    }
                    TcpFraming::RtuOverTcp => {
                        // RTU over TCP:发完整 RTU ADU(unit + pdu + crc)
                        let adu = crate::modbus_rtu::RtuFrame::request(*unit_id, pdu[0], &pdu[1..])
                            .map_err(CoreError::from)?
                            .encode();
                        stream
                            .write_all(&adu)
                            .map_err(|e| connection_io_error(id, &e.to_string()))?;
                        stream
                            .flush()
                            .map_err(|e| connection_io_error(id, &e.to_string()))?;
                        // 读 RTU 响应:最少 4 字节,按 PDU 结构推断长度
                        let mut buf = vec![0u8; 256];
                        let n = read_rtu_response_stream(stream, &mut buf)
                            .map_err(|e| connection_io_error(id, &e.to_string()))?;
                        let frame = crate::modbus_rtu::RtuFrame::decode(
                            &buf[..n],
                            crate::modbus_rtu::RtuFrameRole::Response,
                        )
                        .map_err(CoreError::from)?;
                        // 返回 PDU(FC + data)
                        let mut response_pdu = vec![frame.function_code()];
                        response_pdu.extend_from_slice(frame.data());
                        Ok(response_pdu)
                    }
                    TcpFraming::AsciiOverTcp => {
                        // ASCII over TCP:发 ASCII 帧
                        let frame = crate::modbus_ascii::build_ascii_frame(*unit_id, pdu);
                        stream
                            .write_all(&frame)
                            .map_err(|e| connection_io_error(id, &e.to_string()))?;
                        stream
                            .flush()
                            .map_err(|e| connection_io_error(id, &e.to_string()))?;
                        // 读 ASCII 响应直到 CRLF
                        let mut buf = vec![0u8; 1024];
                        let n = read_ascii_response_stream(stream, &mut buf)
                            .map_err(|e| connection_io_error(id, &e.to_string()))?;
                        let (_resp_unit_id, resp_pdu) =
                            crate::modbus_ascii::parse_ascii_frame(&buf[..n])
                                .map_err(CoreError::from)?;
                        Ok(resp_pdu)
                    }
                }
            }
            Some(Connection::Udp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 UDP，请用 transact_udp"),
                details: None,
            }),
            Some(Connection::McTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 MC 连接，请用 mc_transact"),
                details: None,
            }),
            Some(Connection::McUdp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 MC UDP 连接，请用 mc_udp_transact"),
                details: None,
            }),
            Some(Connection::Mc1eTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 1E TCP 连接，请用 mc_1e_transact"),
                details: None,
            }),
            Some(Connection::S7Tcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 S7 连接，请用 s7_read/s7_write"),
                details: None,
            }),
            Some(Connection::FinsTcp { .. }) | Some(Connection::FinsUdp { .. }) => {
                Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 是 FINS 连接，请用 fins_read/fins_write"),
                    details: None,
                })
            }
            Some(Connection::FwTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 Fetch/Write 连接，请用 fw_read/fw_write"),
                details: None,
            }),
            Some(Connection::PpiTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 PPI 连接，请用 ppi_read/ppi_write"),
                details: None,
            }),
            None => Err(connection_not_found(id)),
        }
    }

    /// 在 UDP 连接上执行 Modbus 事务。
    pub fn transact_udp(&mut self, id: &str, pdu: &[u8]) -> Result<Vec<u8>, CoreError> {
        match self.connections.get_mut(id) {
            Some(Connection::Udp {
                socket,
                peer_addr,
                unit_id,
                framing,
            }) => match framing {
                TcpFraming::Standard => {
                    let tid = self.tid_gen.next();
                    let frame = modbus_tcp::build_mbap_frame(tid, *unit_id, pdu);
                    socket
                        .send_to(&frame, *peer_addr)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let mut buf = [0u8; 1024];
                    let (n, _) = socket
                        .recv_from(&mut buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let (_header, response_pdu) =
                        modbus_tcp::parse_mbap_frame(&buf[..n]).map_err(CoreError::from)?;
                    Ok(response_pdu)
                }
                TcpFraming::RtuOverTcp => {
                    let adu = crate::modbus_rtu::RtuFrame::request(*unit_id, pdu[0], &pdu[1..])
                        .map_err(CoreError::from)?
                        .encode();
                    socket
                        .send_to(&adu, *peer_addr)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let mut buf = [0u8; 1024];
                    let (n, _) = socket
                        .recv_from(&mut buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let frame = crate::modbus_rtu::RtuFrame::decode(
                        &buf[..n],
                        crate::modbus_rtu::RtuFrameRole::Response,
                    )
                    .map_err(CoreError::from)?;
                    let mut response_pdu = vec![frame.function_code()];
                    response_pdu.extend_from_slice(frame.data());
                    Ok(response_pdu)
                }
                TcpFraming::AsciiOverTcp => {
                    let frame = crate::modbus_ascii::build_ascii_frame(*unit_id, pdu);
                    socket
                        .send_to(&frame, *peer_addr)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let mut buf = [0u8; 1024];
                    let (n, _) = socket
                        .recv_from(&mut buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let (_resp_unit_id, resp_pdu) =
                        crate::modbus_ascii::parse_ascii_frame(&buf[..n])
                            .map_err(CoreError::from)?;
                    Ok(resp_pdu)
                }
            },
            Some(Connection::Tcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 TCP，请用 transact_tcp"),
                details: None,
            }),
            Some(Connection::McTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 MC 连接，请用 mc_transact"),
                details: None,
            }),
            Some(Connection::McUdp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 MC UDP 连接，请用 mc_udp_transact"),
                details: None,
            }),
            Some(Connection::Mc1eTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 1E TCP 连接，请用 mc_1e_transact"),
                details: None,
            }),
            Some(Connection::S7Tcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 S7 连接，请用 s7_read/s7_write"),
                details: None,
            }),
            Some(Connection::FinsTcp { .. }) | Some(Connection::FinsUdp { .. }) => {
                Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 是 FINS 连接，请用 fins_read/fins_write"),
                    details: None,
                })
            }
            Some(Connection::FwTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 Fetch/Write 连接，请用 fw_read/fw_write"),
                details: None,
            }),
            Some(Connection::PpiTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 PPI 连接，请用 ppi_read/ppi_write"),
                details: None,
            }),
            None => Err(connection_not_found(id)),
        }
    }

    /// 探测指定站号是否在线(用已有 TCP/UDP 连接,临时用指定 unit_id 发 FC03 读 1 寄存器)。
    /// 返回首次响应耗时(ms)。超时或错误返回 Err。
    pub fn probe_station(
        &mut self,
        id: &str,
        station_id: u8,
        request_pdu: &[u8],
    ) -> Result<u64, CoreError> {
        let started = std::time::Instant::now();
        match self.connections.get_mut(id) {
            Some(Connection::Tcp {
                stream, framing, ..
            }) => {
                let _ = stream; // 借用检查
                let _ = framing;
                // 临时构建请求帧,用 station_id 作 unit_id
                // 先保存原 unit_id,替换,调用内部发送,再恢复
                // 简化:直接在此构建帧并发送
                self.send_and_recv_probe_tcp(id, station_id, request_pdu)?;
                Ok(started.elapsed().as_millis() as u64)
            }
            Some(Connection::Udp { .. }) => {
                self.send_and_recv_probe_udp(id, station_id, request_pdu)?;
                Ok(started.elapsed().as_millis() as u64)
            }
            Some(Connection::McTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: "MC 连接不支持 Modbus 站号探测".into(),
                details: None,
            }),
            Some(Connection::McUdp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: "MC UDP 连接不支持 Modbus 站号探测".into(),
                details: None,
            }),
            Some(Connection::Mc1eTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: "1E TCP 连接不支持 Modbus 站号探测".into(),
                details: None,
            }),
            Some(Connection::S7Tcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: "S7 连接不支持 Modbus 站号探测".into(),
                details: None,
            }),
            Some(Connection::FinsTcp { .. }) | Some(Connection::FinsUdp { .. }) => {
                Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: "FINS 连接不支持 Modbus 站号探测".into(),
                    details: None,
                })
            }
            Some(Connection::FwTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: "Fetch/Write 连接不支持 Modbus 站号探测".into(),
                details: None,
            }),
            Some(Connection::PpiTcp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: "PPI 连接不支持 Modbus 站号探测".into(),
                details: None,
            }),
            None => Err(connection_not_found(id)),
        }
    }

    fn send_and_recv_probe_tcp(
        &mut self,
        id: &str,
        station_id: u8,
        request_pdu: &[u8],
    ) -> Result<(), CoreError> {
        match self.connections.get_mut(id) {
            Some(Connection::Tcp {
                stream, framing, ..
            }) => match framing {
                TcpFraming::Standard => {
                    let tid = self.tid_gen.next();
                    let frame = modbus_tcp::build_mbap_frame(tid, station_id, request_pdu);
                    stream
                        .write_all(&frame)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    stream
                        .flush()
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let mut header_buf = [0u8; MBAP_HEADER_LEN];
                    read_exact(stream, &mut header_buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let header = parse_mbap_header(&header_buf)?;
                    let pdu_len = usize::from(header.length).saturating_sub(1);
                    let mut pdu_buf = vec![0u8; pdu_len];
                    read_exact(stream, &mut pdu_buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    Ok(())
                }
                TcpFraming::RtuOverTcp => {
                    let adu = crate::modbus_rtu::RtuFrame::request(
                        station_id,
                        request_pdu[0],
                        &request_pdu[1..],
                    )
                    .map_err(CoreError::from)?
                    .encode();
                    stream
                        .write_all(&adu)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    stream
                        .flush()
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let mut buf = vec![0u8; 256];
                    read_rtu_response_stream(stream, &mut buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    Ok(())
                }
                TcpFraming::AsciiOverTcp => {
                    let frame = crate::modbus_ascii::build_ascii_frame(station_id, request_pdu);
                    stream
                        .write_all(&frame)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    stream
                        .flush()
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let mut buf = vec![0u8; 1024];
                    read_ascii_response_stream(stream, &mut buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    Ok(())
                }
            },
            _ => Err(connection_not_found(id)),
        }
    }

    fn send_and_recv_probe_udp(
        &mut self,
        id: &str,
        station_id: u8,
        request_pdu: &[u8],
    ) -> Result<(), CoreError> {
        match self.connections.get_mut(id) {
            Some(Connection::Udp {
                socket,
                peer_addr,
                framing,
                ..
            }) => match framing {
                TcpFraming::Standard => {
                    let tid = self.tid_gen.next();
                    let frame = modbus_tcp::build_mbap_frame(tid, station_id, request_pdu);
                    socket
                        .send_to(&frame, *peer_addr)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let mut buf = [0u8; 1024];
                    socket
                        .recv_from(&mut buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    Ok(())
                }
                TcpFraming::RtuOverTcp => {
                    let adu = crate::modbus_rtu::RtuFrame::request(
                        station_id,
                        request_pdu[0],
                        &request_pdu[1..],
                    )
                    .map_err(CoreError::from)?
                    .encode();
                    socket
                        .send_to(&adu, *peer_addr)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let mut buf = [0u8; 1024];
                    socket
                        .recv_from(&mut buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    Ok(())
                }
                TcpFraming::AsciiOverTcp => {
                    let frame = crate::modbus_ascii::build_ascii_frame(station_id, request_pdu);
                    socket
                        .send_to(&frame, *peer_addr)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    let mut buf = [0u8; 1024];
                    socket
                        .recv_from(&mut buf)
                        .map_err(|e| connection_io_error(id, &e.to_string()))?;
                    Ok(())
                }
            },
            _ => Err(connection_not_found(id)),
        }
    }

    /// 列出所有活跃连接的 ID。
    pub fn connection_ids(&self) -> Vec<String> {
        let mut ids: Vec<String> = self.connections.keys().cloned().collect();
        ids.extend(self.ge_srtp_connections.keys().cloned());
        ids.extend(self.fuji_sph_connections.keys().cloned());
        ids.extend(self.fatek_connections.keys().cloned());
        ids.extend(self.keyence_connections.keys().cloned());
        ids.extend(self.ls_xgt_connections.keys().cloned());
        ids.extend(self.ads_connections.keys().cloned());
        ids.extend(self.enip_connections.keys().cloned());
        ids.extend(self.mqtt_connections.keys().cloned());
        ids.extend(self.iec104_connections.keys().cloned());
        ids.extend(self.dnp3_connections.keys().cloned());
        ids.extend(self.bacnet_connections.keys().cloned());
        ids.extend(self.knx_connections.keys().cloned());
        ids
    }

    // === 从站管理 ===

    /// 启动一个 TCP 从站服务器。阻塞 false:在独立线程中运行。
    pub fn start_tcp_slave(
        &mut self,
        slave_id: &str,
        port: u16,
        allowed_station_ids: Vec<u8>,
    ) -> Result<(), CoreError> {
        if self.slaves.contains_key(slave_id) {
            return Err(CoreError::Modbus {
                code: "SLAVE_ALREADY_RUNNING",
                message: format!("从站 {slave_id} 已在运行"),
                details: None,
            });
        }
        let memory = Arc::new(Mutex::new(SlaveMemory::default()));
        let running = Arc::new(Mutex::new(true));
        // 先在当前线程 bind 端口(立即检测端口冲突),成功后移交到后台线程
        let listener = std::net::TcpListener::bind(format!("127.0.0.1:{port}")).map_err(|e| {
            CoreError::Modbus {
                code: "SLAVE_BIND_FAILED",
                message: format!("从站绑定端口 {port} 失败:{e}"),
                details: Some(serde_json::json!({ "port": port, "error": e.to_string() })),
            }
        })?;
        let _ = listener.set_nonblocking(true);
        let mem_clone = Arc::clone(&memory);
        let run_flag = Arc::clone(&running);
        std::thread::spawn(move || {
            while *run_flag.lock().unwrap_or_else(|e| e.into_inner()) {
                match listener.accept() {
                    Ok((stream, _)) => {
                        let mem = Arc::clone(&mem_clone);
                        let allow = allowed_station_ids.clone();
                        let rf = Arc::clone(&run_flag);
                        std::thread::spawn(move || {
                            handle_slave_client(stream, mem, allow, rf);
                        });
                    }
                    Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                        std::thread::sleep(std::time::Duration::from_millis(10));
                    }
                    Err(_) => break,
                }
            }
        });
        self.slaves.insert(slave_id.to_string(), (memory, running));
        Ok(())
    }

    /// 停止从站。
    pub fn stop_slave(&mut self, slave_id: &str) -> Result<(), CoreError> {
        match self.slaves.remove(slave_id) {
            Some((_mem, running)) => {
                *running.lock().unwrap_or_else(|e| e.into_inner()) = false;
                Ok(())
            }
            None => Err(CoreError::Modbus {
                code: "SLAVE_NOT_FOUND",
                message: format!("从站 {slave_id} 不存在"),
                details: None,
            }),
        }
    }

    // === 三菱 MC 虚拟从站 ===

    /// 启动 MC TCP 虚拟从站(3E/4E 帧)。
    /// S7 CPU 控制:"stop"/"hot"/"cold";返回控制结果码。
    pub fn s7_cpu_control(&mut self, id: &str, action: &str) -> Result<(u8, String), CoreError> {
        let pdu_ref = self.s7_pdu_ref(id)?;
        let pdu = match action {
            "stop" => crate::s7_pdu::build_stop_job(pdu_ref),
            "hot" => crate::s7_pdu::build_start_job(pdu_ref, true),
            "cold" => crate::s7_pdu::build_start_job(pdu_ref, false),
            other => {
                return Err(CoreError::Modbus {
                    code: "S7_ACTION_INVALID",
                    message: format!("未知控制动作「{other}」(stop/hot/cold)"),
                    details: None,
                });
            }
        };
        let ack = self.s7_transact(id, pdu)?;
        let code = crate::s7_pdu::parse_control_response(&ack)?;
        Ok((
            code,
            crate::s7_pdu::control_result_message(code).to_string(),
        ))
    }

    /// S7 CPU 状态(SZL 0x0424)→ "RUN"/"STOP"/...
    pub fn s7_read_status(&mut self, id: &str) -> Result<String, CoreError> {
        let pdu_ref = self.s7_pdu_ref(id)?;
        let pdu = crate::s7_pdu::build_szl_request(pdu_ref, 0x0424, 0);
        let ack = self.s7_transact(id, pdu)?;
        let payload = crate::s7_pdu::parse_szl_response(&ack)?;
        Ok(crate::s7_pdu::szl_0424_mode(&payload).to_string())
    }

    /// S7 密码登录(S7-300/400)。
    pub fn s7_password(&mut self, id: &str, password: &str) -> Result<(), CoreError> {
        let pdu_ref = self.s7_pdu_ref(id)?;
        let pdu = crate::s7_pdu::build_password_job(pdu_ref, password);
        self.s7_transact(id, pdu)?;
        Ok(())
    }

    fn s7_pdu_ref(&self, id: &str) -> Result<u16, CoreError> {
        match self.connections.get(id) {
            Some(Connection::S7Tcp { pdu_ref, .. }) => Ok(*pdu_ref),
            _ => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 S7 连接"),
                details: None,
            }),
        }
    }

    // ============ PPI(S7-200,OverTcp 透传形态) ============

    pub fn open_ppi_tcp(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        station: u8,
    ) -> Result<(), CoreError> {
        let addr = format!("{host}:{port}");
        let stream = TcpStream::connect_timeout(
            &addr
                .parse()
                .map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| CoreError::Modbus {
            code: "S7_PPI_CONNECT_FAILED",
            message: format!("无法连接 {addr}(PPI over TCP/串口服务器透传)。{e}"),
            details: None,
        })?;
        stream.set_read_timeout(Some(TCP_READ_TIMEOUT)).ok();
        stream.set_write_timeout(Some(TCP_READ_TIMEOUT)).ok();
        self.connections.insert(
            id.to_string(),
            Connection::PpiTcp {
                stream,
                station,
                master: 0,
            },
        );
        Ok(())
    }

    /// PPI 双拍事务:SD2 请求 → E5 → 短帧确认 → SD2 数据帧(返回内嵌 S7 Ack)。
    fn ppi_transact(
        &mut self,
        id: &str,
        fc: u8,
        s7_pdu: &[u8],
    ) -> Result<crate::s7_pdu::S7Ack, CoreError> {
        let conn = self
            .connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let Connection::PpiTcp {
            stream,
            station,
            master,
        } = conn
        else {
            return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 PPI 连接"),
                details: None,
            });
        };
        let frame = crate::ppi_frame::build_sd2(*station, *master, fc, s7_pdu);
        stream
            .write_all(&frame)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        // ① 期待 E5(读尽直到 E5;容忍串口服务器粘连)
        let mut one = [0u8; 1];
        loop {
            stream
                .read_exact(&mut one)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            if one[0] == crate::ppi_frame::SC_E5 {
                break;
            }
        }
        // ② 发短帧确认
        let confirm = crate::ppi_frame::build_sa_confirm(*station, *master);
        stream
            .write_all(&confirm)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        // ③ 读 SD2 响应(按 LE 定长)
        let mut head = [0u8; 4];
        stream
            .read_exact(&mut head)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        if head[0] != 0x68 {
            return Err(CoreError::Modbus {
                code: "S7_PPI_INVALID",
                message: format!("PPI 响应起始字节 0x{:02X}(期望 0x68)", head[0]),
                details: None,
            });
        }
        let le = head[1] as usize;
        let mut rest = vec![0u8; le + 2];
        stream
            .read_exact(&mut rest)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let mut full = head.to_vec();
        full.extend_from_slice(&rest);
        let (_da, _sa, _fc, resp_pdu) = crate::ppi_frame::parse_sd2(&full)?;
        crate::s7_pdu::parse_ack(&resp_pdu)
    }

    pub fn ppi_read(
        &mut self,
        id: &str,
        address: &str,
        count: u16,
    ) -> Result<Vec<crate::s7_pdu::ReadItemData>, CoreError> {
        let items = [crate::s7_pdu::S7Item::new(address, count)?];
        let pdu = crate::s7_pdu::build_read_request(0, &items)?;
        let ack = self.ppi_transact(id, crate::ppi_frame::FC_READ, &pdu)?;
        if ack.error != 0 {
            return Err(CoreError::Modbus {
                code: "S7_CPU_ERROR",
                message: format!(
                    "PPI 响应错误 0x{:04X}:{}",
                    ack.error,
                    crate::s7_pdu::header_error_message(ack.error)
                ),
                details: None,
            });
        }
        crate::s7_pdu::parse_read_response(&ack)
    }

    pub fn ppi_write(
        &mut self,
        id: &str,
        address: &str,
        count: u16,
        data: &[u8],
    ) -> Result<Vec<u8>, CoreError> {
        let items = [crate::s7_pdu::S7Item::new(address, count)?];
        let pdu = crate::s7_pdu::build_write_request(0, &items, &[data.to_vec()])?;
        let ack = self.ppi_transact(id, crate::ppi_frame::FC_WRITE, &pdu)?;
        if ack.error != 0 {
            return Err(CoreError::Modbus {
                code: "S7_CPU_ERROR",
                message: format!(
                    "PPI 响应错误 0x{:04X}:{}",
                    ack.error,
                    crate::s7_pdu::header_error_message(ack.error)
                ),
                details: None,
            });
        }
        crate::s7_pdu::parse_write_response(&ack)
    }

    pub fn start_ppi_slave(
        &mut self,
        slave_id: &str,
        port: u16,
        seed: bool,
    ) -> Result<(), CoreError> {
        if self.ppi_slaves.contains_key(slave_id) {
            return Err(CoreError::Modbus {
                code: "S7_PPI_SLAVE_ALREADY_RUNNING",
                message: format!("PPI 从站 {slave_id} 已在运行"),
                details: None,
            });
        }
        let mut memory = crate::s7_slave::S7SlaveMemory::new();
        if seed {
            crate::s7_slave::seed_demo(&mut memory);
        }
        let memory = Arc::new(Mutex::new(memory));
        let running = Arc::new(Mutex::new(true));
        let listener = std::net::TcpListener::bind(format!("127.0.0.1:{port}")).map_err(|e| {
            CoreError::Modbus {
                code: "S7_PPI_SLAVE_BIND_FAILED",
                message: format!("PPI 从站绑定端口 {port} 失败:{e}"),
                details: None,
            }
        })?;
        let mem = Arc::clone(&memory);
        let rf = Arc::clone(&running);
        std::thread::spawn(move || crate::ppi_slave::ppi_accept_loop(listener, mem, rf));
        self.ppi_slaves
            .insert(slave_id.to_string(), (memory, running));
        Ok(())
    }

    pub fn stop_ppi_slave(&mut self, slave_id: &str) -> Result<(), CoreError> {
        match self.ppi_slaves.remove(slave_id) {
            Some((_m, running)) => {
                *running.lock().unwrap_or_else(|e| e.into_inner()) = false;
                Ok(())
            }
            None => Err(CoreError::Modbus {
                code: "S7_PPI_SLAVE_NOT_FOUND",
                message: format!("PPI 从站 {slave_id} 不存在"),
                details: None,
            }),
        }
    }

    // ============ Fetch/Write(S5 兼容) ============

    pub fn open_fw_tcp(&mut self, id: &str, host: &str, port: u16) -> Result<(), CoreError> {
        let addr = format!("{host}:{port}");
        let stream = TcpStream::connect_timeout(
            &addr.parse().map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| CoreError::Modbus {
            code: "S7_FW_CONNECT_FAILED",
            message: format!(
                "无法连接 {addr}。Fetch/Write 是 CP 上需在 NetPro 里开启的被动服务(FETCH/WRITE PASSIVE),裸 TCP 走用户端口(常见 2000),不是 102。{e}"
            ),
            details: None,
        })?;
        stream.set_read_timeout(Some(TCP_READ_TIMEOUT)).ok();
        stream.set_write_timeout(Some(TCP_READ_TIMEOUT)).ok();
        self.connections
            .insert(id.to_string(), Connection::FwTcp { stream });
        Ok(())
    }

    pub fn fw_read(
        &mut self,
        id: &str,
        org: u8,
        db: u8,
        address: u16,
        length: u16,
    ) -> Result<Vec<u8>, CoreError> {
        let resp = self.fw_transact(
            id,
            crate::s7_fetchwrite::build_fetch(org, db, address, length),
            length as usize,
        )?;
        let (opc, err, data) = crate::s7_fetchwrite::parse_response(&resp)?;
        if opc != crate::s7_fetchwrite::OPC_FETCH_RESP {
            return Err(CoreError::Modbus {
                code: "S7_FW_RESPONSE_MISMATCH",
                message: format!("Fetch 读取收到错误响应 OPC 0x{opc:02X}"),
                details: None,
            });
        }
        if err != 0 {
            return Err(CoreError::Modbus {
                code: "S7_FW_ERROR",
                message: format!("Fetch/Write 错误号 0x{err:02X}"),
                details: None,
            });
        }
        if data.len() != length as usize {
            return Err(CoreError::Modbus {
                code: "S7_FW_LENGTH_MISMATCH",
                message: format!("Fetch 返回 {} 字节，期望 {} 字节", data.len(), length),
                details: None,
            });
        }
        Ok(data)
    }

    pub fn fw_write(
        &mut self,
        id: &str,
        org: u8,
        db: u8,
        address: u16,
        data: &[u8],
    ) -> Result<(), CoreError> {
        let expect = 0usize;
        let frame = crate::s7_fetchwrite::build_write(org, db, address, data);
        let resp = self.fw_transact(id, frame, expect)?;
        let (opc, err, data) = crate::s7_fetchwrite::parse_response(&resp)?;
        if opc != crate::s7_fetchwrite::OPC_WRITE_RESP || !data.is_empty() {
            return Err(CoreError::Modbus {
                code: "S7_FW_RESPONSE_MISMATCH",
                message: format!("Write 收到错误响应 OPC 0x{opc:02X} 或非空数据体"),
                details: None,
            });
        }
        if err != 0 {
            return Err(CoreError::Modbus {
                code: "S7_FW_ERROR",
                message: format!("Fetch/Write 错误号 0x{err:02X}"),
                details: None,
            });
        }
        Ok(())
    }

    fn fw_transact(
        &mut self,
        id: &str,
        frame: Vec<u8>,
        expect_data: usize,
    ) -> Result<Vec<u8>, CoreError> {
        let conn = self
            .connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let Connection::FwTcp { stream } = conn else {
            return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 Fetch/Write 连接"),
                details: None,
            });
        };
        stream
            .write_all(&frame)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        crate::s7_fetchwrite::read_fw_response(&mut &*stream, expect_data)
    }

    pub fn start_fw_slave(
        &mut self,
        slave_id: &str,
        port: u16,
        seed: bool,
    ) -> Result<(), CoreError> {
        if self.fw_slaves.contains_key(slave_id) {
            return Err(CoreError::Modbus {
                code: "S7_FW_SLAVE_ALREADY_RUNNING",
                message: format!("FW 从站 {slave_id} 已在运行"),
                details: None,
            });
        }
        let mut memory = crate::s7_fetchwrite::FwMemory::new();
        if seed {
            crate::s7_fetchwrite::seed_fw(&mut memory);
        }
        let memory = Arc::new(Mutex::new(memory));
        let running = Arc::new(Mutex::new(true));
        let listener = std::net::TcpListener::bind(format!("127.0.0.1:{port}")).map_err(|e| {
            CoreError::Modbus {
                code: "S7_FW_SLAVE_BIND_FAILED",
                message: format!("FW 从站绑定端口 {port} 失败:{e}"),
                details: None,
            }
        })?;
        let mem = Arc::clone(&memory);
        let rf = Arc::clone(&running);
        std::thread::spawn(move || crate::s7_fetchwrite::fw_accept_loop(listener, mem, rf));
        self.fw_slaves
            .insert(slave_id.to_string(), (memory, running));
        Ok(())
    }

    pub fn stop_fw_slave(&mut self, slave_id: &str) -> Result<(), CoreError> {
        match self.fw_slaves.remove(slave_id) {
            Some((_m, running)) => {
                *running.lock().unwrap_or_else(|e| e.into_inner()) = false;
                Ok(())
            }
            None => Err(CoreError::Modbus {
                code: "S7_FW_SLAVE_NOT_FOUND",
                message: format!("FW 从站 {slave_id} 不存在"),
                details: None,
            }),
        }
    }

    // ============ 欧姆龙 FINS ============

    /// 打开 FINS/TCP 连接:TCP → FINS/TCP 握手(节点协商)。
    pub fn open_fins_tcp(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        nodes: crate::fins_frame::FinsNodes,
    ) -> Result<u16, CoreError> {
        let addr = format!("{host}:{port}");
        let mut stream = TcpStream::connect_timeout(
            &addr.parse().map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| CoreError::Modbus {
            code: "FINS_CONNECT_FAILED",
            message: format!("无法连接 {addr}:9600(FINS/TCP)。检查:① PLC 以太网口与 IP ② 欧姆龙 CPU 的 FINS/TCP 功能。{e}"),
            details: None,
        })?;
        stream.set_read_timeout(Some(TCP_READ_TIMEOUT)).ok();
        stream.set_write_timeout(Some(TCP_READ_TIMEOUT)).ok();
        // 握手:client_node 用源节点号
        stream
            .write_all(&crate::fins_frame::build_tcp_handshake(nodes.sa1 as u16))
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let payload = crate::fins_frame::read_tcp_frame(&mut &stream)?;
        // 响应:cmd=1,err=0,server_node(2),client_node(2)
        if payload.len() < 12 || payload[..4] != [0, 0, 0, 1] {
            return Err(CoreError::Modbus {
                code: "FINS_HANDSHAKE_FAILED",
                message: "FINS/TCP 握手响应无效(CPU 拒绝?检查 FINS 节点号设置)".to_string(),
                details: None,
            });
        }
        let err_code = u32::from_be_bytes([payload[4], payload[5], payload[6], payload[7]]);
        if err_code != 0 {
            return Err(CoreError::Modbus {
                code: "FINS_HANDSHAKE_FAILED",
                message: format!("FINS/TCP 握手错误码 0x{err_code:08X}"),
                details: None,
            });
        }
        self.connections.insert(
            id.to_string(),
            Connection::FinsTcp {
                stream,
                nodes,
                sid: 0,
            },
        );
        Ok(0)
    }

    /// 打开 FINS/UDP 连接。
    pub fn open_fins_udp(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        nodes: crate::fins_frame::FinsNodes,
    ) -> Result<(), CoreError> {
        let socket = UdpSocket::bind("0.0.0.0:0").map_err(|e| CoreError::Modbus {
            code: "FINS_CONNECT_FAILED",
            message: format!("UDP 绑定失败:{e}"),
            details: None,
        })?;
        socket.set_read_timeout(Some(TCP_READ_TIMEOUT)).ok();
        let peer_addr: std::net::SocketAddr = format!("{host}:{port}")
            .parse()
            .map_err(|_| connection_failed(&format!("{host}:{port}"), "地址解析失败"))?;
        self.connections.insert(
            id.to_string(),
            Connection::FinsUdp {
                socket,
                peer_addr,
                nodes,
                sid: 0,
            },
        );
        Ok(())
    }

    fn fins_transact(&mut self, id: &str, app: Vec<u8>) -> Result<Vec<u8>, CoreError> {
        let mismatch = || CoreError::Modbus {
            code: "CONNECTION_TYPE_MISMATCH",
            message: format!("连接 {id} 不是 FINS 连接"),
            details: None,
        };
        let conn = self
            .connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        match conn {
            Connection::FinsTcp { stream, sid, .. } => {
                let frame = crate::fins_frame::wrap_tcp(&app);
                stream
                    .write_all(&frame)
                    .and_then(|_| stream.flush())
                    .map_err(|e| connection_io_error(id, &e.to_string()))?;
                let payload = crate::fins_frame::read_tcp_frame(&mut &*stream)?;
                *sid = sid.wrapping_add(1);
                if payload.len() < 8 {
                    return Err(CoreError::Modbus {
                        code: "FINS_RESPONSE_INVALID",
                        message: "FINS/TCP 响应过短".to_string(),
                        details: None,
                    });
                }
                Ok(payload[8..].to_vec())
            }
            Connection::FinsUdp {
                socket,
                peer_addr,
                sid,
                ..
            } => {
                socket
                    .send_to(&app, *peer_addr)
                    .map_err(|e| connection_io_error(id, &e.to_string()))?;
                let mut buf = [0u8; 2048];
                let (n, _) = socket
                    .recv_from(&mut buf)
                    .map_err(|e| connection_io_error(id, &e.to_string()))?;
                *sid = sid.wrapping_add(1);
                Ok(buf[..n].to_vec())
            }
            _ => Err(mismatch()),
        }
    }

    /// FINS 读:返回(结束码, 数据字节)。
    pub fn fins_read(
        &mut self,
        id: &str,
        address: &str,
        count: u16,
    ) -> Result<(u16, Vec<u8>), CoreError> {
        let addr = crate::fins_address::parse_fins_address(address)?;
        crate::fins_frame::validate_access_window(&addr, count)?;
        let (nodes, sid) = match self.connections.get(id) {
            Some(Connection::FinsTcp { nodes, sid, .. })
            | Some(Connection::FinsUdp { nodes, sid, .. }) => (nodes.clone(), *sid),
            _ => {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 不是 FINS 连接"),
                    details: None,
                });
            }
        };
        let frame = crate::fins_frame::build_read_frame(&nodes, sid, &addr, count);
        let resp = self.fins_transact(id, frame)?;
        let parsed = crate::fins_frame::parse_response_frame(&resp)?;
        if parsed.sid != sid {
            return Err(CoreError::Modbus {
                code: "FINS_SID_MISMATCH",
                message: format!("FINS 响应 SID 不匹配:期望 {},收到 {}", sid, parsed.sid),
                details: Some(serde_json::json!({ "expectedSid": sid, "actualSid": parsed.sid })),
            });
        }
        if parsed.end_code == 0 {
            let expected = crate::fins_frame::expected_data_bytes(&addr, count);
            if parsed.data.len() != expected {
                return Err(CoreError::Modbus {
                    code: "FINS_RESPONSE_LENGTH_MISMATCH",
                    message: format!(
                        "FINS 响应数据长度不匹配:期望 {}B,收到 {}B",
                        expected,
                        parsed.data.len()
                    ),
                    details: Some(
                        serde_json::json!({ "expectedBytes": expected, "actualBytes": parsed.data.len(), "count": count }),
                    ),
                });
            }
        }
        Ok((parsed.end_code, parsed.data))
    }

    /// FINS 写。
    pub fn fins_write(
        &mut self,
        id: &str,
        address: &str,
        count: u16,
        data: &[u8],
    ) -> Result<u16, CoreError> {
        let addr = crate::fins_address::parse_fins_address(address)?;
        crate::fins_frame::validate_access_window(&addr, count)?;
        let expected = crate::fins_frame::expected_data_bytes(&addr, count);
        if data.len() != expected {
            return Err(CoreError::Modbus {
                code: "FINS_DATA_LENGTH_INVALID",
                message: format!(
                    "FINS 写入数据长度不匹配:期望 {}B,收到 {}B",
                    expected,
                    data.len()
                ),
                details: Some(
                    serde_json::json!({ "expectedBytes": expected, "actualBytes": data.len(), "count": count }),
                ),
            });
        }
        let (nodes, sid) = match self.connections.get(id) {
            Some(Connection::FinsTcp { nodes, sid, .. })
            | Some(Connection::FinsUdp { nodes, sid, .. }) => (nodes.clone(), *sid),
            _ => {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 不是 FINS 连接"),
                    details: None,
                });
            }
        };
        let frame = crate::fins_frame::build_write_frame(&nodes, sid, &addr, count, data);
        let resp = self.fins_transact(id, frame)?;
        let parsed = crate::fins_frame::parse_response_frame(&resp)?;
        if parsed.sid != sid {
            return Err(CoreError::Modbus {
                code: "FINS_SID_MISMATCH",
                message: format!("FINS 响应 SID 不匹配:期望 {},收到 {}", sid, parsed.sid),
                details: Some(serde_json::json!({ "expectedSid": sid, "actualSid": parsed.sid })),
            });
        }
        Ok(parsed.end_code)
    }

    /// 启动 FINS 虚拟从站(TCP + UDP 同端口)。
    pub fn start_fins_slave(
        &mut self,
        slave_id: &str,
        port: u16,
        seed: bool,
    ) -> Result<(), CoreError> {
        if self.fins_slaves.contains_key(slave_id) {
            return Err(CoreError::Modbus {
                code: "FINS_SLAVE_ALREADY_RUNNING",
                message: format!("FINS 从站 {slave_id} 已在运行"),
                details: None,
            });
        }
        let mut memory = crate::fins_slave::FinsMemory::new();
        if seed {
            crate::fins_slave::seed_demo(&mut memory);
        }
        let memory = Arc::new(Mutex::new(memory));
        let running = Arc::new(Mutex::new(true));
        let listener = std::net::TcpListener::bind(format!("127.0.0.1:{port}")).map_err(|e| {
            CoreError::Modbus {
                code: "FINS_SLAVE_BIND_FAILED",
                message: format!("FINS 从站绑定端口 {port} 失败:{e}"),
                details: None,
            }
        })?;
        let mem_tcp = Arc::clone(&memory);
        let rf_tcp = Arc::clone(&running);
        std::thread::spawn(move || {
            crate::fins_slave::fins_tcp_accept_loop(listener, mem_tcp, rf_tcp)
        });
        let sock = match std::net::UdpSocket::bind(format!("127.0.0.1:{port}")) {
            Ok(s) => s,
            Err(e) => {
                // #13: UDP 绑定失败时停掉已启动的 TCP 监听线程(防泄漏)
                *running.lock().unwrap_or_else(|er| er.into_inner()) = false;
                return Err(CoreError::Modbus {
                    code: "FINS_SLAVE_BIND_FAILED",
                    message: format!("FINS UDP 绑定端口 {port} 失败:{e}"),
                    details: None,
                });
            }
        };
        let mem_u = Arc::clone(&memory);
        let rf_u = Arc::clone(&running);
        std::thread::spawn(move || crate::fins_slave::fins_udp_loop(sock, mem_u, rf_u));
        self.fins_slaves
            .insert(slave_id.to_string(), (memory, running));
        Ok(())
    }

    pub fn stop_fins_slave(&mut self, slave_id: &str) -> Result<(), CoreError> {
        match self.fins_slaves.remove(slave_id) {
            Some((_m, running)) => {
                *running.lock().unwrap_or_else(|e| e.into_inner()) = false;
                Ok(())
            }
            None => Err(CoreError::Modbus {
                code: "FINS_SLAVE_NOT_FOUND",
                message: format!("FINS 从站 {slave_id} 不存在"),
                details: None,
            }),
        }
    }

    /// FINS 从站内存直写(u16 列表,按地址字偏移)。
    pub fn fins_slave_set(
        &mut self,
        slave_id: &str,
        address: &str,
        values: &[u16],
    ) -> Result<(), CoreError> {
        let (memory, _) = self
            .fins_slaves
            .get(slave_id)
            .ok_or_else(|| CoreError::Modbus {
                code: "FINS_SLAVE_NOT_FOUND",
                message: format!("FINS 从站 {slave_id} 不存在"),
                details: None,
            })?;
        let addr = crate::fins_address::parse_fins_address(address)?;
        let mut m = memory.lock().unwrap_or_else(|e| e.into_inner());
        crate::fins_slave::memory_write(&mut m, addr.area_code, addr.address as usize, values)
            .ok_or_else(|| CoreError::Modbus {
                code: "FINS_SLAVE_WRITE_FAILED",
                message: format!("地址 {address} 写入失败(越界?)"),
                details: None,
            })
    }

    pub fn fins_slave_get(
        &self,
        slave_id: &str,
        address: &str,
        count: u16,
    ) -> Result<Vec<u16>, CoreError> {
        let (memory, _) = self
            .fins_slaves
            .get(slave_id)
            .ok_or_else(|| CoreError::Modbus {
                code: "FINS_SLAVE_NOT_FOUND",
                message: format!("FINS 从站 {slave_id} 不存在"),
                details: None,
            })?;
        let addr = crate::fins_address::parse_fins_address(address)?;
        let m = memory.lock().unwrap_or_else(|e| e.into_inner());
        crate::fins_slave::memory_read(&m, addr.area_code, addr.address as usize, count as usize)
            .ok_or_else(|| CoreError::Modbus {
                code: "FINS_SLAVE_READ_FAILED",
                message: format!("地址 {address} 读取失败(越界?)"),
                details: None,
            })
    }

    // ============ 西门子 S7comm ============

    /// 打开 S7 连接:TCP → COTP CR/CC → Setup Communication(PDU 协商)。
    ///
    /// `conn_type`:1=PG(默认,权限最高) 2=OP 3=S7 Basic;
    /// `local_tsap`/`remote_tsap` 提供时直接覆盖公式计算值(十六进制字符串,如 "0100")。
    pub fn open_s7_connection(
        &mut self,
        id: &str,
        host: &str,
        port: u16,
        rack: u8,
        slot: u8,
        conn_type: u8,
        local_tsap: Option<u16>,
        remote_tsap: Option<u16>,
        pdu_request: u16,
    ) -> Result<u16, CoreError> {
        let addr = format!("{host}:{port}");
        let mut stream = TcpStream::connect_timeout(
            &addr
                .parse()
                .map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| {
            CoreError::Modbus {
                code: "S7_CONNECT_FAILED",
                message: format!(
                    "无法连接 {addr}(TCP)。检查:① IP/子网掩码是否同网段 ② 网线与 LINK 灯 ③ S7 端口应为 102。{e}"
                ),
                details: None,
            }
        })?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;

        let ctype = match conn_type {
            2 => crate::s7_cotp::ConnectionType::Op,
            3 => crate::s7_cotp::ConnectionType::Basic,
            _ => crate::s7_cotp::ConnectionType::Pg,
        };
        let local = local_tsap.unwrap_or(0x0100);
        let remote = remote_tsap.unwrap_or_else(|| crate::s7_cotp::remote_tsap(ctype, rack, slot));

        // CR → CC
        let cr = crate::s7_cotp::build_cr(local, remote, 1024);
        stream
            .write_all(&cr)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let cc_frame = crate::s7_cotp::read_tpkt_frame(&mut &stream)?;
        let cc_cotp = crate::s7_cotp::unwrap_tpkt(&cc_frame)?;
        crate::s7_cotp::parse_cc(cc_cotp)?;

        // Setup Communication
        let pdu_req = if pdu_request == 0 {
            crate::s7_pdu::DEFAULT_PDU_REQUEST
        } else {
            pdu_request
        };
        let setup = crate::s7_pdu::build_setup_request(0x0001, 1, 1, pdu_req);
        let setup_frame = crate::s7_cotp::wrap_dt(&setup);
        stream
            .write_all(&setup_frame)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let resp = crate::s7_cotp::read_tpkt_frame(&mut &stream)?;
        let resp_pdu = crate::s7_cotp::frame_to_pdu(&resp)?;
        let ack = crate::s7_pdu::parse_ack(resp_pdu)?;
        if ack.rosctr != crate::s7_pdu::ROSCTR_ACK_DATA {
            return Err(CoreError::Modbus {
                code: "S7_HANDSHAKE_FAILED",
                message: format!("Setup 响应 ROSCTR=0x{:02X}(期望 Ack_Data 0x03)", ack.rosctr),
                details: None,
            });
        }
        if ack.error != 0 {
            return Err(CoreError::Modbus {
                code: "S7_CPU_ERROR",
                message: format!(
                    "Setup 协商失败 0x{:04X}:{}",
                    ack.error,
                    crate::s7_pdu::header_error_message(ack.error)
                ),
                details: None,
            });
        }
        let (_amq1, _amq2, pdu_size) = crate::s7_pdu::parse_setup_response(&ack)?;
        self.connections.insert(
            id.to_string(),
            Connection::S7Tcp {
                stream,
                pdu_size,
                pdu_ref: 1,
            },
        );
        Ok(pdu_size)
    }

    /// 查询 S7 连接的协商 PDU 长度(分片预算)。
    pub fn s7_pdu_size(&self, id: &str) -> Result<u16, CoreError> {
        match self.connections.get(id) {
            Some(Connection::S7Tcp { pdu_size, .. }) => Ok(*pdu_size),
            _ => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 S7 连接"),
                details: None,
            }),
        }
    }

    fn s7_transact(&mut self, id: &str, pdu: Vec<u8>) -> Result<crate::s7_pdu::S7Ack, CoreError> {
        let mismatch = || CoreError::Modbus {
            code: "CONNECTION_TYPE_MISMATCH",
            message: format!("连接 {id} 不是 S7 连接"),
            details: None,
        };
        let conn = self
            .connections
            .get_mut(id)
            .ok_or_else(|| connection_not_found(id))?;
        let Connection::S7Tcp {
            stream, pdu_ref, ..
        } = conn
        else {
            return Err(mismatch());
        };
        let frame = crate::s7_cotp::wrap_dt(&pdu);
        stream
            .write_all(&frame)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let resp = crate::s7_cotp::read_tpkt_frame(&mut &*stream)?;
        *pdu_ref = pdu_ref.wrapping_add(1);
        let resp_pdu = crate::s7_cotp::frame_to_pdu(&resp)?;
        let ack = crate::s7_pdu::parse_ack(resp_pdu)?;
        // PDU Reference 回显校验(snap7 行为)
        if ack.pdu_ref != u16::from_be_bytes([pdu[4], pdu[5]]) {
            return Err(CoreError::Modbus {
                code: "S7_RESPONSE_MISMATCH",
                message: format!(
                    "响应 PDU Ref {} 与请求 {} 不配对(可能超时后队列错位,建议重连)",
                    ack.pdu_ref,
                    u16::from_be_bytes([pdu[4], pdu[5]])
                ),
                details: None,
            });
        }
        if ack.error != 0 {
            return Err(CoreError::Modbus {
                code: "S7_CPU_ERROR",
                message: format!(
                    "CPU 返回错误 0x{:04X}:{}",
                    ack.error,
                    crate::s7_pdu::header_error_message(ack.error)
                ),
                details: Some(
                    serde_json::json!({ "errorClass": (ack.error >> 8) & 0xFF, "errorCode": ack.error & 0xFF }),
                ),
            });
        }
        Ok(ack)
    }

    /// S7 读(单轮,items ≤ 20 且各项须在 PDU 预算内;分片由协议层负责)。
    pub fn s7_read(
        &mut self,
        id: &str,
        items: &[crate::s7_pdu::S7Item],
    ) -> Result<Vec<crate::s7_pdu::ReadItemData>, CoreError> {
        let pdu_ref = match self.connections.get(id) {
            Some(Connection::S7Tcp { pdu_ref, .. }) => *pdu_ref,
            _ => {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 不是 S7 连接"),
                    details: None,
                });
            }
        };
        let pdu = crate::s7_pdu::build_read_request(pdu_ref, items)?;
        let ack = self.s7_transact(id, pdu)?;
        crate::s7_pdu::parse_read_response(&ack)
    }

    /// S7 写(单轮,items ≤ 20;data_blocks 与 items 一一对应)。
    pub fn s7_write(
        &mut self,
        id: &str,
        items: &[crate::s7_pdu::S7Item],
        data_blocks: &[Vec<u8>],
    ) -> Result<Vec<u8>, CoreError> {
        let pdu_ref = match self.connections.get(id) {
            Some(Connection::S7Tcp { pdu_ref, .. }) => *pdu_ref,
            _ => {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 不是 S7 连接"),
                    details: None,
                });
            }
        };
        let pdu = crate::s7_pdu::build_write_request(pdu_ref, items, data_blocks)?;
        let ack = self.s7_transact(id, pdu)?;
        crate::s7_pdu::parse_write_response(&ack)
    }

    /// 启动西门子 S7 虚拟从站(TCP 102 行为模拟)。
    pub fn start_s7_slave(
        &mut self,
        slave_id: &str,
        port: u16,
        seed: bool,
    ) -> Result<(), CoreError> {
        if self.s7_slaves.contains_key(slave_id) {
            return Err(CoreError::Modbus {
                code: "S7_SLAVE_ALREADY_RUNNING",
                message: format!("S7 从站 {slave_id} 已在运行"),
                details: None,
            });
        }
        let mut memory = crate::s7_slave::S7SlaveMemory::new();
        if seed {
            crate::s7_slave::seed_demo(&mut memory);
        }
        let memory = Arc::new(Mutex::new(memory));
        let running = Arc::new(Mutex::new(true));
        let listener = std::net::TcpListener::bind(format!("127.0.0.1:{port}")).map_err(|e| {
            CoreError::Modbus {
                code: "S7_SLAVE_BIND_FAILED",
                message: format!("S7 从站绑定端口 {port} 失败:{e}"),
                details: Some(serde_json::json!({ "port": port, "error": e.to_string() })),
            }
        })?;
        let _ = listener.set_nonblocking(true);
        let mem_clone = Arc::clone(&memory);
        let run_flag = Arc::clone(&running);
        std::thread::spawn(move || {
            while *run_flag.lock().unwrap_or_else(|e| e.into_inner()) {
                match listener.accept() {
                    Ok((stream, _)) => {
                        let mem = Arc::clone(&mem_clone);
                        let rf = Arc::clone(&run_flag);
                        std::thread::spawn(move || {
                            crate::s7_slave::handle_s7_client(stream, mem, rf);
                        });
                    }
                    Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                        std::thread::sleep(std::time::Duration::from_millis(10));
                    }
                    Err(_) => break,
                }
            }
        });
        self.s7_slaves
            .insert(slave_id.to_string(), (memory, running));
        Ok(())
    }

    /// 停止 S7 虚拟从站。
    pub fn stop_s7_slave(&mut self, slave_id: &str) -> Result<(), CoreError> {
        match self.s7_slaves.remove(slave_id) {
            Some((_mem, running)) => {
                *running.lock().unwrap_or_else(|e| e.into_inner()) = false;
                Ok(())
            }
            None => Err(CoreError::Modbus {
                code: "S7_SLAVE_NOT_FOUND",
                message: format!("S7 从站 {slave_id} 不存在"),
                details: None,
            }),
        }
    }

    fn s7_slave_memory(
        &self,
        slave_id: &str,
    ) -> Result<Arc<Mutex<crate::s7_slave::S7SlaveMemory>>, CoreError> {
        let (memory, _) = self
            .s7_slaves
            .get(slave_id)
            .ok_or_else(|| CoreError::Modbus {
                code: "S7_SLAVE_NOT_FOUND",
                message: format!("S7 从站 {slave_id} 不存在"),
                details: None,
            })?;
        Ok(Arc::clone(memory))
    }

    /// S7 从站内存写(按地址语法,字节序列;位地址时每字节=1 个位的值)。
    pub fn s7_slave_set(
        &mut self,
        slave_id: &str,
        address: &str,
        bytes: &[u8],
    ) -> Result<(), CoreError> {
        let memory = self.s7_slave_memory(slave_id)?;
        let addr = crate::s7_address::parse_s7_address(address)?;
        let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        let ok = match addr.kind {
            crate::s7_address::S7Kind::Timer | crate::s7_address::S7Kind::Counter => {
                mem.write_tc(addr.area, addr.byte, bytes)
            }
            crate::s7_address::S7Kind::Bit => {
                // 逐位读改写
                let mut ok = true;
                for (b, v) in bytes.iter().enumerate() {
                    let abs = addr.byte as usize * 8 + addr.bit as usize + b;
                    match mem.read_area_bytes(addr.area, addr.db, (abs / 8) as u32, 1) {
                        Some(mut cur) => {
                            let mask = 1u8 << (abs % 8);
                            if *v != 0 {
                                cur[0] |= mask;
                            } else {
                                cur[0] &= !mask;
                            }
                            ok &= mem
                                .write_area_bytes(addr.area, addr.db, (abs / 8) as u32, &cur)
                                .is_some();
                        }
                        None => ok = false,
                    }
                }
                ok.then_some(())
            }
            _ => mem.write_area_bytes(addr.area, addr.db, addr.byte, bytes),
        };
        ok.ok_or_else(|| CoreError::Modbus {
            code: "S7_SLAVE_WRITE_FAILED",
            message: format!("地址 {address} 写入失败(越界?)"),
            details: None,
        })
    }

    /// S7 从站内存读(按地址语法与元素数)。
    pub fn s7_slave_get(
        &self,
        slave_id: &str,
        address: &str,
        count: u16,
    ) -> Result<Vec<u8>, CoreError> {
        let memory = self.s7_slave_memory(slave_id)?;
        let addr = crate::s7_address::parse_s7_address(address)?;
        let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        let result = match addr.kind {
            crate::s7_address::S7Kind::Timer | crate::s7_address::S7Kind::Counter => {
                mem.read_tc(addr.area, addr.byte, count)
            }
            crate::s7_address::S7Kind::Bit => {
                let mut packed = vec![0u8; (count as usize + 7) / 8];
                let mut ok = true;
                for b in 0..count as usize {
                    let abs = addr.byte as usize * 8 + addr.bit as usize + b;
                    match mem.read_area_bytes(addr.area, addr.db, (abs / 8) as u32, 1) {
                        Some(v) if v[0] >> (abs % 8) & 1 == 1 => packed[b / 8] |= 1 << (b % 8),
                        Some(_) => {}
                        None => ok = false,
                    }
                }
                ok.then_some(packed)
            }
            _ => mem.read_area_bytes(
                addr.area,
                addr.db,
                addr.byte,
                addr.kind.elem_bytes() as usize * count as usize,
            ),
        };
        result.ok_or_else(|| CoreError::Modbus {
            code: "S7_SLAVE_READ_FAILED",
            message: format!("地址 {address} 读取失败(越界?)"),
            details: None,
        })
    }

    pub fn start_mc_tcp_slave(
        &mut self,
        slave_id: &str,
        port: u16,
        seed: bool,
    ) -> Result<(), CoreError> {
        if self.mc_slaves.contains_key(slave_id) {
            return Err(CoreError::Modbus {
                code: "MC_SLAVE_ALREADY_RUNNING",
                message: format!("MC 从站 {slave_id} 已在运行"),
                details: None,
            });
        }
        let mut memory = crate::mc_slave::McSlaveMemory::new();
        if seed {
            crate::mc_slave::seed_demo(&mut memory);
        }
        let memory = Arc::new(Mutex::new(memory));
        let running = Arc::new(Mutex::new(true));
        let listener = std::net::TcpListener::bind(format!("127.0.0.1:{port}")).map_err(|e| {
            CoreError::Modbus {
                code: "MC_SLAVE_BIND_FAILED",
                message: format!("MC 从站绑定端口 {port} 失败:{e}"),
                details: Some(serde_json::json!({ "port": port, "error": e.to_string() })),
            }
        })?;
        let _ = listener.set_nonblocking(true);
        let mem_clone = Arc::clone(&memory);
        let run_flag = Arc::clone(&running);
        // UDP 监听(§2.5:MC/SLMP 同端口体系支持 UDP)——绑同端口号
        if let Ok(sock) = std::net::UdpSocket::bind(format!("127.0.0.1:{port}")) {
            let _ = sock.set_nonblocking(true);
            let mem_u = Arc::clone(&mem_clone);
            let rf_u = Arc::clone(&run_flag);
            std::thread::spawn(move || handle_mc_slave_udp(sock, mem_u, rf_u));
        }
        std::thread::spawn(move || {
            while *run_flag.lock().unwrap_or_else(|e| e.into_inner()) {
                match listener.accept() {
                    Ok((stream, _)) => {
                        let mem = Arc::clone(&mem_clone);
                        let rf = Arc::clone(&run_flag);
                        std::thread::spawn(move || {
                            handle_mc_slave_client(stream, mem, rf);
                        });
                    }
                    Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                        std::thread::sleep(std::time::Duration::from_millis(10));
                    }
                    Err(_) => break,
                }
            }
        });
        self.mc_slaves
            .insert(slave_id.to_string(), (memory, running));
        Ok(())
    }

    /// 停止 MC 虚拟从站。
    pub fn stop_mc_slave(&mut self, slave_id: &str) -> Result<(), CoreError> {
        match self.mc_slaves.remove(slave_id) {
            Some((_mem, running)) => {
                *running.lock().unwrap_or_else(|e| e.into_inner()) = false;
                Ok(())
            }
            None => Err(CoreError::Modbus {
                code: "MC_SLAVE_NOT_FOUND",
                message: format!("MC 从站 {slave_id} 不存在"),
                details: None,
            }),
        }
    }

    /// MC 从站内存写(供 JSONL mc_slave_set 调用)。
    pub fn mc_slave_set(
        &mut self,
        slave_id: &str,
        device: &str,
        start: u32,
        values: &[u16],
    ) -> Result<(), CoreError> {
        let (memory, _) = self
            .mc_slaves
            .get(slave_id)
            .ok_or_else(|| CoreError::Modbus {
                code: "MC_SLAVE_NOT_FOUND",
                message: format!("MC 从站 {slave_id} 不存在"),
                details: None,
            })?;
        let spec = crate::mc_address::device_spec(device).ok_or_else(|| CoreError::Modbus {
            code: "MC_ADDRESS_INVALID",
            message: format!("未知软元件「{device}」"),
            details: None,
        })?;
        let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        if spec.is_bit {
            mem.set_bits(spec.code, start, values)
        } else {
            mem.set_words(spec.code, start, values)
        }
    }

    /// 设置从站内存值。
    pub fn slave_set_value(
        &mut self,
        slave_id: &str,
        area: &str,
        address: u16,
        values: &[u16],
    ) -> Result<(), CoreError> {
        let (memory, _) = self.slaves.get(slave_id).ok_or_else(|| CoreError::Modbus {
            code: "SLAVE_NOT_FOUND",
            message: format!("从站 {slave_id} 不存在"),
            details: None,
        })?;
        let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        match area {
            "holding" | "holding_registers" => mem.set_holding(address, values),
            "input" | "input_registers" => mem.set_input_register(address, values),
            _ => {
                return Err(CoreError::Modbus {
                    code: "INVALID_AREA",
                    message: format!("不支持的区域:{area}"),
                    details: None,
                });
            }
        }
        Ok(())
    }

    /// 设置从站线圈值。
    pub fn slave_set_coil(
        &mut self,
        slave_id: &str,
        area: &str,
        address: u16,
        values: &[bool],
    ) -> Result<(), CoreError> {
        let (memory, _) = self.slaves.get(slave_id).ok_or_else(|| CoreError::Modbus {
            code: "SLAVE_NOT_FOUND",
            message: format!("从站 {slave_id} 不存在"),
            details: None,
        })?;
        let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        match area {
            "coil" | "coils" => mem.set_coil(address, values),
            "discrete" | "discrete_inputs" => mem.set_discrete_input(address, values),
            _ => {
                return Err(CoreError::Modbus {
                    code: "INVALID_AREA",
                    message: format!("不支持的区域:{area}"),
                    details: None,
                });
            }
        }
        Ok(())
    }

    /// 清零从站内存区。
    pub fn slave_clear(&mut self, slave_id: &str, area: &str) -> Result<(), CoreError> {
        let (memory, _) = self.slaves.get(slave_id).ok_or_else(|| CoreError::Modbus {
            code: "SLAVE_NOT_FOUND",
            message: format!("从站 {slave_id} 不存在"),
            details: None,
        })?;
        let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        mem.clear_area(area);
        Ok(())
    }

    /// 读取从站内存值(用于 UI 显示)。
    pub fn slave_get_memory(
        &self,
        slave_id: &str,
        area: &str,
        address: u16,
        count: u16,
    ) -> Result<Vec<u16>, CoreError> {
        let (memory, _) = self.slaves.get(slave_id).ok_or_else(|| CoreError::Modbus {
            code: "SLAVE_NOT_FOUND",
            message: format!("从站 {slave_id} 不存在"),
            details: None,
        })?;
        let mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        let start = address as usize;
        let end = (start + count as usize).min(65536);
        let values: Vec<u16> = match area {
            "holding" | "holding_registers" => mem.holding_registers[start..end].to_vec(),
            "input" | "input_registers" => mem.input_registers[start..end].to_vec(),
            _ => {
                return Err(CoreError::Modbus {
                    code: "INVALID_AREA",
                    message: format!("不支持的区域:{area}"),
                    details: None,
                });
            }
        };
        Ok(values)
    }

    // === 串口从站模拟(Electron 持 COM 句柄,Rust 通过 JSONL 桥接)===

    /// 启动一个串口从站(注册内存区,不监听端口 —— Electron 驱动收发)。
    pub fn start_serial_slave(&mut self, slave_id: &str) -> Result<(), CoreError> {
        if self.serial_slaves.contains_key(slave_id) {
            return Err(CoreError::Modbus {
                code: "SLAVE_ALREADY_RUNNING",
                message: format!("串口从站 {slave_id} 已在运行"),
                details: None,
            });
        }
        let memory = Arc::new(Mutex::new(SlaveMemory::default()));
        self.serial_slaves.insert(slave_id.to_string(), memory);
        Ok(())
    }

    /// 停止串口从站。
    pub fn stop_serial_slave(&mut self, slave_id: &str) -> Result<(), CoreError> {
        if self.serial_slaves.remove(slave_id).is_some() {
            Ok(())
        } else {
            Err(CoreError::Modbus {
                code: "SLAVE_NOT_FOUND",
                message: format!("串口从站 {slave_id} 不存在"),
                details: None,
            })
        }
    }

    /// 处理从串口收到的原始字节:解析 RTU 帧,生成响应 RTU 帧。
    /// 返回 (should_respond, response_bytes)。
    /// 如果是广播请求(unit 0)或无效帧,返回 (false, [])。
    pub fn slave_handle_serial_bytes(
        &mut self,
        slave_id: &str,
        bytes: &[u8],
    ) -> Result<(bool, Vec<u8>), CoreError> {
        let memory =
            self.serial_slaves
                .get(slave_id)
                .cloned()
                .ok_or_else(|| CoreError::Modbus {
                    code: "SLAVE_NOT_FOUND",
                    message: format!("串口从站 {slave_id} 不存在"),
                    details: None,
                })?;

        // 尝试解析为 RTU 请求帧
        let frame = match crate::modbus_rtu::RtuFrame::decode(
            bytes,
            crate::modbus_rtu::RtuFrameRole::Request,
        ) {
            Ok(f) => f,
            Err(_) => return Ok((false, vec![])), // 无效帧,不响应
        };

        // 广播请求不响应
        if frame.is_broadcast() {
            // 仍然处理写操作(广播写应该执行)
            let mut pdu = vec![frame.function_code()];
            pdu.extend_from_slice(frame.data());
            let _ = crate::modbus_slave::handle_request(&pdu, &memory);
            return Ok((false, vec![]));
        }

        // 构建完整 PDU 并处理
        let mut pdu = vec![frame.function_code()];
        pdu.extend_from_slice(frame.data());
        let response_pdu = crate::modbus_slave::handle_request(&pdu, &memory);

        match response_pdu {
            Some(resp_pdu) => {
                // 包装成 RTU 响应帧
                let resp_frame = crate::modbus_rtu::RtuFrame::response(
                    frame.unit_id(),
                    resp_pdu[0],
                    &resp_pdu[1..],
                )
                .map_err(CoreError::from)?;
                Ok((true, resp_frame.encode()))
            }
            None => Ok((false, vec![])),
        }
    }

    /// 串口从站的内存区操作(复用 TCP 从站的方法签名)。
    pub fn serial_slave_set_value(
        &mut self,
        slave_id: &str,
        area: &str,
        address: u16,
        values: &[u16],
    ) -> Result<(), CoreError> {
        let memory = self
            .serial_slaves
            .get(slave_id)
            .ok_or_else(|| CoreError::Modbus {
                code: "SLAVE_NOT_FOUND",
                message: format!("串口从站 {slave_id} 不存在"),
                details: None,
            })?;
        let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        match area {
            "holding" | "holding_registers" => mem.set_holding(address, values),
            "input" | "input_registers" => mem.set_input_register(address, values),
            _ => {
                return Err(CoreError::Modbus {
                    code: "INVALID_AREA",
                    message: format!("不支持的区域:{area}"),
                    details: None,
                });
            }
        }
        Ok(())
    }

    pub fn serial_slave_get_memory(
        &self,
        slave_id: &str,
        area: &str,
        address: u16,
        count: u16,
    ) -> Result<Vec<u16>, CoreError> {
        let memory = self
            .serial_slaves
            .get(slave_id)
            .ok_or_else(|| CoreError::Modbus {
                code: "SLAVE_NOT_FOUND",
                message: format!("串口从站 {slave_id} 不存在"),
                details: None,
            })?;
        let mem = memory.lock().unwrap_or_else(|e| e.into_inner());
        let start = address as usize;
        let end = (start + count as usize).min(65536);
        match area {
            "holding" | "holding_registers" => Ok(mem.holding_registers[start..end].to_vec()),
            "input" | "input_registers" => Ok(mem.input_registers[start..end].to_vec()),
            _ => Err(CoreError::Modbus {
                code: "INVALID_AREA",
                message: format!("不支持的区域:{area}"),
                details: None,
            }),
        }
    }

    // === 轮询流管理(v2 流式协议) ===

    /// 注册一个轮询流。
    pub fn start_poll_stream(
        &mut self,
        stream_id: &str,
        connection_id: &str,
        fc: u8,
        start_address: u16,
        quantity: u16,
        interval_ms: u32,
    ) -> Result<(), CoreError> {
        let stream = PollStream {
            stream_id: stream_id.to_string(),
            connection_id: connection_id.to_string(),
            fc,
            start_address,
            quantity,
            interval_ms,
            next_due: std::time::Instant::now(),
        };
        self.poll_streams.insert(stream_id.to_string(), stream);
        Ok(())
    }

    /// 注销一个轮询流。
    pub fn stop_poll_stream(&mut self, stream_id: &str) -> Result<(), CoreError> {
        if self.poll_streams.remove(stream_id).is_some() {
            Ok(())
        } else {
            Err(CoreError::Modbus {
                code: "STREAM_NOT_FOUND",
                message: format!("轮询流 {stream_id} 不存在"),
                details: None,
            })
        }
    }

    /// 检查所有轮询流,返回到期的 stream_id 列表。
    pub fn due_poll_streams(&mut self) -> Vec<String> {
        let now = std::time::Instant::now();
        let mut due = Vec::new();
        for stream in self.poll_streams.values_mut() {
            if stream.next_due <= now {
                due.push(stream.stream_id.clone());
                // 更新下次到期时间
                stream.next_due =
                    now + std::time::Duration::from_millis(u64::from(stream.interval_ms));
            }
        }
        due
    }

    /// 执行一次轮询读取,返回 (stream_id, 结果 JSON)。
    pub fn fire_poll(&mut self, stream_id: &str) -> Result<Value, CoreError> {
        let stream = self
            .poll_streams
            .get(stream_id)
            .ok_or_else(|| CoreError::Modbus {
                code: "STREAM_NOT_FOUND",
                message: format!("轮询流 {stream_id} 不存在"),
                details: None,
            })?;
        let connection_id = stream.connection_id.clone();
        let fc = stream.fc;
        let start_address = stream.start_address;
        let quantity = stream.quantity;

        // 构建 PDU
        let request_pdu = match fc {
            0x01 => crate::modbus_pdu::build_read_coils_pdu(start_address, quantity),
            0x02 => crate::modbus_pdu::build_read_discrete_inputs_pdu(start_address, quantity),
            0x03 => crate::modbus_pdu::build_read_holding_registers_pdu(start_address, quantity),
            0x04 => crate::modbus_pdu::build_read_input_registers_pdu(start_address, quantity),
            _ => crate::modbus_pdu::build_read_holding_registers_pdu(start_address, quantity),
        }?;

        // 根据连接类型分发到对应的事务方法(修复:UDP 轮询之前硬编码 transact_tcp 必然失败)
        let response_pdu = match self.connections.get(&connection_id) {
            Some(Connection::Tcp { .. }) => self.transact_tcp(&connection_id, &request_pdu)?,
            Some(Connection::Udp { .. }) => self.transact_udp(&connection_id, &request_pdu)?,
            Some(Connection::McTcp { .. })
            | Some(Connection::McUdp { .. })
            | Some(Connection::Mc1eTcp { .. })
            | Some(Connection::S7Tcp { .. })
            | Some(Connection::FwTcp { .. })
            | Some(Connection::PpiTcp { .. })
            | Some(Connection::FinsTcp { .. })
            | Some(Connection::FinsUdp { .. }) => {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: "MC 连接不支持 Modbus 轮询流".into(),
                    details: None,
                });
            }
            None => {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_NOT_FOUND",
                    message: format!("连接 {connection_id} 不存在"),
                    details: None,
                });
            }
        };

        // 解析响应
        if response_pdu.is_empty() {
            return Ok(serde_json::json!({
                "streamId": stream_id,
                "registers": [],
                "timestamp": chrono_now_ms(),
            }));
        }

        // 检查异常
        let resp_fc = response_pdu[0];
        if resp_fc & 0x80 != 0 {
            let exc_code = response_pdu.get(1).copied().unwrap_or(0);
            return Ok(serde_json::json!({
                "streamId": stream_id,
                "exception": true,
                "exceptionCode": exc_code,
                "timestamp": chrono_now_ms(),
            }));
        }

        match fc {
            0x03 | 0x04 => {
                let regs = crate::modbus_pdu::parse_read_holding_registers_response(
                    &response_pdu,
                    quantity,
                )
                .unwrap_or_default();
                Ok(serde_json::json!({
                    "streamId": stream_id,
                    "registers": regs,
                    "timestamp": chrono_now_ms(),
                }))
            }
            0x01 | 0x02 => {
                let bits = crate::modbus_pdu::parse_read_coils_response(&response_pdu, quantity)
                    .unwrap_or_default();
                Ok(serde_json::json!({
                    "streamId": stream_id,
                    "coils": bits,
                    "timestamp": chrono_now_ms(),
                }))
            }
            _ => Ok(serde_json::json!({
                "streamId": stream_id,
                "registers": [],
                "timestamp": chrono_now_ms(),
            })),
        }
    }
}

fn chrono_now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// 从站客户端处理(独立线程)。
fn handle_slave_client(
    mut stream: TcpStream,
    memory: Arc<Mutex<SlaveMemory>>,
    allowed: Vec<u8>,
    running: Arc<Mutex<bool>>,
) {
    use std::io::Write;
    // Windows 上 accept 出的 stream 继承 listener 非阻塞模式——必须显式切回阻塞,
    // 否则 SO_RCVTIMEO 无效,循环 100% 自旋吃满一核。
    stream.set_nonblocking(false).ok();
    let mut buf = [0u8; 1024];
    stream
        .set_read_timeout(Some(Duration::from_millis(100)))
        .ok();
    loop {
        if !*running.lock().unwrap_or_else(|e| e.into_inner()) {
            break;
        }
        match stream.read(&mut buf) {
            Ok(0) => break,
            Ok(n) => {
                if n < MBAP_HEADER_LEN + 1 {
                    continue;
                }
                let (_header, request_pdu) = match modbus_tcp::parse_mbap_frame(&buf[..n]) {
                    Ok(v) => v,
                    Err(_) => continue,
                };
                let unit_id = buf[6];
                if !allowed.is_empty() && !allowed.contains(&unit_id) {
                    continue;
                }
                let response_pdu = crate::modbus_slave::handle_request(&request_pdu, &memory);
                if let Some(resp) = response_pdu {
                    let tid = u16::from_be_bytes([buf[0], buf[1]]);
                    let frame = modbus_tcp::build_mbap_frame(tid, unit_id, &resp);
                    let _ = stream.write_all(&frame);
                }
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => continue,
            Err(ref e) if e.kind() == std::io::ErrorKind::TimedOut => continue,
            Err(_) => break,
        }
    }
}

impl Default for Session {
    fn default() -> Self {
        Self::new()
    }
}

/// MC 虚拟从站的客户端处理线程。
/// 按长度字段自描述重组粘包,调用 mc_slave::handle_mc_request 生成响应。
/// MC 虚拟从站 UDP 处理线程:收一帧(数据报天然定界)→ 处理 → 回一帧。
fn handle_mc_slave_udp(
    socket: std::net::UdpSocket,
    memory: Arc<Mutex<crate::mc_slave::McSlaveMemory>>,
    running: Arc<Mutex<bool>>,
) {
    let mut buf = [0u8; 2048];
    while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        match socket.recv_from(&mut buf) {
            Ok((n, peer)) => {
                let is_ascii = buf.starts_with(b"5000") || buf.starts_with(b"5400");
                let resp: Option<Vec<u8>> = if is_ascii {
                    handle_mc_ascii_request(&buf[..n], &memory)
                } else {
                    crate::mc_slave::handle_mc_request(&buf[..n], &memory).ok()
                };
                if let Some(r) = resp {
                    let _ = socket.send_to(&r, peer);
                }
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
            Err(_) => break,
        }
    }
}

fn handle_mc_slave_client(
    mut stream: TcpStream,
    memory: Arc<Mutex<crate::mc_slave::McSlaveMemory>>,
    running: Arc<Mutex<bool>>,
) {
    use std::io::Read;
    use std::io::Write;
    // 同 handle_slave_client:切回阻塞模式(Windows 非阻塞继承)
    stream.set_nonblocking(false).ok();
    stream
        .set_read_timeout(Some(Duration::from_millis(100)))
        .ok();
    let mut pending: Vec<u8> = Vec::new();
    let mut chunk = [0u8; 2048];
    while *running.lock().unwrap_or_else(|e| e.into_inner()) {
        match stream.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                pending.extend_from_slice(&chunk[..n]);
                // 先判 ASCII 帧(文本 "5000"/"5400" 开头)再判 Binary(字节 50 00)
                loop {
                    if pending.starts_with(b"5000") || pending.starts_with(b"5400") {
                        match mc_ascii_frame_length(&pending) {
                            Some(len) if pending.len() >= len => {
                                let frame: Vec<u8> = pending.drain(..len).collect();
                                if let Some(resp) = handle_mc_ascii_request(&frame, &memory) {
                                    if stream.write_all(&resp).is_err() {
                                        return;
                                    }
                                }
                                continue;
                            }
                            Some(_) | None => break, // 不完整
                        }
                    }
                    let frame_len = match mc_frame_length(&pending) {
                        Some(l) => l,
                        None => break, // 不完整,继续读
                    };
                    if pending.len() < frame_len {
                        break;
                    }
                    let frame: Vec<u8> = pending.drain(..frame_len).collect();
                    match crate::mc_slave::handle_mc_request(&frame, &memory) {
                        Ok(resp) => {
                            if stream.write_all(&resp).is_err() {
                                return;
                            }
                        }
                        Err(_) => {
                            // 帧损坏:丢弃该帧继续(不断开,与宽容的真机行为一致)
                        }
                    }
                }
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => continue,
            Err(ref e) if e.kind() == std::io::ErrorKind::TimedOut => continue,
            Err(_) => break,
        }
    }
}

/// ASCII 请求帧字符长度推断:头(4 [+4]) + 路由(10) + 长度(4) + 长度字段统计的字符。
/// 长度字段是二进制等效字节数,数据区字符数 = (len-2)×2(保守上界)。
fn mc_ascii_frame_length(buf: &[u8]) -> Option<usize> {
    let is4e = buf.starts_with(b"5400");
    let header_chars = if is4e { 22usize } else { 18usize }; // 副(4)+[seq(4)]+路由(10)+长度(4)
    if buf.len() < header_chars {
        return None;
    }
    let s = String::from_utf8_lossy(buf);
    let len_str = &s[header_chars - 4..header_chars];
    let data_len = usize::from_str_radix(len_str, 16).ok()?;
    // 监视定时器+指令区(二进制等效)全部按 2 字符/字节保守估算
    Some(header_chars + data_len * 2)
}

/// 处理 ASCII 请求帧:文本 → 解析指令(仅支持 0401/1401 成批读写)→ 生成 ASCII 响应。
/// 返回 None = 帧损坏或暂不支持的指令(丢弃)。
fn handle_mc_ascii_request(
    frame: &[u8],
    memory: &Arc<Mutex<crate::mc_slave::McSlaveMemory>>,
) -> Option<Vec<u8>> {
    let s = String::from_utf8(frame.to_vec()).ok()?;
    let is4e = s.starts_with("5400");
    // 字符布局:副(4)[+seq(4)] + net(2)+pc(2)+io(4)+st(2)+len(4) + watchdog(4) →
    // 固定头 22(3E)/26(4E),然后 cmd(4) + sub(4) + body
    let base = if is4e { 26 } else { 22 };
    // MC ASCII 帧应全 ASCII:多字节 UTF-8 落在切片边界会 char-boundary panic
    if !s.is_ascii() {
        return None;
    }
    if s.len() < base + 8 {
        return None;
    }
    let cmd = u16::from_str_radix(&s[base..base + 4], 16).ok()?;
    let sub = u16::from_str_radix(&s[base + 4..base + 8], 16).ok()?;
    let body = &s[base + 8..];
    let is_bit = sub == 0x0000; // 0401: 字=0001/位=0000(⚠️ 与 0403 相反,§7.1)
    // body 最短 12 字符(地址6+代码2[+*]+点数4);不足直接丢弃(旧实现切片越界 panic,
    // UDP 路径一包即可永久杀死从站 UDP 服务)
    if body.len() < 12 {
        return None;
    }
    match cmd {
        0x0401 => {
            // 地址(6)+代码(2[+*])+点数(4)
            let addr_hex = &body[..6];
            let head = u32::from_str_radix(addr_hex, 16).ok()?;
            let code_str = &body[6..];
            let code_u8 = u8::from_str_radix(&code_str[..2], 16).ok()?;
            let points_start = if code_str.len() > 2 && code_str.as_bytes()[2] == b'*' {
                3
            } else {
                2
            };
            let points =
                u16::from_str_radix(&body[6 + points_start..6 + points_start + 4], 16).ok()?;
            let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
            let result = if is_bit {
                mem.get_bits(code_u8, head, points)
            } else {
                mem.get_words(code_u8, head, points)
            };
            let values = result.ok()?;
            // 组装 ASCII 响应
            let data_str: String = if is_bit {
                values
                    .iter()
                    .map(|v| if *v == 1 { '1' } else { '0' })
                    .collect()
            } else {
                values.iter().map(|v| format!("{v:04X}")).collect()
            };
            // 长度字段 = 结束码(2) + 数据二进制等效(位=点数;字=点数*2)
            let bin_data = if is_bit {
                values.len()
            } else {
                values.len() * 2
            };
            let resp_len = 2 + bin_data;
            let sub_hdr = if is4e { &s[4..8] } else { "" }; // 4E 回显序列号
            let resp = format!(
                "{}{}00FFFF0300{:04X}0000{}",
                if is4e { "D400" } else { "D000" },
                sub_hdr,
                resp_len,
                data_str
            );
            Some(resp.into_bytes())
        }
        0x1401 => {
            // 地址(6)+代码(2[+*])+点数(4)+数据
            let addr_hex = &body[..6];
            let head = u32::from_str_radix(addr_hex, 16).ok()?;
            let code_str = &body[6..];
            let code_u8 = u8::from_str_radix(&code_str[..2], 16).ok()?;
            let star = code_str.len() > 2 && code_str.as_bytes()[2] == b'*';
            let points_off = 6 + if star { 3 } else { 2 };
            let count = u16::from_str_radix(&body[points_off..points_off + 4], 16).ok()?;
            let data = &body[points_off + 4..];
            let values: Vec<u16> = if is_bit {
                data.bytes()
                    .take(count as usize)
                    .map(|b| if b == b'1' { 1 } else { 0 })
                    .collect()
            } else {
                (0..count as usize)
                    .filter_map(|i| {
                        data.get(i * 4..i * 4 + 4)
                            .and_then(|h| u16::from_str_radix(h, 16).ok())
                    })
                    .collect()
            };
            let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
            let result = if is_bit {
                mem.set_bits(code_u8, head, &values)
            } else {
                mem.set_words(code_u8, head, &values)
            };
            result.ok()?;
            let sub_hdr = if is4e { &s[4..8] } else { "" };
            let resp = format!(
                "{}{}00FFFF030000020000",
                if is4e { "D400" } else { "D000" },
                sub_hdr
            );
            Some(resp.into_bytes())
        }
        _ => None, // 其余指令暂不支持 ASCII 模式(虚拟从站用)
    }
}

/// 根据缓冲区现有字节推断完整 MC 帧长度。
/// 返回 None = 数据不足;Some(len) = 完整帧长度。
///
/// ⚠️ 请求帧与响应帧的长度字段语义不同:
/// - 请求:长度 = 监视定时器(2)+指令(2)+子命令(2)+数据区 → 完整帧 = 头(9/11)+长度
/// - 响应:长度 = 结束代码(2)+数据区 → 完整帧 = 头(9/11)+长度
/// 两者恰好都是「固定头 + 长度字段值」,因为长度字段总是紧跟其后统计到帧尾。
fn mc_frame_length(buf: &[u8]) -> Option<usize> {
    if buf.len() < 2 {
        return None;
    }
    let subheader = u16::from_le_bytes([buf[0], buf[1]]);
    let (len_off, header_len) = match subheader {
        0x0050 | 0x00D0 => (7usize, 9usize),  // 3E: 头 = 2+5+2 = 9
        0x0054 | 0x00D4 => (9usize, 11usize), // 4E: 头 = 2+2+5+2 = 11
        _ => return Some(buf.len()),          // 非法副帧头:消费全部(丢弃)
    };
    if buf.len() < header_len {
        return None;
    }
    let data_len = u16::from_le_bytes([buf[len_off], buf[len_off + 1]]) as usize;
    // 完整帧 = 固定头 + 长度字段统计的全部字节
    Some(header_len + data_len)
}

// =============================================================================
// 辅助函数
// =============================================================================

/// Reads one complete GE SRTP response.  The protocol always starts with a
/// fixed 56-byte header; long responses append the declared payload bytes,
/// while short responses carry up to six data bytes inside that header.
fn send_dnp3_application(
    connection: &mut Dnp3Connection,
    connection_id: &str,
    application_fragment: &[u8],
) -> Result<Vec<Vec<u8>>, CoreError> {
    let segments =
        crate::dnp3::segment_application(application_fragment, &mut connection.transport_sequence)?;
    let mut frames = Vec::with_capacity(segments.len());
    for segment in segments {
        let frame = crate::dnp3::build_link_frame(
            crate::dnp3::MASTER_UNCONFIRMED_USER_DATA,
            connection.outstation_address,
            connection.master_address,
            &segment,
        )?;
        connection
            .stream
            .write_all(&frame)
            .and_then(|_| connection.stream.flush())
            .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
        frames.push(frame);
    }
    Ok(frames)
}

fn dnp3_exchange(
    connection: &mut Dnp3Connection,
    connection_id: &str,
    request: Vec<u8>,
    request_sequence: u8,
    operation: &str,
    classes: Vec<u8>,
) -> Result<crate::dnp3::ScanResult, CoreError> {
    let request_frames = send_dnp3_application(connection, connection_id, &request)?;
    let mut response_frames = Vec::new();
    let mut confirmation_frames = Vec::new();
    let mut assembler =
        crate::dnp3::TransportAssembler::new(crate::dnp3::MAX_APPLICATION_FRAGMENT_SIZE)?;
    let mut points = Vec::new();
    let mut iin1 = 0u8;
    let mut iin2 = 0u8;
    let mut received_first = false;
    let mut expected_application_sequence = request_sequence;

    let mut unsolicited_responses = Vec::new();
    let mut unsolicited_active = false;
    let mut unsolicited_initial_sequence = 0u8;
    let mut expected_unsolicited_sequence = 0u8;
    let mut unsolicited_iin1 = 0u8;
    let mut unsolicited_iin2 = 0u8;
    let mut unsolicited_points = Vec::new();

    for _ in 0..crate::dnp3::MAX_RESPONSE_LINK_FRAMES {
        let wire_frame = read_dnp3_link_frame(&mut connection.stream, connection_id)?;
        let link = crate::dnp3::parse_link_frame(&wire_frame)?;
        response_frames.push(wire_frame);
        if link.destination != connection.master_address
            || link.source != connection.outstation_address
        {
            return Err(CoreError::Modbus {
                code: "DNP3_LINK_ADDRESS_MISMATCH",
                message: "DNP3 响应链路地址与当前 Master/Outstation 配置不一致".into(),
                details: Some(json!({
                    "connectionId": connection_id,
                    "expectedSource": connection.outstation_address,
                    "expectedDestination": connection.master_address,
                    "actualSource": link.source,
                    "actualDestination": link.destination
                })),
            });
        }
        if link.control != crate::dnp3::OUTSTATION_UNCONFIRMED_USER_DATA {
            return Err(CoreError::Modbus {
                code: "DNP3_LINK_CONTROL_UNSUPPORTED",
                message: format!(
                    "DNP3 TCP 首轮只接受 Outstation Unconfirmed User Data 0x44，收到 0x{:02X}",
                    link.control
                ),
                details: Some(json!({
                    "connectionId": connection_id,
                    "control": link.control
                })),
            });
        }
        let Some(application) = assembler.add(&link.user_data)? else {
            continue;
        };
        let fragment = crate::dnp3::parse_application_response(&application)?;
        if fragment.confirm_requested {
            let confirm = crate::dnp3::build_confirm(fragment.sequence, fragment.unsolicited)?;
            confirmation_frames.extend(send_dnp3_application(connection, connection_id, &confirm)?);
        }

        if fragment.unsolicited {
            if !unsolicited_active {
                if !fragment.first {
                    return Err(CoreError::Modbus {
                        code: "DNP3_UNSOLICITED_FIR_MISSING",
                        message: "DNP3 自发响应没有以 FIR 首片开始".into(),
                        details: Some(json!({
                            "connectionId": connection_id,
                            "sequence": fragment.sequence
                        })),
                    });
                }
                unsolicited_active = true;
                unsolicited_initial_sequence = fragment.sequence;
                expected_unsolicited_sequence = fragment.sequence;
                unsolicited_iin1 = 0;
                unsolicited_iin2 = 0;
                unsolicited_points.clear();
            } else if fragment.first {
                return Err(CoreError::Modbus {
                    code: "DNP3_UNSOLICITED_FIR_DUPLICATE",
                    message: "DNP3 未结束的自发响应续片重复设置 FIR".into(),
                    details: Some(json!({
                        "connectionId": connection_id,
                        "sequence": fragment.sequence
                    })),
                });
            }
            if fragment.sequence != expected_unsolicited_sequence {
                return Err(CoreError::Modbus {
                    code: "DNP3_UNSOLICITED_SEQUENCE_MISMATCH",
                    message: format!(
                        "DNP3 自发响应序号不符，期望 {expected_unsolicited_sequence}，实际 {}",
                        fragment.sequence
                    ),
                    details: Some(json!({
                        "connectionId": connection_id,
                        "expected": expected_unsolicited_sequence,
                        "actual": fragment.sequence
                    })),
                });
            }
            unsolicited_iin1 |= fragment.iin.iin1;
            unsolicited_iin2 |= fragment.iin.iin2;
            unsolicited_points.extend(fragment.points);
            if fragment.final_fragment {
                unsolicited_responses.push(crate::dnp3::UnsolicitedResponse {
                    initial_sequence: unsolicited_initial_sequence,
                    iin: crate::dnp3::decode_iin(unsolicited_iin1, unsolicited_iin2),
                    points: std::mem::take(&mut unsolicited_points),
                });
                unsolicited_active = false;
            } else {
                expected_unsolicited_sequence =
                    crate::dnp3::next_application_sequence(expected_unsolicited_sequence);
            }
            continue;
        }

        if unsolicited_active {
            return Err(CoreError::Modbus {
                code: "DNP3_UNSOLICITED_INTERRUPTED",
                message: "DNP3 solicited 响应打断了未完成的自发响应".into(),
                details: Some(json!({ "connectionId": connection_id })),
            });
        }
        if fragment.sequence != expected_application_sequence {
            return Err(CoreError::Modbus {
                code: "DNP3_APPLICATION_SEQUENCE_MISMATCH",
                message: format!(
                    "DNP3 应用响应序号不符，期望 {expected_application_sequence}，实际 {}",
                    fragment.sequence
                ),
                details: Some(json!({
                    "connectionId": connection_id,
                    "expected": expected_application_sequence,
                    "actual": fragment.sequence
                })),
            });
        }
        if !received_first && !fragment.first {
            return Err(CoreError::Modbus {
                code: "DNP3_APPLICATION_FIR_MISSING",
                message: "DNP3 solicited 响应没有以 FIR 首片开始".into(),
                details: Some(json!({ "connectionId": connection_id })),
            });
        }
        if received_first && fragment.first {
            return Err(CoreError::Modbus {
                code: "DNP3_APPLICATION_FIR_DUPLICATE",
                message: "DNP3 solicited 响应续片重复设置 FIR".into(),
                details: Some(json!({ "connectionId": connection_id })),
            });
        }
        received_first = true;
        iin1 |= fragment.iin.iin1;
        iin2 |= fragment.iin.iin2;
        points.extend(fragment.points);
        if fragment.final_fragment {
            return Ok(crate::dnp3::ScanResult {
                operation: operation.to_string(),
                classes,
                request_frames,
                response_frames,
                confirmation_frames,
                initial_application_sequence: request_sequence,
                iin: crate::dnp3::decode_iin(iin1, iin2),
                points,
                unsolicited_responses,
                final_application_sequence: fragment.sequence,
                final_transport_sequence: connection.transport_sequence,
            });
        }
        expected_application_sequence =
            crate::dnp3::next_application_sequence(expected_application_sequence);
    }

    Err(CoreError::Modbus {
        code: "DNP3_RESPONSE_NOT_TERMINATED",
        message: format!(
            "DNP3 响应在 {} 个链路帧软件上限内未结束",
            crate::dnp3::MAX_RESPONSE_LINK_FRAMES
        ),
        details: Some(json!({
            "connectionId": connection_id,
            "receivedFrames": response_frames.len()
        })),
    })
}

fn read_dnp3_link_frame(stream: &mut TcpStream, connection_id: &str) -> Result<Vec<u8>, CoreError> {
    let mut header = [0u8; crate::dnp3::LINK_HEADER_LENGTH];
    read_exact(stream, &mut header)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    if header[0] != crate::dnp3::START_BYTE_1 || header[1] != crate::dnp3::START_BYTE_2 {
        return Err(CoreError::Modbus {
            code: "DNP3_LINK_SYNC_INVALID",
            message: "DNP3 TCP 收帧同步字不是 05 64".into(),
            details: Some(json!({
                "connectionId": connection_id,
                "actual": [header[0], header[1]]
            })),
        });
    }
    let body_length = crate::dnp3::wire_body_length(header[2])?;
    let mut body = vec![0u8; body_length];
    read_exact(stream, &mut body)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    let mut frame = Vec::with_capacity(header.len() + body.len());
    frame.extend_from_slice(&header);
    frame.extend_from_slice(&body);
    crate::dnp3::parse_link_frame(&frame)?;
    Ok(frame)
}

fn read_ge_srtp_frame(
    stream: &mut TcpStream,
    connection_id: &str,
    handshake: bool,
) -> Result<Vec<u8>, CoreError> {
    let mut header = vec![0u8; crate::ge_srtp::HEADER_BYTES];
    read_exact(stream, &mut header)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    if handshake {
        return Ok(header);
    }

    let form = header[31];
    let payload_length = u16::from_le_bytes([header[4], header[5]]) as usize;
    let extra = match form {
        crate::ge_srtp::RESPONSE_SHORT => 0,
        crate::ge_srtp::RESPONSE_LONG => payload_length,
        _ => {
            return Err(CoreError::Modbus {
                code: "GE_SRTP_FRAME_INVALID",
                message: format!("GE SRTP 响应格式未知：0x{form:02X}"),
                details: Some(json!({ "connectionId": connection_id, "responseForm": form })),
            });
        }
    };
    if extra == 0 {
        return Ok(header);
    }
    let mut payload = vec![0u8; extra];
    read_exact(stream, &mut payload)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    header.extend_from_slice(&payload);
    Ok(header)
}

/// Reads one complete Fuji SPH response.  The first 20 bytes are always
/// present; bytes 18..19 declare the payload length that follows the header.
fn read_fuji_sph_frame(stream: &mut TcpStream, connection_id: &str) -> Result<Vec<u8>, CoreError> {
    let mut header = vec![0u8; crate::fuji_sph::HEADER_BYTES];
    read_exact(stream, &mut header)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    let payload_length = u16::from_le_bytes([header[18], header[19]]) as usize;
    let mut payload = vec![0u8; payload_length];
    read_exact(stream, &mut payload)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    header.extend_from_slice(&payload);
    Ok(header)
}

/// Reads one complete FATEK ASCII response terminated by ETX.  A hard bound
/// prevents a malformed peer from growing the buffer without limit.
fn read_fatek_frame(stream: &mut TcpStream, connection_id: &str) -> Result<Vec<u8>, CoreError> {
    const MAX_FRAME_BYTES: usize = 1024;
    let mut frame = Vec::with_capacity(64);
    let mut one = [0u8; 1];
    while frame.len() < MAX_FRAME_BYTES {
        read_exact(stream, &mut one)
            .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
        frame.push(one[0]);
        if one[0] == 0x03 {
            return Ok(frame);
        }
    }
    Err(CoreError::Modbus {
        code: "FATEK_FRAME_INVALID",
        message: "FATEK 响应超过 1024 字节或缺少 ETX".into(),
        details: Some(json!({ "connectionId": connection_id })),
    })
}

/// Reads one complete Keyence Host Link ASCII response terminated by CRLF.
/// A bounded byte collector keeps a malformed peer from growing the frame
/// indefinitely and preserves fragmented responses across TCP packets.
fn read_keyence_line(stream: &mut TcpStream, connection_id: &str) -> Result<Vec<u8>, CoreError> {
    const MAX_FRAME_BYTES: usize = 8192;
    let mut frame = Vec::with_capacity(64);
    let mut one = [0u8; 1];
    while frame.len() < MAX_FRAME_BYTES {
        read_exact(stream, &mut one)
            .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
        frame.push(one[0]);
        if frame.ends_with(b"\r\n") {
            return Ok(frame);
        }
    }
    Err(CoreError::Modbus {
        code: "KEYENCE_FRAME_INVALID",
        message: "Keyence Host Link 响应超过 8192 字节或缺少 CRLF".into(),
        details: Some(json!({ "connectionId": connection_id })),
    })
}

/// Reads one complete LS XGT response: fixed 20-byte header followed by the
/// little-endian application length declared at header bytes 16..17.
fn read_ls_xgt_frame(stream: &mut TcpStream, connection_id: &str) -> Result<Vec<u8>, CoreError> {
    let mut header = vec![0u8; crate::ls_xgt::HEADER_BYTES];
    read_exact(stream, &mut header)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    let application_length = u16::from_le_bytes([header[16], header[17]]) as usize;
    let mut application = vec![0u8; application_length];
    read_exact(stream, &mut application)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    header.extend_from_slice(&application);
    Ok(header)
}

/// Reads one complete AMS/TCP frame.  The six-byte transport header declares
/// the exact AMS packet length, so a fragmented response is collected before
/// the ADS codec sees it.
fn read_ads_frame(stream: &mut TcpStream, connection_id: &str) -> Result<Vec<u8>, CoreError> {
    let mut header = vec![0u8; crate::ads::AMS_TCP_HEADER_BYTES];
    read_exact(stream, &mut header)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    let packet_length = u32::from_le_bytes([header[2], header[3], header[4], header[5]]) as usize;
    if !(crate::ads::AMS_HEADER_BYTES..=crate::ads::MAX_AMS_PACKET_LENGTH).contains(&packet_length)
    {
        return Err(CoreError::Modbus {
            code: "ADS_FRAME_INVALID",
            message: format!("AMS/TCP packet length 超出边界: {packet_length}"),
            details: Some(json!({ "connectionId": connection_id, "packetLength": packet_length })),
        });
    }
    let mut payload = vec![0u8; packet_length];
    read_exact(stream, &mut payload)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    header.extend_from_slice(&payload);
    Ok(header)
}

/// Reads one complete EtherNet/IP encapsulation frame from TCP.  The 24-byte
/// header's u16 length is authoritative and bounded by the wire format.
fn read_enip_frame(stream: &mut TcpStream, connection_id: &str) -> Result<Vec<u8>, CoreError> {
    let mut header = vec![0u8; crate::enip::ENIP_HEADER_BYTES];
    read_exact(stream, &mut header)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    let payload_length = u16::from_le_bytes([header[2], header[3]]) as usize;
    let mut payload = vec![0u8; payload_length];
    read_exact(stream, &mut payload)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    header.extend_from_slice(&payload);
    Ok(header)
}

/// Reads one complete IEC 60870-5-104 APDU. The one-byte length field covers
/// the four control bytes plus optional ASDU and is validated before payload
/// allocation.
fn read_iec104_frame(stream: &mut TcpStream, connection_id: &str) -> Result<Vec<u8>, CoreError> {
    let mut header = [0u8; 2];
    read_exact(stream, &mut header)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    if header[0] != crate::iec104::START_BYTE {
        return Err(CoreError::Modbus {
            code: "IEC104_INVALID",
            message: "IEC104 APDU 起始字节必须为 0x68".into(),
            details: Some(json!({
                "connectionId": connection_id,
                "actual": header[0]
            })),
        });
    }
    let declared = header[1] as usize;
    if !(4..=crate::iec104::MAX_APDU_LENGTH_FIELD).contains(&declared) {
        return Err(CoreError::Modbus {
            code: "IEC104_INVALID",
            message: "IEC104 APDU 长度字段必须在 4..253".into(),
            details: Some(json!({
                "connectionId": connection_id,
                "declared": declared
            })),
        });
    }
    let mut frame = Vec::with_capacity(declared + 2);
    frame.extend_from_slice(&header);
    let mut body = vec![0u8; declared];
    read_exact(stream, &mut body)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    frame.extend_from_slice(&body);
    crate::iec104::parse_apdu(&frame)?;
    Ok(frame)
}

/// Reads one complete MQTT 3.1.1 packet.  The remaining-length field is
/// variable-width, so it is collected byte-by-byte before the payload is
/// allocated and handed to the codec.
fn read_mqtt_frame(stream: &mut TcpStream, connection_id: &str) -> Result<Vec<u8>, CoreError> {
    let mut fixed = [0u8; 1];
    read_exact(stream, &mut fixed)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    let mut length_bytes = Vec::with_capacity(4);
    let mut remaining_length = 0usize;
    let mut multiplier = 1usize;
    let mut terminated = false;
    for _ in 0..4 {
        let mut byte = [0u8; 1];
        read_exact(stream, &mut byte)
            .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
        length_bytes.push(byte[0]);
        remaining_length = remaining_length
            .checked_add(((byte[0] & 0x7F) as usize).saturating_mul(multiplier))
            .ok_or_else(|| CoreError::Modbus {
                code: "MQTT_FRAME_INVALID",
                message: "MQTT remaining length 溢出".into(),
                details: Some(json!({ "connectionId": connection_id })),
            })?;
        if remaining_length > crate::mqtt::MAX_REMAINING_LENGTH {
            return Err(CoreError::Modbus {
                code: "MQTT_FRAME_TOO_LARGE",
                message: format!(
                    "MQTT remaining length 超过 {} 字节",
                    crate::mqtt::MAX_REMAINING_LENGTH
                ),
                details: Some(
                    json!({ "connectionId": connection_id, "remainingLength": remaining_length }),
                ),
            });
        }
        if byte[0] & 0x80 == 0 {
            terminated = true;
            break;
        }
        multiplier = multiplier
            .checked_mul(128)
            .ok_or_else(|| CoreError::Modbus {
                code: "MQTT_FRAME_INVALID",
                message: "MQTT remaining length multiplier 溢出".into(),
                details: Some(json!({ "connectionId": connection_id })),
            })?;
    }
    if !terminated {
        return Err(CoreError::Modbus {
            code: "MQTT_FRAME_INVALID",
            message: "MQTT remaining length 最多允许 4 个字节".into(),
            details: Some(json!({ "connectionId": connection_id, "lengthBytes": length_bytes })),
        });
    }
    let mut frame = Vec::with_capacity(1 + length_bytes.len() + remaining_length);
    frame.push(fixed[0]);
    frame.extend_from_slice(&length_bytes);
    let mut payload = vec![0u8; remaining_length];
    read_exact(stream, &mut payload)
        .map_err(|e| connection_io_error(connection_id, &e.to_string()))?;
    frame.extend_from_slice(&payload);
    crate::mqtt::parse_frame(&frame)?;
    Ok(frame)
}

fn read_exact(stream: &mut TcpStream, buf: &mut [u8]) -> std::io::Result<()> {
    stream.read_exact(buf)
}

fn parse_mbap_header(bytes: &[u8]) -> Result<MbapHeader, CoreError> {
    if bytes.len() < MBAP_HEADER_LEN {
        return Err(CoreError::from(
            crate::modbus_rtu::RtuError::MbapFrameTooShort { len: bytes.len() },
        ));
    }
    Ok(MbapHeader {
        transaction_id: u16::from_be_bytes([bytes[0], bytes[1]]),
        protocol_id: u16::from_be_bytes([bytes[2], bytes[3]]),
        length: u16::from_be_bytes([bytes[4], bytes[5]]),
        unit_id: bytes[6],
    })
}

fn connection_failed(addr: &str, reason: &str) -> CoreError {
    CoreError::Modbus {
        code: "CONNECTION_FAILED",
        message: format!("连接 {addr} 失败：{reason}"),
        details: Some(serde_json::json!({ "address": addr, "reason": reason })),
    }
}

fn connection_io_error(id: &str, reason: &str) -> CoreError {
    CoreError::Modbus {
        code: "CONNECTION_IO_ERROR",
        message: format!("连接 {id} 通信失败：{reason}"),
        details: Some(serde_json::json!({ "connectionId": id, "reason": reason })),
    }
}

/// 从 TCP 流读 RTU 响应:最少 4 字节,最多 256 字节。
/// 策略:先读前 2 字节(unit + fc),按 FC 推断长度,读完整个帧。
fn read_rtu_response_stream(stream: &mut TcpStream, buf: &mut Vec<u8>) -> std::io::Result<usize> {
    use std::io::Read;
    // 读 unit_id + fc(2 字节)
    let mut header = [0u8; 2];
    stream.read_exact(&mut header)?;
    let fc = header[1];
    buf.clear();
    buf.extend_from_slice(&header);
    // 推断剩余长度
    let remaining = if fc & 0x80 != 0 {
        // 异常响应:1 字节异常码 + 2 字节 CRC = 3
        3
    } else {
        match fc {
            0x01..=0x04 => {
                // 读响应:byte_count(1) + data + CRC(2)。需要先读 byte_count。
                let mut bc = [0u8; 1];
                stream.read_exact(&mut bc)?;
                buf.push(bc[0]);
                usize::from(bc[0]) + 2
            }
            0x05 | 0x06 | 0x0F | 0x10 => {
                // 写响应:addr(2) + qty(2) + CRC(2) = 6
                6
            }
            _ => 4,
        }
    };
    let mut rest = vec![0u8; remaining];
    stream.read_exact(&mut rest)?;
    buf.extend_from_slice(&rest);
    Ok(buf.len())
}

/// 从 TCP 流读 ASCII 响应:读到 CRLF 为止。
fn read_ascii_response_stream(stream: &mut TcpStream, buf: &mut Vec<u8>) -> std::io::Result<usize> {
    use std::io::Read;
    buf.clear();
    let mut byte = [0u8; 1];
    loop {
        match stream.read(&mut byte) {
            Ok(0) => {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::UnexpectedEof,
                    "连接关闭",
                ));
            }
            Ok(_) => {
                buf.push(byte[0]);
                if buf.len() >= 2 && buf[buf.len() - 2] == b'\r' && buf[buf.len() - 1] == b'\n' {
                    return Ok(buf.len());
                }
                if buf.len() > 1024 {
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::InvalidData,
                        "ASCII 帧超过 1024 字节",
                    ));
                }
            }
            Err(e) => return Err(e),
        }
    }
}

fn connection_not_found(id: &str) -> CoreError {
    CoreError::Modbus {
        code: "CONNECTION_NOT_FOUND",
        message: format!("连接 {id} 不存在"),
        details: Some(serde_json::json!({ "connectionId": id })),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;

    #[test]
    fn open_and_close_tcp_connection() {
        // 用回环 listener 模拟服务器
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let port = addr.port();

        let mut session = Session::new();
        session
            .open_tcp("test", "127.0.0.1", port, 1, TcpFraming::Standard)
            .unwrap();
        assert!(session.connection_ids().contains(&"test".to_string()));

        session.close_connection("test").unwrap();
        assert!(!session.connection_ids().contains(&"test".to_string()));
    }

    #[test]
    fn close_nonexistent_connection_fails() {
        let mut session = Session::new();
        let result = session.close_connection("nonexistent");
        assert!(result.is_err());
    }

    #[test]
    fn tcp_transaction_round_trip() {
        // 模拟一个 Modbus TCP 从站
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let port = addr.port();

        // 从站线程:接受连接,读取请求,回送一个 FC03 响应(2 个寄存器)
        let handle = std::thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            // 读 MBAP 头 + PDU
            let mut header = [0u8; MBAP_HEADER_LEN];
            socket.read_exact(&mut header).unwrap();
            let pdu_len = u16::from_be_bytes([header[4], header[5]]) as usize - 1;
            let mut pdu = vec![0u8; pdu_len];
            socket.read_exact(&mut pdu).unwrap();

            // 构造响应:FC03 + byte_count=4 + 0x1234 + 0xABCD
            let response_pdu = vec![0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD];
            let tid = u16::from_be_bytes([header[0], header[1]]);
            let unit_id = header[6];
            let response_frame = modbus_tcp::build_mbap_frame(tid, unit_id, &response_pdu);
            socket.write_all(&response_frame).unwrap();
        });

        let mut session = Session::new();
        session
            .open_tcp("c1", "127.0.0.1", port, 1, TcpFraming::Standard)
            .unwrap();
        // 发 FC03 读 2 个保持寄存器
        let request_pdu = vec![0x03, 0x00, 0x00, 0x00, 0x02];
        let response_pdu = session.transact_tcp("c1", &request_pdu).unwrap();

        assert_eq!(response_pdu, vec![0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD]);

        handle.join().unwrap();
    }

    #[test]
    fn rtu_over_tcp_transaction_round_trip() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let port = addr.port();

        // RTU over TCP 从站:读请求数据(FC03 请求是 8 字节:unit+fc+addr+qty+crc),
        // 回送固定 RTU 响应
        let handle = std::thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            use std::io::Read;
            // FC03 读请求:unit(1)+fc(1)+start(2)+qty(2)+crc(2) = 8 字节
            let mut req = [0u8; 8];
            socket.read_exact(&mut req).unwrap();
            // 构造响应:unit=1, fc=03, byte_count=4, 0x1234, 0xABCD, CRC
            let resp =
                crate::modbus_rtu::RtuFrame::response(1, 0x03, &[0x04, 0x12, 0x34, 0xAB, 0xCD])
                    .unwrap()
                    .encode();
            socket.write_all(&resp).unwrap();
        });

        let mut session = Session::new();
        session
            .open_tcp("c1", "127.0.0.1", port, 1, TcpFraming::RtuOverTcp)
            .unwrap();
        let request_pdu = vec![0x03, 0x00, 0x00, 0x00, 0x02];
        let response_pdu = session.transact_tcp("c1", &request_pdu).unwrap();

        assert_eq!(response_pdu, vec![0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD]);

        handle.join().unwrap();
    }
}
