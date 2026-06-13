# Nexus DNP3

Nexus DNP3 covers DNP3 (Distributed Network Protocol) over TCP for SCADA and utility automation scenarios.

## Client

| Client | Transport | Base | Default Port | Notes |
|--------|-----------|------|--------------|-------|
| `Dnp3Client` | TCP | `TcpDeviceBase` | 20000 | Primary client. Supports `IBatchReadWrite`, `ISubscribeDevice`. |
| `Dnp3ConnectionPool` | Pool wrapper | `ConnectionPool<T>` | — | Thread-safe persistent connection pool. |
| `Dnp3VirtualServer` | TCP server | standalone | 20000 | Virtual outstation for integration testing. |

## Feature Summary

| Feature | Status |
|---------|--------|
| Analog Input read (Group 30) | Yes |
| Binary Input read (Group 1) | Yes |
| Counter read (Group 20) | Yes |
| Analog Output read/write (Group 40) | Yes |
| Direct Operate (binary/analog) | Yes |
| Select-Before-Operate | Yes |
| Cold Restart | Yes |
| Delay Measure | Yes |
| `IBatchReadWrite` | Yes |
| `ISubscribeDevice` | Yes |
| String read/write | Not supported |

## Address Format

DNP3 uses a simple prefix + index format:

| Prefix | Group | Description |
|--------|-------|-------------|
| `AI<n>` | 30 | Analog Input |
| `BI<n>` | 1 | Binary Input |
| `AO<n>` | 40 | Analog Output |
| `C<n>` | 20 | Counter |

Examples: `"AI0"`, `"BI1"`, `"AO5"`, `"C10"`

## Quick Start

```csharp
using Nexus.Dnp3;

using var client = new Dnp3Client("192.168.1.10", 20000);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

// Read analog inputs
var values = client.ReadAnalogInputs(0, 10);
if (values.IsSuccess)
{
    for (int i = 0; i < values.Content.Length; i++)
        Console.WriteLine($"AI{i} = {values.Content[i]}");
}

// Read binary inputs
var bits = client.ReadBinaryInputs(0, 16);

// Direct operate on analog output
client.DirectOperateAnalog(0, 3.14f);

// Select-before-operate on binary output
client.SelectBeforeOperateBinary(0, true);
```

## DNP3 Layer Architecture

| Layer | Description |
|-------|-------------|
| Data Link | Start bytes `0x05 0x64`, CRC-16/DNP3 (polynomial `0xA6BC`), source/dest addressing |
| Transport | FIR/FIN bits, 6-bit sequence number |
| Application | PDU builders for each function code, invoke ID tracking |

## Limitations

- No serial (EIA-485) client — TCP only
- String read/write not supported
- No Freeze/WarmRestart/EnableUnsolicited function codes
- No Secure Authentication (Group 120)
- Batch operations are sequential per-address (not multi-object optimized)
