# Nexus IEC 61850

Nexus IEC 61850 covers MMS (Manufacturing Message Specification) communication over TCP for IEC 61850 IEDs (Intelligent Electronic Devices) in substation automation.

## Client

| Client | Transport | Base | Default Port | Notes |
|--------|-----------|------|--------------|-------|
| `Iec61850Client` | TCP (MMS/ISO 8073) | `TcpDeviceBase` | 102 | Primary client. Supports `IBatchReadWrite`, `ISubscribeDevice`. |
| `Iec61850ConnectionPool` | Pool wrapper | `ConnectionPool<T>` | — | Thread-safe persistent connection pool. |
| `Iec61850VirtualServer` | TCP server | standalone | configurable | Virtual IED for integration testing. |

## MMS Model

IEC 61850 data is organized in a hierarchical model:

```
Server
  └─ Logical Device (LD)
       └─ Logical Node (LN)
            └─ Data Object (DO)
                 └─ Data Attribute (DA)
```

### Functional Constraints (FC)

| FC | Byte | Usage |
|----|------|-------|
| ST | 0x01 | Status information |
| MX | 0x02 | Measurands (default for reads) |
| SP | 0x03 | Setpoints (default for writes) |
| SV | 0x04 | Substitution values |
| CF | 0x05 | Configuration |
| DC | 0x06 | Description |
| SG | 0x07 | Setting groups |
| SE | 0x08 | Setting group editing |
| CO | 0x0D | Control |

## Address Format

Object references follow the standard `LD/LN.DO[.DA]` pattern:

```
LD0/LLN0.Beh
LD0/GGIO1.Ind1.stVal
LD0/MMXU1.TotW
LD0/XCBR1.Pos.Oper
```

## Data Browsing

| Method | Returns |
|--------|---------|
| `GetServerDirectory()` | List of logical device names |
| `GetLogicalDeviceDirectory(ld)` | List of logical node names |
| `GetLogicalNodeDirectory(ld, ln)` | List of data object names |
| `GetDataDirectory(objectRef)` | List of data attribute names |

## Quick Start

```csharp
using Nexus.Iec61850;

using var client = new Iec61850Client("192.168.1.10", 102);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

// Browse server directory
var devices = client.GetServerDirectory();
if (devices.IsSuccess)
    foreach (var ld in devices.Content)
        Console.WriteLine($"LD: {ld}");

// Read a measurement
var value = client.ReadFloat("LD0/MMXU1.TotW");

// Read with timestamp and quality
var ts = client.ReadTimestamped("LD0/GGIO1.Ind1", FunctionalConstraint.ST);

// Write a setpoint
client.Write("LD0/GGIO1.AnOut1", 42.0f);

// Control operation (Select-Before-Operate)
client.Select("LD0/XCBR1.Pos");
client.Operate("LD0/XCBR1.Pos", true);
```

## Control Operations

| Method | Description |
|--------|-------------|
| `Select(objectRef)` | Acquire control lock |
| `Operate(objectRef, value)` | Execute control command |
| `Cancel(objectRef)` | Release control selection |

Supported control models: DirectWithNormalSecurity, SboWithNormalSecurity, DirectWithEnhancedSecurity, SboWithEnhancedSecurity.

## Report Control Blocks

| Method | Description |
|--------|-------------|
| `EnableReports(rcbRef, datasetRef)` | Enable a report control block |
| `DisableReports(rcbRef)` | Disable a report control block |

## Quality Stamps

QualityStamp is a flags enum: Valid, Overflow, OutOfRange, BadReference, Oscillatory, Failure, OldData, Inconsistent, Inaccurate, Substituted, Test, Blocked.

## Limitations

- No GOOSE/SV pub-sub (defined in enums only)
- No full ASN.1 BER encoding (uses simplified custom framing)
- String/byte[] read/write returns failure
- Async methods are synchronous wrappers
