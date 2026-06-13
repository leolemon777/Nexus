# Modbus Diagnostics

> Last updated: 2026-06-13

`ModbusDiagnostics` (`Nexus.Modbus`) is a static utility for parsing raw Modbus frames into human-readable descriptions and translating exception codes.

## Quick Start

```csharp
using Nexus.Modbus;

// Parse a TCP request
var desc = ModbusDiagnostics.ParseMessage("00010000000601030000000A", ModbusProtocol.Tcp);
Console.WriteLine(desc);

// Parse RTU
var rtu = ModbusDiagnostics.ParseMessage("01030000000AC5CD", ModbusProtocol.Rtu);

// Translate exception code
var err = ModbusDiagnostics.TranslateException(0x02);
Console.WriteLine(err); // "非法数据地址"

// Format request/response pair
var formatted = ModbusDiagnostics.FormatTransaction(requestBytes, responseBytes, ModbusProtocol.Tcp);
```

## API Reference

| Method | Parameters | Returns |
|--------|------------|---------|
| `ParseMessage(string hex, protocol)` | Hex string + protocol enum | Human-readable description |
| `ParseMessage(byte[] data, protocol)` | Raw bytes + protocol enum | Human-readable description |
| `TranslateException(byte code)` | Exception code (0x01-0x0B) | Chinese description string |
| `FormatTransaction(req, resp, protocol)` | Request bytes, response bytes, protocol | Formatted pair |

## Supported Protocols

| Protocol | Header | Validation |
|----------|--------|------------|
| `ModbusProtocol.Tcp` | MBAP (TxId, ProtocolId, Length, UnitId) | — |
| `ModbusProtocol.Rtu` | Station address | CRC16 validation |
| `ModbusProtocol.Ascii` | `:` framing | LRC validation |
| `ModbusProtocol.RtuOverTcp` | Station + CRC over TCP | CRC16 validation |

## Supported Function Codes

| FC | Description |
|----|-------------|
| 01 | Read Coils |
| 02 | Read Discrete Inputs |
| 03 | Read Holding Registers |
| 04 | Read Input Registers |
| 05 | Write Single Coil |
| 06 | Write Single Register |
| 08 | Diagnostics (14 sub-functions) |
| 15 | Write Multiple Coils |
| 16 | Write Multiple Registers |
| 22 | Mask Write Register |
| 23 | Read/Write Multiple Registers |
| 43 | Encapsulated Interface / Read Device ID |

## Exception Codes

| Code | Description |
|------|-------------|
| 0x01 | Illegal function code |
| 0x02 | Illegal data address |
| 0x03 | Illegal data value |
| 0x04 | Slave device failure |
| 0x05 | Acknowledge (processing) |
| 0x06 | Slave device busy |
| 0x07 | Negative acknowledge |
| 0x08 | Memory parity error |
| 0x0A | Gateway path unavailable |
| 0x0B | Gateway target device failed |

## FC08 Diagnostics Sub-functions

| Sub-function | Description |
|--------------|-------------|
| 0x0000 | Return Query Data |
| 0x0001 | Restart Communications |
| 0x000A | Clear Counters |
| 0x000B | Return Bus Message Count |
| 0x000C | Return Bus Communication Error Count |
| 0x000D | Return Bus Exception Error Count |
| 0x000E | Return Server Message Count |
| 0x000F | Return Server No Response Count |
| 0x0010 | Return Server NAK Count |
| 0x0011 | Return Server Busy Count |
| 0x0012 | Return Bus Character Overrun Count |
| 0x0014 | Clear Overrun Counter |

## FC43 Read Device ID

Parses MEI type, read level (Basic/Regular/Extended/Obsoleted), start object ID, conformity level, object count, and data hex.
