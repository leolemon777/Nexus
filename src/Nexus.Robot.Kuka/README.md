# Nexus.Robot.Kuka

KUKA 机器人 TCP/VarProxy 通讯客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Robot.Kuka;

using var client = new KukaVarProxyClient("192.168.1.100", port: 7000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("$POS_ACT.X");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- KUKA VarProxy TCP 协议。
- 机器人变量读写。
- Test coverage (16 tests).
