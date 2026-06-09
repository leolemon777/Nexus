# Nexus — 开源工业通讯库

Nexus 是一个面向 .NET 的开源工业通讯框架，目标是替代 HslCommunication，提供更干净、更一致的 API。

## 特性

- **42 个协议库** — Modbus, Siemens S7, Mitsubishi, Omron, Allen-Bradley, Schneider, Yokogawa 等
- **统一接口** — 所有协议实现 `IReadWriteDevice`，切换 PLC 无需改代码
- **零依赖** — 基于 netstandard2.0，支持 .NET Framework 4.6.1+ 和 .NET Core 2.0+
- **批量读写** — `IBatchReadWrite` 一次请求读写多个地址
- **数据订阅** — `ISubscribeDevice` 轮询式变化通知
- **数据采集引擎** — `DataAcquisitionEngine` 多设备调度 + `IDataSink` 可扩展输出

## 快速开始

```csharp
using Nexus.Modbus;

// 1. 创建客户端
using var client = new ModbusTcpClient("192.168.1.10", port: 502);

// 2. 读取数据
var result = client.ReadInt16("40001");
if (result.IsSuccess)
    Console.WriteLine($"值: {result.Content}");
else
    Console.WriteLine($"错误: {result.Message}");
```

5 分钟完成首次 PLC 读取 → [快速开始](getting-started.md)

## 支持的协议

| 协议 | 系列 | 传输层 |
|------|------|--------|
| Modbus | TCP / RTU / ASCII / UDP / RtuOverTcp | TCP, Serial, UDP |
| Siemens | S7 / FetchWrite / PPI | TCP |
| Mitsubishi | MC3E Binary/Ascii / A1E / FX Serial | TCP, Serial |
| Omron | FINS TCP/UDP/Serial / HostLink | TCP, UDP, Serial |
| Allen-Bradley | CIP / PCCC | TCP |
| Schneider | Modicon M580/M340 | TCP |
| Yokogawa | 二进制链接 | TCP |
| Inovance | Easy 系列 | TCP |
| Delta | DVP/AS (Modbus RTU) | Serial |
| GE | SRTP (90-30/90-70/PACSystems) | TCP |
| Beckhoff | ADS | TCP |
| Panasonic | Mewtocol | TCP, Serial |
| 更多... | 见 [完整协议列表](protocols/more-protocols.md) | |

## 架构

```
Nexus.Core (netstandard2.0)              — 统一接口和基类
  └── Nexus.{Protocol} (netstandard2.0)  — 42 个协议客户端库
        └── Nexus.App (net8.0-windows)   — WPF 调试应用
```

## 安装

通过 NuGet 安装：

```bash
dotnet add package Nexus.Modbus
dotnet add package Nexus.Siemens
dotnet add package Nexus.Mitsubishi
```

或直接引用项目：

```xml
<ProjectReference Include="..\Nexus.Modbus\Nexus.Modbus.csproj" />
```

## 核心基础设施

- [OperateResult 模式](core/operate-result.md)
- [IReadWriteDevice 接口](core/read-write-device.md)
- [IBatchReadWrite 批量读写](core/batch-read-write.md)
- [ISubscribeDevice 订阅](core/subscribe-device.md)
- [数据采集引擎](core/data-acquisition.md)
- [连接池](core/connection-pool.md)
- [重连与心跳](core/reconnect-heartbeat.md)
- [结构体映射](core/struct-mapping.md)

## 协议文档

- [Modbus](protocols/modbus/index.md)
- [Siemens](protocols/siemens/index.md)
- [Mitsubishi](protocols/mitsubishi/index.md)
- [Omron](protocols/omron/index.md)
- [Allen-Bradley](protocols/allenbradley/index.md)

## 迁移与规划

- [HSL 迁移指南](../HSL_MIGRATION_GUIDE.md)
- [协议就绪状态](../PROTOCOL_READINESS.md)
- [执行计划](../EXECUTION_PLAN.md)
