# Nexus.Fanuc

FANUC FOCAS/Ethernet protocol client for Nexus.

## Quick Start

```csharp
using Nexus.Fanuc;

using var client = new FanucFocasClient("192.168.1.100", port: 8193);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `FanucFocasClient` | TCP | FOCAS Ethernet for CNC/robot controllers |

## Features

- Read/write for CNC data areas.
- Virtual server for integration testing (26 tests).
