# Nexus.Secs

SECS/GEM 半导体设备通讯协议客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Secs;

using var client = new HsmsClient("192.168.1.100", port: 5000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("S1F1");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- SECS-II message protocol over HSMS (High-Speed SECS Message Services)。
- GEM (Generic Equipment Model) 支持。
- Test coverage (27 tests).
