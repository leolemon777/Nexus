# Nexus.Keyence

Keyence KV series upper communication protocol client for Nexus.

## Quick Start

```csharp
using Nexus.Keyence;

using var client = new KeyenceKvClient("192.168.1.100", port: 5022);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("DM100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `KeyenceKvClient` | TCP | KV series upper communication, IBatchReadWrite |

## Features

- Read/write for DM, R, B, MR, VR, ZR, TN, CN areas.
- Batch read/write through `IBatchReadWrite`.
- Virtual server for integration testing (21 tests).
