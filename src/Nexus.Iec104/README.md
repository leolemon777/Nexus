# Nexus.Iec104

IEC 60870-5-104 protocol client for power/energy SCADA systems.

## Quick Start

```csharp
using Nexus.Iec104;

using var client = new Iec104Client("192.168.1.100", port: 2404);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("M_SP_NA_1:1");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `Iec104Client` | TCP | IEC 60870-5-104 for power SCADA |

## Features

- I-frame, S-frame, U-frame support.
- Interrogation and counter interrogation commands.
- Single/Double point information, measured values.
- Test coverage (22 tests).
