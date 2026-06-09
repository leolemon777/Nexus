# Nexus.Robot.Staubli

Staubli 机器人 VAL3 协议客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Robot.Staubli;

using var client = new StaubliClient("192.168.1.100", port: 5656);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("joint1");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- VAL3 协议实现。
- Test coverage (15 tests).
