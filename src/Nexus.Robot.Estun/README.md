# Nexus.Robot.Estun

埃斯顿 (Estun) 机器人 Modbus TCP 通讯客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Robot.Estun;

using var client = new EstunRobotClient("192.168.1.100", port: 502);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- 埃斯顿机器人 Modbus TCP 通讯。
- 基于 Nexus.Modbus 实现。
- Test coverage (6 tests).
