# Nexus.Kuka

KUKA Robot EKI (Ethernet KRL Interface) protocol client for Nexus.

## Quick Start

```csharp
using Nexus.Kuka;

using var client = new KukaEkiClient("192.168.1.100", port: 54601);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `KukaEkiClient` | TCP | EKI protocol for KUKA robots |

## Features

- Read/write for robot memory variables.
- Virtual server for integration testing (21 tests).
