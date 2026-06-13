# Nexus

**开源免费工控通讯库** — Open-source industrial communication library for .NET

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET Standard 2.0](https://img.shields.io/badge/.NET-Standard%202.0-green.svg)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
[![Tests](https://img.shields.io/badge/tests-3623-brightgreen.svg)]()
[![Build](https://img.shields.io/badge/build-passing-brightgreen.svg)]()

Nexus 是一个免费开源的工业通讯协议库，目标是替代 HslCommunication 等商业库，提供**零成本**的 PLC/工控通讯解决方案。

> **无 8 小时运行限制 · 无授权码 · MIT 永久免费 · 源码完全公开**

## ✨ 特性

- **58 个协议模块**，覆盖主流 PLC/CNC/机器人/仪表品牌
- **3,623 单元测试**，0 失败，100% 通过率
- **37 个虚拟服务器**，无需硬件即可开发调试
- **统一 API**：`IReadWriteDevice` 接口，一套代码操作所有设备
- **OperateResult\<T\>** 错误处理模式，告别异常流控
- **netstandard2.0** 兼容 .NET Framework 4.6.2+ / .NET Core 2.0+ / .NET 5-10
- **WPF 调试工具** 附带 375 种主题、示例代码显示
- **MIT 协议**，商用免费，无需购买授权

## 🚀 快速开始

### 安装

```bash
# 安装单个协议（推荐）
dotnet add package Nexus.Modbus
dotnet add package Nexus.Siemens
dotnet add package Nexus.Mitsubishi
```

### 5 分钟上手

```csharp
using Nexus.Modbus;

// Modbus TCP 读写
using var client = new ModbusTcpClient("192.168.1.100", 502);
client.Connect();

// 读取保持寄存器 (FC03)
var result = client.ReadInt16("40001");
if (result.IsSuccess)
    Console.WriteLine($"值: {result.Content}");

// 写入寄存器 (FC06)
client.Write("40001", (short)1234);

client.Disconnect();
```

```csharp
using Nexus.Siemens;

// 西门子 S7 读写
using var s7 = new SiemensS7Client("192.168.1.1");
s7.Connect();

var value = s7.ReadInt16("DB1.DBW0");
var str = s7.ReadS7String("DB1.DBB100");
s7.Write("DB1.DBW0", (short)100);

s7.Disconnect();
```

```csharp
using Nexus.Mitsubishi;

// 三菱 MC3E 读写
using var mc = new Mc3EBinaryClient("192.168.1.10");
mc.Connect();

var d100 = mc.ReadInt16("D100");
mc.Write("D100", (short)200);

mc.Disconnect();
```

## 📦 支持的协议 (58 个)

| 品牌 | 协议 | 传输 | 品牌 | 协议 | 传输 |
|------|------|------|------|------|------|
| **Siemens** | S7, PPI, MPI, FetchWrite, WebAPI | TCP/Serial | **Modbus** | TCP, RTU, ASCII, UDP, RTUoTCP | 全部 |
| **Mitsubishi** | MC3E(二进制/ASCII/UDP), A1E, FX, FxLink, A3C, CIP | TCP/Serial | **Omron** | FINS TCP/UDP, HostLink, CIP | TCP/Serial |
| **AllenBradley** | CIP, PCCC, SLC, DF1, Micro | TCP/Serial | **Panasonic** | Mewtocol, MC | TCP/Serial |
| **Keyence** | KV, MC, Nano, DL-EN1, SR-2000 | TCP/Serial | **Beckhoff** | ADS | TCP |
| **LS Electric** | XGT, Cnet, CPU Serial | TCP/Serial | **Delta** | DVP (TCP/RTU/ASCII/RTUoTCP) | 全部 |
| **Fuji** | SPH, SPB, CommandSetting | TCP/Serial | **Fatek** | FBs | TCP/Serial |
| **Inovance** | Easy, Modbus, CIP, ASCII, ComputerLink | TCP/Serial | **Yokogawa** | 横河协议 | TCP |
| **Yaskawa** | Memobus | TCP | **Xinje** | XC/XG | TCP/Serial |
| **GE** | SRTP | TCP | **Schneider** | Modicon M340/M580 | TCP |
| **DNP3** | DNP3 | TCP | **IEC 61850** | MMS | TCP |
| **IEC 104** | IEC 60870-5-104 | TCP | **BACnet** | BACnet/IP | UDP |
| **OPC UA** | OPC UA | TCP | **MQTT** | 3.1.1 + Broker | TCP |
| **Redis** | Redis | TCP | **SECS** | HSMS/GEM | TCP/Serial |
| **DLT 645** | 电表协议 | Serial | **CJT 188** | 水/气/热表 | Serial |
| **KUKA** | EKI, VarProxy | TCP | **FANUC** | FOCAS | TCP |
| **ABB** | RobotWare | TCP | **Yaskawa** | YRC1000 | TCP |
| **Efort** | KEBA | TCP | **Estun** | 自有协议 | TCP |
| **Yamaha** | RCX | TCP | **Staubli** | 自有协议 | TCP |
| **Universal Robots** | RTDE | TCP | **RKC** | 温控表 | TCP |
| **Toledo** | 称重仪表 | TCP | **KNX** | 楼宇协议 | UDP |
| **FTP** | 文件传输 | TCP | | | |

## 🏗️ 架构

```
Nexus.Core (netstandard2.0)
├── IReadWriteDevice        — 统一读写接口
├── OperateResult<T>        — 错误处理模式
├── TcpDeviceBase           — TCP 基类（短/长连接、自动重连、心跳）
├── SerialDeviceBase        — 串口基类
├── UdpDeviceBase           — UDP 基类
├── IBatchReadWrite         — 批量读写接口
├── ISubscribeDevice        — 数据订阅接口
├── DataConverter           — 字节序转换（4 种）
├── ConnectionPool<T>       — 连接池
├── PacketRecorder          — 报文录制/分析
└── DataAcquisitionEngine   — 数据采集引擎

Nexus.{Protocol} (netstandard2.0)
├── {Protocol}Client.cs     — 协议客户端
├── {Protocol}Address.cs    — 地址解析
├── {Protocol}Model.cs      — 数据模型
└── {Protocol}VirtualServer.cs — 虚拟服务器（用于测试）

Nexus.App (net8.0-windows)
└── WPF 调试工具（375 种主题、示例代码、报文日志）
```

## 🧪 测试

```bash
# 运行全部测试
dotnet test Nexus.slnx

# 运行单个协议测试
dotnet test tests/Nexus.Modbus.Tests

# 运行特定测试
dotnet test Nexus.slnx --filter "FullyQualifiedName~ReadInt16"
```

## 📖 文档

- [快速开始](docs/getting-started.md)
- [Modbus 协议](docs/protocols/modbus/index.md)
- [Siemens 协议](docs/protocols/siemens/index.md)
- [Mitsubishi 协议](docs/protocols/mitsubishi/index.md)
- [Omron 协议](docs/protocols/omron/index.md)
- [AllenBradley 协议](docs/protocols/allenbradley/index.md)
- [高级功能](docs/advanced/) — VirtualPlc / DataAcquisition / PacketRecorder / ProtocolBridge

## 🤝 贡献

欢迎贡献！请阅读 [贡献指南](CONTRIBUTING.md)。

## 📄 License

[MIT](LICENSE) — 免费商用，无需购买授权。
