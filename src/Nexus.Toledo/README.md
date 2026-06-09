# Nexus.Toledo

梅特勒-托利多 (Mettler Toledo) 电子秤通讯客户端 for Nexus。

## Quick Start

```csharp
using Nexus.Toledo;

using var client = new ToledoClient("192.168.1.100", port: 8000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadFloat("Weight");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- 梅特勒-托利多电子秤标准通讯协议。
- 重量数据读取。
- Test coverage (10 tests).
