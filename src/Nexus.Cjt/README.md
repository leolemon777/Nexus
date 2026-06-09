# Nexus.Cjt

CJT188 户用计量仪表通讯协议客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Cjt;

using var client = new CjtClient("192.168.1.100", port: 8000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("D100");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- CJT188/T 188 户用计量仪表数据读取。
- 支持水表、气表、热量表。
- Test coverage (10 tests).
