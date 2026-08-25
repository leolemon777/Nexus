# Modbus 软件黄金向量与命令矩阵

日期：2026-08-23  
证据等级：S2/S3（Rust PDU/RTU/ASCII/TCP/UDP 编解码、虚拟从站、JSONL、Electron 路由）；**不是**真实 PLC/仪表 L2。

主站 UI 必须按传输选择命令族，禁止把 UDP 当成 TCP，也禁止把串口 FC01/FC02 当成 FC03。

## 1. 命令矩阵（8 功能码 × 6 传输）

| 功能码 | RTU/ASCII 串口 | TCP / RTU-over-TCP / ASCII-over-TCP | UDP |
|---|---|---|---|
| FC01 | `read_coils_once` | `tcp_read_coils` | `udp_read_coils` |
| FC02 | `read_discrete_inputs_once` | `tcp_read_discrete_inputs` | `udp_read_discrete_inputs` |
| FC03 | `read_holding_registers_once` | `tcp_read_holding_registers` | `udp_read_holding_registers` |
| FC04 | `read_input_registers_once` | `tcp_read_input_registers` | `udp_read_input_registers` |
| FC05 | `write_single_coil_once` | `tcp_write_single_coil` | `udp_write_single_coil` |
| FC06 | `write_single_register_once` | `tcp_write_single_register` | `udp_write_single_register` |
| FC15 | `write_multiple_coils_once` | `tcp_write_multiple_coils` | `udp_write_multiple_coils` |
| FC16 | `write_multiple_registers_once` | `tcp_write_multiple_registers` | `udp_write_multiple_registers` |

连接：

| 传输 | 命令 | framing |
|---|---|---|
| TCP | `open_tcp_connection` | `standard` |
| UDP | `open_udp_connection` | `standard`（不得落到 `ascii-over-tcp`） |
| RTU-over-TCP | `open_tcp_connection` | `rtu-over-tcp` |
| ASCII-over-TCP | `open_tcp_connection` | `ascii-over-tcp` |
| RTU / ASCII 串口 | 主站页 `open` COM | Electron 持句柄 |

UDP 走 `tcp_*` 时 Rust 返回 `CONNECTION_TYPE_MISMATCH`。未知传输/功能码必须 fail-closed，不得默认为 FC03。

## 2. 黄金帧

站号 1、起始地址 0、数量 2 的 FC03 RTU 请求（Rust `build_read_holding_registers`）：

```text
TX 01 03 00 00 00 02 C4 0B
```

正常响应，两寄存器 `0x1234`、`0xABCD`，CRC 低字节在前：

```text
RX 01 03 04 12 34 AB CD + CRC16/MODBUS(LE)
```

异常 02（非法数据地址），5 字节即结束，不得再等正常长度：

```text
RX 01 83 02 + CRC16
```

数量 10 的规范请求（`READ_TEN_HOLDING_REGISTERS`）：

```text
01 03 00 00 00 0A C5 CD
```

TCP 使用 MBAP + 同一 PDU，不带 CRC。UDP 使用同一 MBAP/PDU 封装，但必须调用 `udp_*`。ASCII 使用 `:` + HEX + LRC + CRLF。

## 3. 安全写与扫描

- 写前读取旧值 → 确认框显示传输/站号/地址/旧值/新值 → 写入 → 同地址回读 → `logs/write-audit.jsonl`。
- 广播站号 0 因无法旧值/回读被阻止。
- 扫描：可取消、firstHit/full、不漏句柄；TCP 扫描走 Rust，串口扫描走 Electron。

## 4. 只读与受控写步骤

1. 选传输并连接（串口先开 COM；网口点连接）。
2. 只读：FC01–04，核对应答长度、CRC/LRC/MBAP、异常码。
3. 受控写：仅在确认框同意后发送 FC05/06/15/16，核对应答与回读。
4. 禁止把虚拟从站结果写成现场 L2。

## 5. 关联测试

- `src/modbus-route.js`、`electron/modbus-route.test.cjs`
- `electron/modbus-master-service.test.cjs`、`electron/modbus-scan-service.test.cjs`
- `rust-core/src/modbus_rtu.rs`、`rust-core/src/modbus_pdu.rs`
