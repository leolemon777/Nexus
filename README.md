# Nexus

**开源免费工控通讯库** — Open-source industrial communication library for .NET

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET Standard 2.0](https://img.shields.io/badge/.NET-Standard%202.0-green.svg)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)

Nexus 是一个免费开源的工业通讯协议库，目标是替代 HslCommunication 等商业库，提供**零成本**的 PLC/工控通讯解决方案。

## ✨ 特性

- **26 个协议模块**，覆盖主流 PLC/CNC/机器人品牌
- **统一 API**：`IReadWriteDevice` 接口，一套代码操作所有设备
- **OperateResult\<T\>** 错误处理模式，告别异常流控
- **netstandard2.0** 兼容 .NET Framework / .NET Core / .NET 5-8
- **1129 单元测试**，100% 通过率
- **WPF 调试工具** 附带可视化通讯调试界面
- **MIT 协议**，商用免费

## 📦 支持的协议

| 品牌 | 协议模块 | 传输方式 |
|------|---------|---------|
| Siemens | S7 (FetchWrite) | TCP |
| Mitsubishi | MC3E / A1E / FX Serial | TCP / Serial |
| Omron | FINS TCP / HostLink | TCP |
| AllenBradley | CIP / PCCC | TCP |
| Modbus | TCP / RTU / ASCII / UDP / RtuOverTcp | 全部 |
| Beckhoff | ADS | TCP |
| Panasonic | Mewtocol | TCP |
| Keyence | KV | TCP |
| Inovance | EasyNet | TCP |
| YASKAWA | Memobus | TCP |
| Yokogawa | 横河协议 | TCP |
| Delta | DVP | Serial |
| Fatek | FBs | TCP |
| Fuji | SPH | Serial |
| GE | SRTP | TCP |
| Fanuc | FOCAS | TCP |
| KUKA | EKI | TCP |
| Xinje | Modbus兼容 | TCP |
| LS Electric | XGT | TCP |
| BACnet | BACnet/IP | UDP |
| IEC 104 | IEC 60870-5-104 | TCP |
| OPC UA | OPC UA | TCP |
| MQTT | MQTT 3.1.1 | TCP |
| Redis | Redis | TCP |

## 🚀 快速开始

```csharp
using Nexus.Modbus;

// Modbus TCP
using var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
client.Connect();

// 读取保持寄存器
var result = client.ReadInt16("40001");
if (result.IsSuccess)
    Console.WriteLine($"Value: {result.Content}");

// 写入寄存器
client.Write("40001", (short)1234);
```

```csharp
using Nexus.Siemens;

// Siemens S7
using var s7 = new SiemensS7Client("192.168.1.1");
s7.Connect();

var db1 = s7.ReadInt16("DB1.DBW0");
var str = s7.ReadS7String("DB1.DBB100");
```

## 🏗️ 架构

```
Nexus.Core          → IReadWriteDevice, OperateResult<T>, TcpDeviceBase, SerialDeviceBase
Nexus.Modbus        → Modbus TCP/RTU/ASCII/UDP/RtuOverTcp (170 tests)
Nexus.Siemens       → S7 + FetchWrite (95 tests)
Nexus.Mitsubishi    → MC3E + A1E (166 tests)
Nexus.Omron         → FINS TCP + HostLink (112 tests)
Nexus.AllenBradley  → CIP + PCCC (84 tests)
... 22 more protocol modules
```

## 🧪 测试

```bash
dotnet test Nexus.slnx    # 1129 tests, 0 failures
```

## 📄 License

[MIT](LICENSE) — 免费商用。
