# Nexus.Beckhoff

Beckhoff TwinCAT ADS protocol client for Nexus.

## Quick Start

```csharp
using Nexus.Beckhoff;

using var client = new BeckhoffAdsClient("192.168.1.100", port: 48898);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("Main.var1");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `BeckhoffAdsClient` | TCP | TwinCAT ADS protocol, IBatchReadWrite |

## Features

- Read/write for ADS symbol addresses.
- Batch read/write through `IBatchReadWrite`.
- Test coverage (14 tests).
