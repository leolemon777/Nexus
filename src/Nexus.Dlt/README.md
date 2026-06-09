# Nexus.Dlt

DLT645/698 电力仪表通讯协议客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Dlt;

using var client = new Dlt645Client("192.168.1.100");
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- DLT645-2007 多功能电能表通讯。
- DLT698.45 面向对象协议。
- Test coverage (17 tests).
