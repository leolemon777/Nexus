# Nexus.Robot.Abb

ABB 机器人 WebAPI 通讯客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Robot.Abb;

using var client = new AbbWebApiClient("192.168.1.100");
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("joint1");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- ABB IRC5/OmniCore 机器人 WebAPI 接口。
- 关节位置、IO 读写。
- Test coverage (12 tests).
