# Nexus.Wago

WAGO 750/PFC PLC Modbus-variant protocol client for Nexus.

## 重要说明 / Honesty Note

**这是一个 Modbus TCP variant 客户端，不是 WAGO 私有协议实现。**

WAGO 750 以太网耦合器（750-3xx）与 PFC200 控制器**原生作为标准 Modbus TCP Server**（固件内建，开箱即用）。本库的价值在于封装 IEC 61131-3 地址语法（`%MW`/`%IW`/`%QX`/`%IX`）到标准 Modbus 寄存器/线圈/离散输入的映射，符合 Nexus "厂商库" 范式（与 `Nexus.Xinje`/`Nexus.Delta` 一致）。

底层通讯完全复用标准 Modbus TCP。

## Quick Start

```csharp
using Nexus.Wago;

using var client = new WagoPlcClient("192.168.1.17", port: 502, station: 1);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

// 读保持寄存器 %MW100
var r = client.ReadInt16("%MW100");
if (r.IsSuccess) Console.WriteLine(r.Content);

// 写 %MW100 = 1234
client.Write("%MW100", (short)1234);

// 读线圈 %QX0
var b = client.ReadBool("%QX0");

client.Disconnect();
```

## 地址映射 (IEC 61131-3 → Modbus)

来源：WAGO 750 Ethernet Coupler Manual §4.5.6 Modbus/TCP。

所有区域统一从 `0x3000` (12288) 起始。

| IEC 地址 | Modbus 区 | 起始 | 读 FC | 写 FC |
|----------|-----------|------|-------|-------|
| `%MWn`   | Holding Register | `0x3000 + n` | 03 | 16 |
| `%IWn`   | Input Register  | `0x3000 + n` | 04 | 只读 |
| `%QWn`   | Holding Register | `0x3000 + n` | 03 | 16 |
| `%IXn` / `%In` | Discrete Input | `0x3000 + n` | 02 | 只读 |
| `%QXn` / `%Qn` / `%Mn` | Coil | `0x3000 + n` | 01 | 05 |

### 偏移约定

实测存在两种偏移约定，通过 `WagoOffsetMode` 切换：

- `ZeroBased`（默认，手册）：`%MW0` → `0x3000`
- `OneBased`（部分现场实测/旧固件）：`%MW0` → `0x3001`

## Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `WagoPlcClient` | TCP (Modbus variant) | IEC 地址封装，默认端口 502 |

## Features

- Read/write for `%MW`, `%IW`, `%QW`, `%IX`, `%QX`, `%M` 区域
- 11 种数据类型 + 批量读写 (`IBatchReadWrite`)
- `WagoVirtualServer` 供集成测试
- 默认端口 502，默认站号 1

## 成熟度

- **协议本质**：标准 Modbus TCP（成熟）
- **地址映射**：基于 WAGO 官方手册，但实测设备行为以现场为准（偏移约定可配）
- **实机验证**：未实机验证，建议首次接入时核对 `%MW0` 的实际偏移
