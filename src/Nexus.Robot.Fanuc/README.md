# Nexus.Robot.Fanuc

FANUC 机器人 SocketMessage 通讯客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Robot.Fanuc;

using var client = new FanucRobotClient("192.168.1.100", port: 60008);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- FANUC 机器人 SocketMessage 通讯。
- Test coverage (10 tests).
