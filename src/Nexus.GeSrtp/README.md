# Nexus.GeSrtp

GE 90-30/70/PACSystems SRTP protocol client for Nexus.

## Quick Start

```csharp
using Nexus.GeSrtp;

using var client = new GeSrtpClient("192.168.1.100", port: 18245);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("R100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `GeSrtpClient` | TCP | GE SRTP for 90-30/70/PACSystems |

## Features

- Read/write for R, I, Q, AI, AQ, M, T, C areas.
- Virtual server for integration testing (56 tests).
