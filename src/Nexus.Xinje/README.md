# Nexus.Xinje

Xinje (信捷) XG/XC series Modbus-variant protocol client for Nexus.

## Quick Start

```csharp
using Nexus.Xinje;

using var client = new XinjeClient("192.168.1.100", port: 5021);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `XinjeClient` | TCP | Modbus-variant for XG/XC series |

## Features

- Read/write for D, M, X, Y, C, T registers.
- Virtual server for integration testing (37 tests).
