# Nexus.Hitachi

Hitachi EH-150 PLC Modbus RTU-over-TCP variant protocol client for Nexus.

## 重要说明 / Honesty Note

**这是 Modbus RTU-over-TCP variant 客户端，不是日立私有协议实现。**

关键事实：
- **EH-150 CPU 本身不内建 Modbus**。Modbus master/slave 能力由 **EH-SIO 串口通信模块** 提供（支持 RS-232C/422/485、Modbus master/slave）。
- EH-SIO **仅支持 Modbus RTU，无原生 TCP**。要 TCP 访问需通过串口服务器/网关做 RTU-over-TCP 透传。
- 本客户端通过 RTU-over-TCP（RTU ADU 经 TCP socket 传输）访问 EH-150。

**地址映射表基于日系 PLC 惯例，未实机验证。** 具体偏移以 EH-SIO Application Manual (NJI443BX) 为准。

## Quick Start

```csharp
using Nexus.Hitachi;

// 连接到 EH-150 前方的 RTU 透传网关
using var client = new HitachiClient("192.168.1.200", port: 502, station: 1);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

// 读数据寄存器 D100
var r = client.ReadInt16("D100");
if (r.IsSuccess) Console.WriteLine(r.Content);

// 写输出 Y10
client.Write("Y10", true);

client.Disconnect();
```

## 地址映射（日系 operand → Modbus，未实机验证）

| Operand | 含义 | Modbus 区 | 读 FC | 写 FC |
|---------|------|-----------|-------|-------|
| `D`     | 数据寄存器 | Holding Register | 03 | 06·16 |
| `R`/`W` | 扩展寄存器 | Holding Register (0x1000+) | 03 | 06·16 |
| `T`     | 定时器当前值 | Holding Register (0x2000+) | 03 | 只读 |
| `C`     | 计数器当前值 | Holding Register (0x2800+) | 03 | 只读 |
| `Y`     | 输出 | Coil (0x0020+) | 01 | 05 |
| `M`/`L` | 内部继电器 | Coil (0x0100+) | 01 | 05 |
| `X`     | 输入 | Discrete Input | 02 | 只读 |

## Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `HitachiClient` | RTU-over-TCP (Modbus variant) | 需 EH-SIO 模块 + RTU 透传网关 |

## 成熟度

- **协议本质**：Modbus RTU-over-TCP
- **地址映射**：基于日系惯例，**未实机验证**。首次接入请以 EH-SIO Application Manual 为准核对偏移
- **硬件依赖**：需要现场有 EH-SIO 串口模块 + RTU 透传网关（EH-150 无原生以太网 Modbus）
