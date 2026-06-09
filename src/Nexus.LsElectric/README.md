# Nexus.LsElectric

LS Electric (LG) XGT protocol client for Nexus.

## Quick Start

```csharp
using Nexus.LsElectric;

using var client = new LsXgtClient("192.168.1.100", port: 20040);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `LsXgtClient` | TCP | XGT protocol for XGB/XBC series |

## Features

- Read/write for D, M, P, L, F, T, C, S areas.
- Virtual server for integration testing (29 tests).
