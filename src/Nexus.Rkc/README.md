# Nexus.Rkc

RKC CD/CH 系列温控器通讯客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Rkc;

using var client = new RkcClient("192.168.1.100", port: 8000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadFloat("PV1");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- RKC CD/CH 系列温控器通讯。
- 当前温度 (PV)、设定温度 (SV) 读写。
- Test coverage (14 tests).
