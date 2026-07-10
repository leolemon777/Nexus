# Nexus.Phoenix

Phoenix Contact AXC PLC Modbus-variant protocol client for Nexus.

## 重要说明 / Honesty Note

**这是一个 Modbus TCP variant 客户端，不是 Phoenix Contact 私有协议实现。**

Phoenix Contact AXC 系列 PLC（AXC F 2152 / AXC F 3152 等，运行 PLCnext Technology）**原生作为标准 Modbus TCP Server**（在 PLCnext 工程中配置 Modbus 服务并映射变量，开箱即用）。本库的价值在于封装 IEC 61131-3 地址语法（`%MW`/`%IW`/`%QW`/`%IX`/`%QX`/`%M`）到标准 Modbus 寄存器/线圈/离散输入的映射，符合 Nexus 厂商库范式（与 `Nexus.Wago`/`Nexus.AbbPlc`/`Nexus.Xinje` 一致）。

底层通讯完全复用标准 Modbus TCP。

## Quick Start

```csharp
using Nexus.Phoenix;

using var client = new PhoenixPlcClient("192.168.1.10", port: 502, station: 1);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

// 读保持寄存器 %MW100
var r = client.ReadInt16("%MW100");
if (r.IsSuccess) Console.WriteLine(r.Content);

// 写 %MW100 = 1234
client.Write("%MW100", (short)1234);

// 读输入寄存器 %IW0（只读）
var iw = client.ReadUInt16("%IW0");

// 读线圈 %QX0
var b = client.ReadBool("%QX0");

// 读离散输入 %IX5（只读）
var ix = client.ReadBool("%IX5");

client.Disconnect();
```

## 地址映射 (IEC 61131-3 → Modbus)

来源：Phoenix Contact PLCnext Engineer Modbus Parameterization 文档。

标准 IEC 编号（无统一偏移，与 WAGO 不同）。

| IEC 地址 | Modbus 区 | 读 FC | 写 FC |
|----------|-----------|-------|-------|
| `%MWn`   | Holding Register n | 03 | 16 |
| `%IWn`   | Input Register n  | 04 | 只读 |
| `%QWn`   | Holding Register n | 03 | 16 |
| `%IXn` / `%In` | Discrete Input n | 02 | 只读 |
| `%QXn` / `%Qn` / `%Mn` | Coil n | 01 | 05 |

> 在 PLCnext Engineer 中，Modbus 服务需要先建立变量到 Modbus 寄存器/线圈的映射表，本库假设映射已完成且 IEC 编号直接对应 Modbus 地址。

## ⚠️ 重要警告：%IB / %QB 不支持

Phoenix Contact PLCnext Technology 的 `%IB`（输入字节）与 `%QB`（输出字节）**字节寻址没有官方固定的 Modbus 偏移公式**。这不像 `%MW`/`%QX` 那样能机械映射。

本解析器**遇到 `%IB` / `%QB` 前缀会直接抛出 `ArgumentException`**，错误信息：

> Phoenix %IB/%QB 字节寻址无固定映射，请在 PLCnext 程序侧配置（映射到 %MW/%QX 等寄存器/线圈）

**解决方法**：在 PLCnext 程序侧将字节变量显式映射到保持寄存器（`%MW`）或线圈（`%QX`），然后通过 `%MW`/`%QX` 地址访问即可。

## Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `PhoenixPlcClient` | TCP (Modbus variant) | IEC 地址封装，默认端口 502，默认站号 1 |

## Features

- Read/write for `%MW`、`%IW`、`%QW`、`%IX`、`%QX`、`%M` 区域
- 11 种数据类型 + 批量读写 (`IBatchReadWrite`)
- `PhoenixVirtualServer` 供集成测试（默认端口 5020）
- 默认端口 502，默认站号 1

## 成熟度

- **协议本质**：标准 Modbus TCP（成熟）
- **地址映射**：基于 Phoenix Contact PLCnext Engineer 公开文档（标准 IEC 编号映射）
- **实机验证**：未实机验证，建议首次接入时核对 `%MW0` 的实际映射偏移（PLCnext 工程中的映射表配置决定）
