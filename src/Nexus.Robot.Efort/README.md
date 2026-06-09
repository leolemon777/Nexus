# Nexus.Robot.Efort

埃夫特 (EFORT) 机器人 ER7BC10 通讯客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Robot.Efort;

using var client = new EfortClient("192.168.1.100", port: 8000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- 埃夫特机器人 ER7BC10 协议。
- Test coverage (11 tests).
