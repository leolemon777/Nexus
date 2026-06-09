# Omron HostLink Coverage

> Last updated: 2026-06-09

## Overview

HostLink is Omron's serial protocol for PLC communication, also available over TCP (transparent serial tunnel). It uses a text-based frame format with FCS (Frame Check Sequence) checksum. Nexus provides two HostLink clients and a virtual server.

## HostLink Clients

| Client | Transport | Base | Batch | Virtual Server |
|--------|-----------|------|-------|----------------|
| `OmronHostLinkClient` | TCP | `TcpDeviceBase` | Yes | `OmronHostLinkVirtualServer` |
| `OmronHostLinkSerialClient` | Serial | `SerialDeviceBase` | No | — |

## Frame Format

HostLink frames use ASCII text:

```
@<UnitNumber><Header><Data>FCS<CR>
```

| Component | Description |
|-----------|-------------|
| `@` | Start marker |
| Unit Number | 2-digit ASCII hex (e.g., `00` for unit 0) |
| Header | Command code (e.g., `RR` for read, `WR` for write) |
| Data | Command-specific payload |
| FCS | 2-character Frame Check Sequence |
| `CR` | Carriage return (0x0D) |

## Address Format

HostLink uses the same address format as FINS. See [Address Format](address-format.md) for details.

## Supported Operations

| Operation | HostLink Command | TCP Client | Serial Client |
|-----------|-----------------|------------|---------------|
| Read single word | `RR` | Yes | Yes |
| Read multiple words | `RHR` / batch | Yes | Yes |
| Write single word | `WR` | Yes | Yes |
| Write multiple words | `WHR` / batch | Yes | Yes |
| Read bit | Bit read command | Yes | Yes |
| Write bit | Bit write command | Yes | Yes |
| Batch read | Multiple Service | Yes | No |
| Batch write | Multiple Service | Yes | No |

## Quick Start

### HostLink TCP

```csharp
using Nexus.Omron;

using var client = new OmronHostLinkClient("192.168.1.10", 9600);
client.Connect();

var value = client.ReadInt16("D100");
client.Write("D100", (short)42);
```

### HostLink Serial

```csharp
using Nexus;
using Nexus.Omron;

ISerialPort port = CreateSerialPort(); // 9600, 7E2 typical
using var client = new OmronHostLinkSerialClient(port);
client.Connect();

var value = client.ReadInt16("D100");
```

## Serial Settings

HostLink serial typically uses:

| Parameter | Value |
|-----------|-------|
| Baud Rate | 9600, 19200, or 38400 |
| Data Bits | 7 |
| Parity | Even |
| Stop Bits | 2 |

## When to Use HostLink vs FINS

| Scenario | Recommended Protocol |
|----------|---------------------|
| Modern PLC with Ethernet (CJ2/NJ/NX) | FINS TCP |
| Legacy PLC with serial only (CP1, CQM1) | HostLink Serial |
| PLC behind serial-to-Ethernet gateway | HostLink TCP |
| Need device discovery | FINS UDP |
| Need batch read/write | FINS TCP or HostLink TCP |

## Known Limitations

- HostLink Serial does not support `IBatchReadWrite`.
- HostLink frame size is limited compared to FINS binary.
- Text-based framing adds overhead; FINS binary is faster for large data transfers.
