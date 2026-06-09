# Nexus.Delta

Delta DVP/AS series PLC protocol client for Nexus.

## Quick Start

```csharp
using Nexus.Delta;

using var client = new DeltaDvpClient("192.168.1.100", port: 5020);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `DeltaDvpClient` | TCP | DVP/AS series Modbus-like protocol |

## Features

- Read/write for D, M, X, Y, T, C registers.
- Virtual server for integration testing (42 tests).
