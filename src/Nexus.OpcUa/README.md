# Nexus.OpcUa

OPC UA client for Nexus (experimental).

## Quick Start

```csharp
using Nexus.OpcUa;

using var client = new OpcUaClient("opc.tcp://192.168.1.100:4840");
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt32("ns=2;s=Temperature");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- OPC UA binary protocol over TCP.
- Node ID based read/write.
- Session management.
- **Status: Experimental** — not yet production-ready.
