# Nexus.AbbPlc

ABB AC500 PLC Modbus-variant protocol client for Nexus.

## 重要说明 / Honesty Note

**这是一个 Modbus TCP variant 客户端，覆盖 ABB AC500 PLC 产品线，不是 ABB 私有协议实现。**

ABB AC500 系列（PM571/PM58x/PM59x）原生作为标准 Modbus TCP Server。本库封装 IEC 61131-3 地址语法（`%MW`/`%IW`/`%QX`/`%IX`）到标准 Modbus 的映射，符合 Nexus 厂商库范式（与 `Nexus.Xinje`/`Nexus.Delta` 一致）。

注意：ABB 机器人协议见独立库：
- `Nexus.ABB.EGM` — 机器人 EGM 实时运动控制
- `Nexus.Robot.Abb` — IRC5/OmniCore WebAPI

本库仅覆盖 PLC 产品线（AC500）。

## Quick Start

```csharp
using Nexus.AbbPlc;

using var client = new AbbPlcClient("192.168.1.100", port: 502, station: 1);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

// 读保持寄存器 %MW100
var r = client.ReadInt16("%MW100");
if (r.IsSuccess) Console.WriteLine(r.Content);

// 写 %MW100 = 1234
client.Write("%MW100", (short)1234);

client.Disconnect();
```

## 地址映射 (IEC 61131-3 → Modbus)

来源：ABB AC500 V3 Modbus TCP 手册 3ADR010810。

标准 IEC 编号（无统一偏移，与 WAGO 不同）。

| IEC 地址 | Modbus 区 | 读 FC | 写 FC |
|----------|-----------|-------|-------|
| `%MWn`   | Holding Register n | 03 | 16 |
| `%IWn`   | Input Register n  | 04 | 只读 |
| `%QWn`   | Holding Register n | 03 | 16 |
| `%IXn` / `%In` | Discrete Input n | 02 | 只读 |
| `%QXn` / `%Qn` / `%Mn` | Coil n | 01 | 05 |

## Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `AbbPlcClient` | TCP (Modbus variant) | IEC 地址封装，默认端口 502 |

## 成熟度

- **协议本质**：标准 Modbus TCP（成熟）
- **地址映射**：基于 ABB 官方手册
- **实机验证**：未实机验证
