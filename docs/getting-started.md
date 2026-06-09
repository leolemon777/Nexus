# 快速开始

5 分钟完成首次 PLC 数据读取。

## 1. 安装 NuGet 包

```bash
# 以 Modbus TCP 为例
dotnet add package Nexus.Modbus
```

## 2. 创建客户端

```csharp
using Nexus.Modbus;

var client = new ModbusTcpClient("192.168.1.10", port: 502, station: 1);
```

所有协议客户端都遵循相同的模式：

```csharp
// Siemens S7
var s7 = new SiemensS7Client("192.168.1.20", rack: 0, slot: 1);

// Mitsubishi MC3E
var mitsu = new Mc3EBinaryClient("192.168.1.30", port: 6000);

// Omron FINS
var omron = new FinsTcpClient("192.168.1.40", port: 9600);

// Schneider Modicon
var schneider = new SchneiderModiconClient("192.168.1.50", port: 502);
```

## 3. 读取数据

所有客户端实现 `IReadWriteDevice` 接口，API 完全一致：

```csharp
// 读取 Int16
var result = client.ReadInt16("40001");
if (result.IsSuccess)
    Console.WriteLine($"读取成功: {result.Content}");
else
    Console.WriteLine($"读取失败: {result.Message}");

// 读取其他类型
client.ReadBool("0");           // 线圈
client.ReadUInt16("40001");     // 无符号 16 位
client.ReadInt32("40001");      // 32 位整数
client.ReadFloat("40001");      // 浮点数
client.ReadString("40001", 10); // 字符串
```

> **重要**: `OperateResult<T>.Content` 对于数值类型是值类型（不是引用类型），请勿使用 `?.` 操作符。

## 4. 写入数据

```csharp
client.Write("40001", (short)1234);
client.Write("0", true);            // 写线圈
client.Write("40001", 3.14f);       // 写浮点数
client.Write("40001", "hello");     // 写字符串
```

## 5. 批量读写

支持 `IBatchReadWrite` 接口的协议可以一次读写多个地址：

```csharp
if (client is IBatchReadWrite batch)
{
    var values = batch.BatchRead(new[] { "40001", "40002", "40003" });
    if (values.IsSuccess)
    {
        foreach (var kv in values.Content)
            Console.WriteLine($"{kv.Key} = {kv.Value}");
    }

    batch.BatchWrite(new Dictionary<string, object>
    {
        ["40001"] = (short)100,
        ["40002"] = (short)200,
    });
}
```

## 6. 连接管理

Nexus 使用自动连接模式 — 首次读写时自动建立连接，无需手动 `Connect()`：

```csharp
// 自动连接模式（推荐）
var result = client.ReadInt16("40001"); // 内部自动连接 → 发送 → 接收

// 手动连接（可选）
client.Connect();
// ... 多次读写 ...
client.Disconnect();
```

## 7. 错误处理

所有操作返回 `OperateResult`，**不会抛异常**：

```csharp
var result = client.ReadInt16("99999");
if (!result.IsSuccess)
{
    // result.Message 包含中文错误描述
    // result.ErrorCode 包含协议错误码
    Console.WriteLine($"错误: {result.Message} (0x{result.ErrorCode:X2})");
}
```

## 下一步

- [核心类型参考](core/operate-result.md)
- [支持的协议列表](protocols/modbus.md)
- [批量读写详解](core/batch-read-write.md)
- [数据采集引擎](core/data-acquisition.md)
