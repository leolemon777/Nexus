# Nexus.Honeywell

Honeywell HC900 / ControlEdge HC900 Modbus-variant protocol client for Nexus.

## 重要说明 / Honesty Note

**这是标准 Modbus TCP 直通客户端，不是 Honeywell 私有协议实现。**

Honeywell HC900（ControlEdge HC900）作为 Modbus TCP Server。它**没有厂商专属地址语法** —— 寄存器布局由 HC Designer 软件中配置的 "Custom Modbus Map" 决定（用户在 HC Designer 中把内部 PV/SP/OP 等功能块变量映射到标准 Modbus 4xxxxx/3xxxxx/0xxxxx 区）。

因此本库的价值在于：提供 Honeywell 品牌入口 + 文档化 HC900 的 Modbus 寄存器约定。地址使用标准 Modbus 编号。

底层通讯完全复用标准 Modbus TCP（`Nexus.Modbus.ModbusTcpClient`）。

## Quick Start

```csharp
using Nexus.Honeywell;

// 地址使用标准 Modbus 编号（在 HC Designer 的 Custom Modbus Map 中配置）
using var client = new HoneywellClient("192.168.1.50", port: 502, station: 1);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

// 读保持寄存器 400101 (HC Designer 中映射的 PV 变量)
var r = client.ReadInt16("400101");
if (r.IsSuccess) Console.WriteLine($"PV = {r.Content}");

// 浮点读（HC900 常用 32-bit float，跨 2 个寄存器）
var f = client.ReadFloat("400101");

client.Disconnect();
```

## HC900 Modbus 寄存器约定

来源：Honeywell 51-52-25-111 HC900 Communications Manual。

| Modbus 区 | HC900 用法 | 功能码 |
|-----------|-----------|--------|
| 4xxxxx Holding Registers | 主要数据区，HC Designer 自定义映射（PV/SP/OP 浮点或整数） | 03 读 / 06·16 写 |
| 3xxxxx Input Registers | 只读输入（部分固定映射） | 04 读 |
| 0xxxxx Coils | 状态位（较少用） | 01 读 / 05 写 |
| 1xxxxx Discrete Inputs | 只读状态位 | 02 读 |

**注意**：寄存器布局不是固定的，由用户在 HC Designer 中通过 "Custom Modbus Map" 配置。请参考你的 HC900 工程的 Modbus 映射表确定具体地址。

## Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `HoneywellClient` | TCP (标准 Modbus 直通) | 默认端口 502，HC900 支持双网口 |

## 成熟度

- **协议本质**：标准 Modbus TCP（成熟）
- **地址映射**：无固定映射，由 HC Designer Custom Map 决定，本库直通标准 Modbus 编号
- **实机验证**：未实机验证，建议参考具体工程的 Modbus 映射表
