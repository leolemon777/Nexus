# Nexus.Panasonic

Panasonic Mewtocol protocol client for FP series PLCs.

## Quick Start

```csharp
using Nexus.Panasonic;

using var client = new PanasonicMewtocolClient("192.168.1.100", port: 9094);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("DT100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `PanasonicMewtocolClient` | TCP | Mewtocol for FP0/FP1/FP-X/FP7 series, IBatchReadWrite |

## Features

- Read/write for DT, WR, X, Y, R areas.
- Batch read/write through `IBatchReadWrite`.
- Test coverage (19 tests).
