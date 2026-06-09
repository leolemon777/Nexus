# Nexus.Robot.Yaskawa

安川 (YASKAWA) 机器人 YRC1000 高速以太网通讯客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Robot.Yaskawa;

using var client = new YaskawaRobotClient("192.168.1.100", port: 10000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- YRC1000 高速以太网通讯协议。
- 机器人 I/O 和位置数据读写。
- Test coverage (11 tests).
