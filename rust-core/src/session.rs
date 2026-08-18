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
use crate::modbus_tcp::{self, MBAP_HEADER_LEN, MbapHeader, TransactionIdGenerator};
use crate::modbus_slave::SlaveMemory;

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
    Mc1eTcp {
        stream: TcpStream,
    },
    /// 西门子 PPI over TCP(串口服务器透传/仿真;双拍确认)
    PpiTcp {
        stream: TcpStream,
        /// PLC 站号(2 默认)
        station: u8,
        /// 主站(PC)站号 0
        master: u8,
    },
    /// 西门子 Fetch/Write(S5 兼容)裸 TCP 连接
    FwTcp {
        stream: TcpStream,
    },
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
        let mut stream = TcpStream::connect_timeout(
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
        use std::io::Read as _;
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
        let frame = crate::mc_frame::build_request_frame(*frame_type, route, *watchdog, req_data, *sequence);
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
        read_exact(stream, &mut header)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        // 长度字段位置:3E 在 [7..9],4E 在 [9..11]
        let len_off = header_len - 2;
        let resp_data_len =
            u16::from_le_bytes([header[len_off], header[len_off + 1]]) as usize;
        // 剩余 = 结束代码(2) + 数据区(resp_data_len - 2)
        let rest_len = resp_data_len.saturating_sub(2) + 2;
        let mut rest = vec![0u8; rest_len];
        read_exact(stream, &mut rest)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;

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
            let Connection::McTcp { route, frame_type, watchdog, sequence, ascii, .. } = conn else {
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
            let Connection::McTcp { route, frame_type, watchdog, sequence, ascii, .. } = conn else {
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
            &addr.parse().map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_read_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        stream
            .set_write_timeout(Some(TCP_READ_TIMEOUT))
            .map_err(|e| connection_failed(&addr, &e.to_string()))?;
        self.connections.insert(id.to_string(), Connection::Mc1eTcp { stream });
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
            0x01 => points as usize * 2,        // 字读
            _ => 0,                              // 写:仅 81 00
        };
        let expected = 2 + data_len;

        let conn = self.connections.get_mut(id).ok_or_else(|| connection_not_found(id))?;
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
        read_exact(stream, &mut head)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        if head[1] == 0x5B {
            // 异常详细码:5B <det> 00
            let mut det = [0u8; 2];
            read_exact(stream, &mut det)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            return Ok(vec![head[0], head[1], det[0], det[1]]);
        }
        if head[1] != 0x00 {
            return Ok(head.to_vec()); // 其他异常码:无数据
        }
        let mut rest = vec![0u8; expected - 2];
        if !rest.is_empty() {
            read_exact(stream, &mut rest)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
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
        let (peer, route, frame_type, watchdog, seq) = {
            let conn = self
                .connections
                .get_mut(id)
                .ok_or_else(|| connection_not_found(id))?;
            let Connection::McUdp { socket, peer_addr, route, frame_type, watchdog, sequence } = conn
            else {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_TYPE_MISMATCH",
                    message: format!("连接 {id} 不是 MC UDP 连接,请用 open_mc_udp 打开"),
                    details: None,
                });
            };
            *sequence = sequence.wrapping_add(1);
            let frame =
                crate::mc_frame::build_request_frame(*frame_type, route, *watchdog, req_data, *sequence);
            socket
                .send_to(&frame, *peer_addr)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            (*peer_addr, route.clone(), *frame_type, *watchdog, *sequence)
        };
        let _ = (route, watchdog);

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
        Err(connection_io_error(
            id,
            "MC UDP 连续收到序列号不匹配的响应",
        ))
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
        read_exact(stream, &mut head4)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
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
        read_exact(stream, &mut mid)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
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
        let socket =
            UdpSocket::bind("0.0.0.0:0").map_err(|e| connection_failed(&peer_addr.to_string(), &e.to_string()))?;
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
    pub fn set_connection_read_timeout_ms(&mut self, id: &str, timeout_ms: u32) -> Result<(), CoreError> {
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
        Ok(())
    }

    pub fn close_connection(&mut self, id: &str) -> Result<(), CoreError> {
        if self.connections.remove(id).is_some() {
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
                        let adu =
                            crate::modbus_rtu::RtuFrame::request(*unit_id, pdu[0], &pdu[1..])
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
            Some(Connection::FinsTcp { .. }) | Some(Connection::FinsUdp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 FINS 连接，请用 fins_read/fins_write"),
                details: None,
            }),
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
            }) => {
                match framing {
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
                        let adu =
                            crate::modbus_rtu::RtuFrame::request(*unit_id, pdu[0], &pdu[1..])
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
                }
            }
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
            Some(Connection::FinsTcp { .. }) | Some(Connection::FinsUdp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 是 FINS 连接，请用 fins_read/fins_write"),
                details: None,
            }),
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
                stream,
                framing,
                ..
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
            Some(Connection::FinsTcp { .. }) | Some(Connection::FinsUdp { .. }) => Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: "FINS 连接不支持 Modbus 站号探测".into(),
                details: None,
            }),
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
            Some(Connection::Tcp { stream, framing, .. }) => {
                match framing {
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
                }
            }
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
            }) => {
                match framing {
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
                }
            }
            _ => Err(connection_not_found(id)),
        }
    }

    /// 列出所有活跃连接的 ID。
    pub fn connection_ids(&self) -> Vec<String> {
        self.connections.keys().cloned().collect()
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
                })
            }
        };
        let ack = self.s7_transact(id, pdu)?;
        let code = crate::s7_pdu::parse_control_response(&ack)?;
        Ok((code, crate::s7_pdu::control_result_message(code).to_string()))
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

    pub fn open_ppi_tcp(&mut self, id: &str, host: &str, port: u16, station: u8) -> Result<(), CoreError> {
        let addr = format!("{host}:{port}");
        let stream = TcpStream::connect_timeout(
            &addr.parse().map_err(|_| connection_failed(&addr, "地址解析失败"))?,
            TCP_READ_TIMEOUT,
        )
        .map_err(|e| CoreError::Modbus {
            code: "S7_PPI_CONNECT_FAILED",
            message: format!("无法连接 {addr}(PPI over TCP/串口服务器透传)。{e}"),
            details: None,
        })?;
        stream.set_read_timeout(Some(TCP_READ_TIMEOUT)).ok();
        stream.set_write_timeout(Some(TCP_READ_TIMEOUT)).ok();
        self.connections.insert(id.to_string(), Connection::PpiTcp { stream, station, master: 0 });
        Ok(())
    }

    /// PPI 双拍事务:SD2 请求 → E5 → 短帧确认 → SD2 数据帧(返回内嵌 S7 Ack)。
    fn ppi_transact(&mut self, id: &str, fc: u8, s7_pdu: &[u8]) -> Result<crate::s7_pdu::S7Ack, CoreError> {
        let conn = self.connections.get_mut(id).ok_or_else(|| connection_not_found(id))?;
        let Connection::PpiTcp { stream, station, master } = conn else {
            return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 PPI 连接"),
                details: None,
            });
        };
        let frame = crate::ppi_frame::build_sd2(*station, *master, fc, s7_pdu);
        stream.write_all(&frame)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        // ① 期待 E5(读尽直到 E5;容忍串口服务器粘连)
        let mut one = [0u8; 1];
        loop {
            stream.read_exact(&mut one)
                .map_err(|e| connection_io_error(id, &e.to_string()))?;
            if one[0] == crate::ppi_frame::SC_E5 {
                break;
            }
        }
        // ② 发短帧确认
        let confirm = crate::ppi_frame::build_sa_confirm(*station, *master);
        stream.write_all(&confirm)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        // ③ 读 SD2 响应(按 LE 定长)
        let mut head = [0u8; 4];
        stream.read_exact(&mut head)
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
        stream.read_exact(&mut rest)
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        let mut full = head.to_vec();
        full.extend_from_slice(&rest);
        let (_da, _sa, _fc, resp_pdu) = crate::ppi_frame::parse_sd2(&full)?;
        crate::s7_pdu::parse_ack(&resp_pdu)
    }

    pub fn ppi_read(&mut self, id: &str, address: &str, count: u16) -> Result<Vec<crate::s7_pdu::ReadItemData>, CoreError> {
        let items = [crate::s7_pdu::S7Item::new(address, count)?];
        let pdu = crate::s7_pdu::build_read_request(0, &items)?;
        let ack = self.ppi_transact(id, crate::ppi_frame::FC_READ, &pdu)?;
        if ack.error != 0 {
            return Err(CoreError::Modbus {
                code: "S7_CPU_ERROR",
                message: format!("PPI 响应错误 0x{:04X}:{}", ack.error, crate::s7_pdu::header_error_message(ack.error)),
                details: None,
            });
        }
        crate::s7_pdu::parse_read_response(&ack)
    }

    pub fn ppi_write(&mut self, id: &str, address: &str, count: u16, data: &[u8]) -> Result<Vec<u8>, CoreError> {
        let items = [crate::s7_pdu::S7Item::new(address, count)?];
        let pdu = crate::s7_pdu::build_write_request(0, &items, &[data.to_vec()])?;
        let ack = self.ppi_transact(id, crate::ppi_frame::FC_WRITE, &pdu)?;
        if ack.error != 0 {
            return Err(CoreError::Modbus {
                code: "S7_CPU_ERROR",
                message: format!("PPI 响应错误 0x{:04X}:{}", ack.error, crate::s7_pdu::header_error_message(ack.error)),
                details: None,
            });
        }
        crate::s7_pdu::parse_write_response(&ack)
    }

    pub fn start_ppi_slave(&mut self, slave_id: &str, port: u16, seed: bool) -> Result<(), CoreError> {
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
        self.ppi_slaves.insert(slave_id.to_string(), (memory, running));
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
        self.connections.insert(id.to_string(), Connection::FwTcp { stream });
        Ok(())
    }

    pub fn fw_read(&mut self, id: &str, org: u8, db: u8, address: u16, length: u16) -> Result<Vec<u8>, CoreError> {
        let resp = self.fw_transact(id, crate::s7_fetchwrite::build_fetch(org, db, address, length), length as usize)?;
        let (_opc, err, data) = crate::s7_fetchwrite::parse_response(&resp)?;
        if err != 0 {
            return Err(CoreError::Modbus {
                code: "S7_FW_ERROR",
                message: format!("Fetch/Write 错误号 0x{err:02X}"),
                details: None,
            });
        }
        Ok(data)
    }

    pub fn fw_write(&mut self, id: &str, org: u8, db: u8, address: u16, data: &[u8]) -> Result<(), CoreError> {
        let expect = 0usize;
        let frame = crate::s7_fetchwrite::build_write(org, db, address, data);
        let resp = self.fw_transact(id, frame, expect)?;
        let (_opc, err, _) = crate::s7_fetchwrite::parse_response(&resp)?;
        if err != 0 {
            return Err(CoreError::Modbus {
                code: "S7_FW_ERROR",
                message: format!("Fetch/Write 错误号 0x{err:02X}"),
                details: None,
            });
        }
        Ok(())
    }

    fn fw_transact(&mut self, id: &str, frame: Vec<u8>, expect_data: usize) -> Result<Vec<u8>, CoreError> {
        let conn = self.connections.get_mut(id).ok_or_else(|| connection_not_found(id))?;
        let Connection::FwTcp { stream } = conn else {
            return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 Fetch/Write 连接"),
                details: None,
            });
        };
        stream.write_all(&frame)
            .and_then(|_| stream.flush())
            .map_err(|e| connection_io_error(id, &e.to_string()))?;
        crate::s7_fetchwrite::read_fw_response(&mut &*stream, expect_data)
    }

    pub fn start_fw_slave(&mut self, slave_id: &str, port: u16, seed: bool) -> Result<(), CoreError> {
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
        self.fw_slaves.insert(slave_id.to_string(), (memory, running));
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
        stream.write_all(&crate::fins_frame::build_tcp_handshake(nodes.sa1 as u16))
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
        self.connections.insert(id.to_string(), Connection::FinsTcp { stream, nodes, sid: 0 });
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
        self.connections.insert(id.to_string(), Connection::FinsUdp { socket, peer_addr, nodes, sid: 0 });
        Ok(())
    }

    fn fins_transact(&mut self, id: &str, app: Vec<u8>) -> Result<Vec<u8>, CoreError> {
        let mismatch = || CoreError::Modbus {
            code: "CONNECTION_TYPE_MISMATCH",
            message: format!("连接 {id} 不是 FINS 连接"),
            details: None,
        };
        let conn = self.connections.get_mut(id).ok_or_else(|| connection_not_found(id))?;
        match conn {
            Connection::FinsTcp { stream, sid, .. } => {
                let frame = crate::fins_frame::wrap_tcp(&app);
                stream.write_all(&frame)
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
            Connection::FinsUdp { socket, peer_addr, sid, .. } => {
                socket.send_to(&app, *peer_addr)
                    .map_err(|e| connection_io_error(id, &e.to_string()))?;
                let mut buf = [0u8; 2048];
                let (n, _) = socket.recv_from(&mut buf)
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
        let (nodes, sid) = match self.connections.get(id) {
            Some(Connection::FinsTcp { nodes, sid, .. }) | Some(Connection::FinsUdp { nodes, sid, .. }) => (nodes.clone(), *sid),
            _ => return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 FINS 连接"),
                details: None,
            }),
        };
        let frame = crate::fins_frame::build_read_frame(&nodes, sid, &addr, count);
        let resp = self.fins_transact(id, frame)?;
        let parsed = crate::fins_frame::parse_response_frame(&resp)?;
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
        let (nodes, sid) = match self.connections.get(id) {
            Some(Connection::FinsTcp { nodes, sid, .. }) | Some(Connection::FinsUdp { nodes, sid, .. }) => (nodes.clone(), *sid),
            _ => return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 FINS 连接"),
                details: None,
            }),
        };
        let frame = crate::fins_frame::build_write_frame(&nodes, sid, &addr, count, data);
        let resp = self.fins_transact(id, frame)?;
        let parsed = crate::fins_frame::parse_response_frame(&resp)?;
        Ok(parsed.end_code)
    }

    /// 启动 FINS 虚拟从站(TCP + UDP 同端口)。
    pub fn start_fins_slave(&mut self, slave_id: &str, port: u16, seed: bool) -> Result<(), CoreError> {
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
        std::thread::spawn(move || crate::fins_slave::fins_tcp_accept_loop(listener, mem_tcp, rf_tcp));
        let sock = std::net::UdpSocket::bind(format!("127.0.0.1:{port}")).map_err(|e| {
            CoreError::Modbus {
                code: "FINS_SLAVE_BIND_FAILED",
                message: format!("FINS UDP 绑定端口 {port} 失败:{e}"),
                details: None,
            }
        })?;
        let mem_u = Arc::clone(&memory);
        let rf_u = Arc::clone(&running);
        std::thread::spawn(move || crate::fins_slave::fins_udp_loop(sock, mem_u, rf_u));
        self.fins_slaves.insert(slave_id.to_string(), (memory, running));
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
        let (memory, _) = self.fins_slaves.get(slave_id).ok_or_else(|| CoreError::Modbus {
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
        let (memory, _) = self.fins_slaves.get(slave_id).ok_or_else(|| CoreError::Modbus {
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
        let cc = crate::s7_cotp::parse_cc(cc_cotp)?;

        // Setup Communication
        let pdu_req = if pdu_request == 0 { crate::s7_pdu::DEFAULT_PDU_REQUEST } else { pdu_request };
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
            Connection::S7Tcp { stream, pdu_size, pdu_ref: 1 },
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
        let conn = self.connections.get_mut(id).ok_or_else(|| connection_not_found(id))?;
        let Connection::S7Tcp { stream, pdu_ref, .. } = conn else {
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
                details: Some(serde_json::json!({ "errorClass": (ack.error >> 8) & 0xFF, "errorCode": ack.error & 0xFF })),
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
            _ => return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 S7 连接"),
                details: None,
            }),
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
            _ => return Err(CoreError::Modbus {
                code: "CONNECTION_TYPE_MISMATCH",
                message: format!("连接 {id} 不是 S7 连接"),
                details: None,
            }),
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
        self.s7_slaves.insert(slave_id.to_string(), (memory, running));
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
        let (memory, _) = self.s7_slaves.get(slave_id).ok_or_else(|| CoreError::Modbus {
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
        let (memory, _) = self.mc_slaves.get(slave_id).ok_or_else(|| CoreError::Modbus {
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
                })
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
                })
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
            _ => return Err(CoreError::Modbus {
                code: "INVALID_AREA",
                message: format!("不支持的区域:{area}"),
                details: None,
            }),
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
        let memory = self.serial_slaves.get(slave_id).cloned().ok_or_else(|| {
            CoreError::Modbus {
                code: "SLAVE_NOT_FOUND",
                message: format!("串口从站 {slave_id} 不存在"),
                details: None,
            }
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
                })
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
                stream.next_due = now + std::time::Duration::from_millis(u64::from(stream.interval_ms));
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
                })
            }
            None => {
                return Err(CoreError::Modbus {
                    code: "CONNECTION_NOT_FOUND",
                    message: format!("连接 {connection_id} 不存在"),
                    details: None,
                })
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
                let regs = crate::modbus_pdu::parse_read_holding_registers_response(&response_pdu, quantity)
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
    stream.set_read_timeout(Some(Duration::from_millis(100))).ok();
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
    stream.set_read_timeout(Some(Duration::from_millis(100))).ok();
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
fn handle_mc_ascii_request(frame: &[u8], memory: &Arc<Mutex<crate::mc_slave::McSlaveMemory>>) -> Option<Vec<u8>> {
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
            let points_start = if code_str.len() > 2 && code_str.as_bytes()[2] == b'*' { 3 } else { 2 };
            let points = u16::from_str_radix(&body[6 + points_start..6 + points_start + 4], 16).ok()?;
            let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
            let result = if is_bit { mem.get_bits(code_u8, head, points) } else { mem.get_words(code_u8, head, points) };
            let values = result.ok()?;
            // 组装 ASCII 响应
            let data_str: String = if is_bit {
                values.iter().map(|v| if *v == 1 { '1' } else { '0' }).collect()
            } else {
                values.iter().map(|v| format!("{v:04X}")).collect()
            };
            // 长度字段 = 结束码(2) + 数据二进制等效(位=点数;字=点数*2)
            let bin_data = if is_bit { values.len() } else { values.len() * 2 };
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
                data.bytes().take(count as usize).map(|b| if b == b'1' { 1 } else { 0 }).collect()
            } else {
                (0..count as usize)
                    .filter_map(|i| {
                        data.get(i * 4..i * 4 + 4).and_then(|h| u16::from_str_radix(h, 16).ok())
                    })
                    .collect()
            };
            let mut mem = memory.lock().unwrap_or_else(|e| e.into_inner());
            let result = if is_bit { mem.set_bits(code_u8, head, &values) } else { mem.set_words(code_u8, head, &values) };
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
        0x0050 | 0x00D0 => (7usize, 9usize), // 3E: 头 = 2+5+2 = 9
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
fn read_rtu_response_stream(
    stream: &mut TcpStream,
    buf: &mut Vec<u8>,
) -> std::io::Result<usize> {
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
fn read_ascii_response_stream(
    stream: &mut TcpStream,
    buf: &mut Vec<u8>,
) -> std::io::Result<usize> {
    use std::io::Read;
    buf.clear();
    let mut byte = [0u8; 1];
    loop {
        match stream.read(&mut byte) {
            Ok(0) => return Err(std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "连接关闭")),
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
            let resp = crate::modbus_rtu::RtuFrame::response(1, 0x03, &[0x04, 0x12, 0x34, 0xAB, 0xCD])
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
