# Allen-Bradley PCCC Coverage

> Last updated: 2026-06-09

## Overview

PCCC (Programmable Controller Communications Command) is the legacy protocol for Allen-Bradley PLC-5, SLC 500, and MicroLogix controllers. It uses data file addressing instead of tag names.

## PCCC Client

```csharp
using Nexus.AllenBradley;

using var client = new PcccClient("192.168.1.20", 44818);
client.Connect();
```

## Data File Addressing

PCCC uses a `FileType:Element` addressing scheme:

| Address | File Type | File Number | Element | Description |
|---------|-----------|-------------|---------|-------------|
| `N7:0` | Integer | 7 | 0 | Integer file 7, element 0 |
| `N7:10` | Integer | 7 | 10 | Integer file 7, element 10 |
| `F8:0` | Float | 8 | 0 | Float file 8, element 0 |
| `B3:0` | Binary | 3 | 0 | Binary/Bit file 3, bit 0 |
| `T4:0` | Timer | 4 | 0 | Timer file 4, timer 0 |
| `C5:0` | Counter | 5 | 0 | Counter file 5, counter 0 |
| `L9:0` | Long | 9 | 0 | Long integer file 9 |
| `ST10:0` | String | 10 | 0 | String file 10 |
| `R6:0` | Control | 6 | 0 | Control file 6 |
| `O:0` | Output | — | 0 | Output image (O data table) |
| `I:0` | Input | — | 0 | Input image (I data table) |
| `S:0` | Status | — | 0 | Status file (S2) |

## Supported Operations

### Read/Write

```csharp
using Nexus.AllenBradley;

using var client = new PcccClient("192.168.1.20");
client.Connect();

// Read integer
var n7_0 = client.ReadInt16("N7:0");

// Write integer
client.Write("N7:0", (short)42);

// Read float
var f8_0 = client.ReadFloat("F8:0");

// Write float
client.Write("F8:0", 3.14f);

// Read boolean
var b3_0 = client.ReadBool("B3:0");

// Write boolean
client.Write("B3:0", true);
```

## PLC-5 / SLC 500 / MicroLogix Differences

| Feature | PLC-5 | SLC 500 | MicroLogix 1100/1400 |
|---------|-------|---------|----------------------|
| Transport | Ethernet (ENBT) | Serial (DF1) or Ethernet | Ethernet (built-in) |
| Default Port | 44818 | N/A (serial) | 44818 |
| Integer files | N-file | N-file | N-file |
| Float files | F-file | F-file | F-file |
| String files | ST-file | ST-file | ST-file |
| Max data files | 255 | 255 | 255 |

## Known Limitations

- PCCC does not support `IBatchReadWrite`.
- Tag-based addressing is not available (use data file addressing).
- String handling differs from CIP STRING type.
- No fragmented read/write; maximum read/write size is limited by the PCCC frame.
- Micro800 series uses CIP, not PCCC. Use `AllenBradleyCipClient` for Micro800.

## When to Use PCCC vs CIP

| PLC | Protocol | Client |
|-----|----------|--------|
| ControlLogix | CIP | `AllenBradleyCipClient` |
| CompactLogix | CIP | `AllenBradleyCipClient` |
| Micro850 / Micro870 | CIP | `AllenBradleyCipClient` |
| MicroLogix 1100 | PCCC | `PcccClient` |
| MicroLogix 1400 | PCCC | `PcccClient` |
| PLC-5 | PCCC | `PcccClient` |
| SLC 500 | PCCC | `PcccClient` |

## Virtual Server

```csharp
using var server = new PcccVirtualServer(44818);
server.Start();

using var client = new PcccClient("127.0.0.1", server.Port);
client.Connect();

client.Write("N7:0", (short)42);
var result = client.ReadInt16("N7:0");
```
