# Nexus IEC 104

Nexus IEC 104 covers IEC 60870-5-104 protocol for telecontrol and SCADA communication over TCP.

## Client

| Client | Transport | Base | Default Port | Notes |
|--------|-----------|------|--------------|-------|
| `Iec104Client` | TCP | `TcpDeviceBase` | 2404 | Primary client. Persistent connection. Supports `IBatchReadWrite`, `ISubscribeDevice`. |
| `Iec104ConnectionPool` | Pool wrapper | `ConnectionPool<T>` | — | Thread-safe persistent connection pool. |
| `Iec104VirtualServer` | TCP server | standalone | configurable | Virtual outstation for integration testing. |

## ASDU Type IDs

### Monitoring

| TypeId | Value | Description |
|--------|-------|-------------|
| `M_SP_NA_1` | 1 | Single-point information |
| `M_DP_NA_1` | 3 | Double-point information |
| `M_ME_NA_1` | 9 | Measured value, normalized |
| `M_ME_NC_1` | 13 | Measured value, short floating point |
| `M_IT_NA_1` | 15 | Integrated totals (counter) |

### Commands

| TypeId | Value | Description |
|--------|-------|-------------|
| `C_SC_NA_1` | 45 | Single command |
| `C_DC_NA_1` | 46 | Double command |
| `C_SE_NA_1` | 48 | Set-point command, normalized |

### System

| TypeId | Value | Description |
|--------|-------|-------------|
| `C_IC_NA_1` | 100 | Interrogation command (GI) |
| `C_CI_NA_1` | 101 | Counter interrogation |
| `C_RD_NA_1` | 102 | Read command |
| `C_CS_NA_1` | 103 | Clock sync command |
| `C_TS_TA_1` | 104 | Test command with time tag |

## Address Format

Pattern: `{prefix}:{ioa}` where IOA is the information object address.

| Prefix | PointType | Read | Write |
|--------|-----------|------|-------|
| `SP` | SinglePoint | bool | — |
| `DP` | DoublePoint | bool | — |
| `MN` | MeasuredNormalized | float | — |
| `MF` | MeasuredFloat | float | — |
| `SC` | SingleCommand | — | bool |
| `DC` | DoubleCommand | — | bool |
| `SN` | SetpointNormalized | — | float |

Examples: `"SP:100"`, `"MF:200"`, `"SC:1"`, `"SN:50"`

Bare integer (e.g. `"42"`) defaults to MeasuredFloat.

## General Interrogation (GI)

```csharp
// Station-wide GI
var result = client.SendGeneralInterrogation();

// Group GI (groups 1-16)
var result = client.SendGeneralInterrogation(1);
```

GI sends `C_IC_NA_1` with QOI=20 (station) or QOI=group (1-31). Waits for activation confirmation, processes monitoring data, and completes on activation termination (COT=10).

## Clock Synchronization

```csharp
var result = client.SynchronizeClock();
if (result.IsSuccess)
    Console.WriteLine($"Server time: {result.Content}");
```

Sends `C_CS_NA_1` with CP56Time2a-encoded `DateTime.UtcNow`. Returns the server-echoed time.

## Quick Start

```csharp
using Nexus.Iec104;

using var client = new Iec104Client("192.168.1.10", 2404);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

// Send GI to load all monitoring data
client.SendGeneralInterrogation();

// Read a float measurement
var value = client.ReadFloat("MF:200");

// Send a single command
client.Write("SC:1", true);

// Synchronize clock
client.SynchronizeClock();
```

## APCI Framing

| Frame Type | Description |
|------------|-------------|
| I-frame | Carries ASDU, has send/receive sequence numbers (15-bit) |
| S-frame | Acknowledges received I-frames |
| U-frame | STARTDT/STOPDT/TESTFR control |

## Timeout Parameters

| Timer | Default | Purpose |
|-------|---------|---------|
| T0 | 30s | Connection establishment |
| T1 | 15s | Command response timeout |
| T2 | 10s | S-frame send timeout |
| T3 | 20s | Idle link test (triggers TESTFR) |

## Limitations

- String/byte[] read/write not supported
- No time-tagged monitoring types (M_SP_TB_1, etc.)
- No bitstring command (C_BO_NA_1)
- Async methods are synchronous wrappers
