# Nexus.Schneider

Schneider Modicon M580/M340/M221 protocol client for Nexus.

## Quick Start

```csharp
using Nexus.Schneider;

using var client = new SchneiderClient("192.168.1.100", port: 5020);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("%MW100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `SchneiderClient` | TCP | Modbus-like protocol for Modicon series |

## Features

- Read/write for %MW, %M, %I, %QW areas.
- Virtual server for integration testing (36 tests).
