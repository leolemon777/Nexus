# Nexus Schneider Modicon

Nexus Schneider covers Modbus TCP communication with Schneider Electric Modicon PLC families using IEC 61131-3 style addressing.

## Client

| Client | Transport | Base | Default Port | Notes |
|--------|-----------|------|--------------|-------|
| `SchneiderModiconClient` | TCP (MBAP) | `TcpDeviceBase` | 502 | Primary client. Supports `IBatchReadWrite`, `ISubscribeDevice`. |
| `SchneiderConnectionPool` | Pool wrapper | `ConnectionPool<T>` | — | Thread-safe persistent connection pool. |
| `SchneiderVirtualServer` | TCP server | standalone | 5020 | Virtual PLC for integration testing. |

## Feature Summary

| Feature | Status |
|---------|--------|
| FC01 Read Coils | Yes |
| FC02 Read Discrete Inputs | Yes |
| FC03 Read Holding Registers | Yes |
| FC04 Read Input Registers | Yes |
| FC05 Write Single Coil | Yes |
| FC06 Write Single Register | Yes |
| FC15 Write Multiple Coils | Yes |
| FC16 Write Multiple Registers | Yes |
| `IBatchReadWrite` | Yes |
| `ISubscribeDevice` | Yes |
| PLC diagnostics | Yes |
| Byte order option | Yes |

## Address Format (IEC 61131-3 Modicon)

Addresses use the `%` prefix with area letter and index. The `%` prefix is optional; addresses are case-insensitive.

| Address | Area | Function Code | Description |
|---------|------|---------------|-------------|
| `%MW<n>` | Internal Word | FC03 | Holding registers |
| `%M<n>` | Internal Bit | FC01 | Coils |
| `%M<w>.<b>` | Internal Bit | FC01 | Bit by word.bit |
| `%IW<n>` | Input Word | FC04 | Input registers |
| `%I<w>.<b>` | Input Bit | FC02 | Discrete inputs |
| `%QW<n>` | Output Word | FC03 | Output registers (offset 0x0600) |
| `%Q<w>.<b>` | Output Bit | FC01 | Output bits |
| `%KW<n>` | Constant Word | FC03 | Constants (offset 0x0800) |
| `%SW<n>` | System Word | FC03 | System status (offset 0x0400) |
| `%S<n>` | System Bit | FC01 | System status bits |

## Supported PLC Models

| Model | Series |
|-------|--------|
| M580 | ePAC |
| M340 | — |
| M221 | — |
| M241 | — |
| M251 | — |
| M262 | — |
| M380 | — |
| Premium | TSX |
| Quantum | — |

## Quick Start

```csharp
using Nexus.Schneider;

using var client = new SchneiderModiconClient("192.168.1.10", 502);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

// Read %MW0 as Int16
var value = client.ReadInt16("%MW0");
if (value.IsSuccess)
    Console.WriteLine($"%MW0 = {value.Content}");

// Write %MW10
client.Write("%MW10", (short)1234);

// Read %M0 as bool
var coil = client.ReadBool("%M0");

// Read diagnostics
var diag = client.ReadDiagnostics();
```

## PLC Diagnostics

| Method | Returns | Description |
|--------|---------|-------------|
| `ReadPlcInfo()` | `SchneiderPlcInfo` | Device type, firmware, hardware version, status word |
| `ReadDiagnostics()` | `SchneiderDiagnostics` | Comm errors, CRC errors, timeouts, exceptions, run mode |
| `ReadSystemWord(offset)` | `short` | Read `%SW<offset>` |
| `ReadSystemBit(index)` | `bool` | Read `%S<index>` |

## Related Pages

- [Modbus Protocol Family](../modbus/index.md)
