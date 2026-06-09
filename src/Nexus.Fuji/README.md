# Nexus.Fuji

Fuji SPH/SPB series PLC protocol client for Nexus.

## Quick Start

```csharp
using Nexus.Fuji;

using var client = new FujiClient("192.168.1.100", port: 9000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `FujiClient` | TCP | SPH/SPB series PLC |

## Features

- Read/write for D, X, Y, M registers.
- Configurable station and timeout.
- Virtual server for integration testing (54 tests).
