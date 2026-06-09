# Nexus.Robot.Yamaha

雅马哈 (YAMAHA) 机器人 RCX 通讯客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Robot.Yamaha;

using var client = new YamahaRcxClient("192.168.1.100", port: 10000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- YAMAHA RCX 控制器通讯协议。
- Test coverage (7 tests).
