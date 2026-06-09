# Nexus.Bacnet

BACnet/IP protocol client for building automation systems.

## Quick Start

```csharp
using Nexus.Bacnet;

using var client = new BacnetIpClient("192.168.1.100", port: 47808);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt32("1:0.AnalogInput.0.PresentValue");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `BacnetIpClient` | UDP | BACnet/IP — BVLC, Who-Is/I-Am, ReadProperty, WriteProperty, SubscribeCOV |

## Features

- BACnet/IP BVLC layer.
- Who-Is/I-Am device discovery.
- ReadProperty / WriteProperty.
- SubscribeCOV change notifications.
- Test coverage (40 tests).
