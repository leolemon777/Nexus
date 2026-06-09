# Nexus Modbus

Nexus Modbus is the reference protocol family for the first production-readiness pass. It currently covers TCP, UDP, RTU, ASCII, ASCII-over-TCP, RTU-over-TCP, TCP server support, address prefixes, byte order options, batch reads, custom function codes, and basic polling subscriptions for TCP/UDP clients.

## Clients

| Client | Transport | Base | Current Role |
|--------|-----------|------|--------------|
| `ModbusTcpClient` | TCP with MBAP header | `TcpDeviceBase` | Main reference client. |
| `ModbusUdpClient` | UDP with MBAP header | `UdpDeviceBase` | UDP and broadcast-oriented scenarios. |
| `ModbusRtuClient` | Serial RTU with CRC16 | `SerialDeviceBase` | RS485/RS232 RTU scenarios. |
| `ModbusAsciiClient` | Serial ASCII with LRC | `SerialDeviceBase` | ASCII framed serial scenarios. |
| `ModbusAsciiOverTcpClient` | ASCII frame over TCP | `ModbusAsciiClient` + TCP stream adapter | Ethernet gateways that expose Modbus ASCII framing. |
| `ModbusRtuOverTcpClient` | RTU ADU over TCP | `TcpDeviceBase` | DTU and transparent gateway scenarios. |
| `ModbusTcpServer` | TCP server | standalone | Local integration tests and WPF simulator support. |
| `ModbusVirtualServer` | TCP server | standalone | Virtual-server utility with write callbacks. |

## Feature Summary

| Feature | TCP | UDP | RTU | ASCII | ASCII-over-TCP | RTU-over-TCP |
|---------|-----|-----|-----|-------|----------------|--------------|
| FC01 read coils | Yes | Yes | Yes | Yes | Yes | Yes |
| FC02 read discrete inputs | Yes | Yes | Yes | Yes | Yes | Yes |
| FC03 read holding registers | Yes | Yes | Yes | Yes | Yes | Yes |
| FC04 read input registers | Yes | Yes | Yes | Yes | Yes | Yes |
| FC05 write single coil | Yes | Yes | Yes | Yes | Yes | Yes |
| FC06 write single register | Yes | Yes | Yes | Yes | Yes | Yes |
| FC15 write multiple coils | Yes | Yes | Yes | Yes | Yes | Yes |
| FC16 write multiple registers | Yes | Yes | Yes | Yes | Yes | Yes |
| FC22 mask write register | Yes | Yes | Yes | Yes | Yes | Yes |
| FC23 read/write multiple registers | Yes | Yes | Yes | Yes | Yes | Yes |
| Prefix addresses | Yes | Yes | Yes | Yes | Yes | Yes |
| Byte order option | Yes | Yes | Yes | Yes | Yes | Yes |
| Encoded strings | Yes | Yes | Partial | Yes | Yes | Yes |
| `IBatchReadWrite` | Yes | Yes | No | No | No | No |
| `ISubscribeDevice` | Yes | Yes | No | No | No | No |
| Custom function code | Yes | Yes | Yes | Yes | Yes | Yes |

## Production Gate

Modbus is the first `Production Candidate`, but it still needs these release artifacts before public production claims:

1. ~~Real-device validation entries in `REAL_DEVICE_VALIDATION.md`.~~ Target rows added; real evidence pending.
2. ~~Benchmark and long-run test notes.~~ Done: `performance.md` and `long-run.md`.
3. Gateway/DTU field notes for RTU-over-TCP.
4. Extended function-code coverage for diagnostics, file records, FIFO, and device identification.

## Related Pages

- [Quickstart](quickstart.md)
- [Complete Scope](complete-scope.md)
- [Address Format](address-format.md)
- [Function Codes](function-codes.md)
- [Byte Order](byte-order.md)
- [Packet Logging](packet-logging.md)
- [Performance](performance.md)
- [Long-Run Stability](long-run.md)
- [Troubleshooting](troubleshooting.md)
