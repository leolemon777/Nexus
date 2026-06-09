# Nexus.Robot.Ur

Universal Robots URScript 协议客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Robot.Ur;

using var client = new UrClient("192.168.1.100", port: 30003);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- URScript 协议实现。
- UR3/UR5/UR10 系列支持。
- Test coverage (17 tests).
