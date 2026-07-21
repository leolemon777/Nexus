# 🔧 Nexus — .NET 工业通信库

> 开源工业设备通信库，支持 **100+ 工业协议**，覆盖 PLC、仪表、传感器、机器人、RFID 等设备类型。

[![Build Status](https://github.com/wanglizhou523/Nexus/actions/workflows/ci.yml/badge.svg)](https://github.com/wanglizhou523/Nexus/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-netstandard2.0%20%7C%20net8.0-blue.svg)](#)

---

## ✨ 特性

- 🏭 **100+ 协议库** — Modbus、Siemens S7、Mitsubishi MELSEC、Omron FINS、Allen-Bradley CIP、Keyence、Beckhoff ADS 等
- 🎯 **统一 API** — 所有协议实现 `IReadWriteDevice` 接口，学习一个 API 就能操作所有设备
- 🛡️ **无异常设计** — 操作返回 `OperateResult<T>`，`IsSuccess` + `Message` + `Content`，不用 try/catch
- 🔌 **传输无关** — Phase B 新架构（CommunicationPipe + INetMessage + DeviceCommunication）让一个协议透明切换 TCP/串口/UDP/SSL/DTU
- 🧪 **3500+ 自动化测试** — 单元测试 + 集成测试（虚拟服务器），不依赖真实硬件
- 🖥️ **WPF 调试器** — 内置可视化调试应用，支持 60+ 协议页面的连接/读写/日志
- 📦 **零外部依赖** — 协议库仅依赖 `netstandard2.0`，可运行于 .NET Framework 4.6.2+ / .NET Core 3.1+ / .NET 5/6/7/8

---

## 🚀 快速上手

### 安装

```bash
# 从源码编译（推荐）
git clone https://github.com/wanglizhou523/Nexus.git
cd Nexus
dotnet build Nexus.slnx
```

### 读 Modbus TCP 寄存器（3 行代码）

```csharp
using Nexus.Modbus;

var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
client.Connect();

// 读保持寄存器 40001 的值
var result = client.ReadUInt16("40001");
if (result.IsSuccess)
    Console.WriteLine($"温度 = {result.Content}");
else
    Console.WriteLine($"读取失败: {result.Message}");

client.Disconnect();
```

### 读西门子 S7 PLC

```csharp
using Nexus.Siemens;

var plc = new SiemensS7Net("192.168.1.1", SiemensPLCS.S1200);
plc.Connect();

var temperature = plc.ReadFloat("DB1.DBD0");   // 读 DB1 的浮点值
var counter     = plc.ReadInt16("MW100");        // 读 M 区

plc.Disconnect();
```

### 读三菱 PLC（MC3E Binary）

```csharp
using Nexus.Mitsubishi;

var plc = new Mc3EBinaryClient("192.168.1.10", 5001);
plc.Connect();

var value = plc.ReadUInt16("D100");  // 读数据寄存器 D100

plc.Disconnect();
```

---

## 📊 项目统计

| 指标 | 数值 |
|------|------|
| 协议库数量 | 100+ |
| 测试用例 | 3500+ |
| 源代码行数 | 250,000+ |
| .cs 文件 | 2,700+ |
| 目标框架 | netstandard2.0（协议库）/ net8.0-windows（WPF App）|

---

## 🏗️ 架构概览

```
Nexus.Core (netstandard2.0)
  ├── IReadWriteDevice        — 统一读写接口
  ├── OperateResult<T>        — 无异常结果类型
  ├── TcpDeviceBase           — TCP 传输基类（Phase A 修复后线程安全）
  ├── SerialDeviceBase        — 串口传输基类
  ├── UdpDeviceBase           — UDP 传输基类
  ├── ByteTransform/          — 字节序变换（Phase B 新架构）
  ├── Pipe/                   — 通信管道抽象（Phase B 新架构）
  │   ├── CommunicationPipe   — 传输无关的 IO 抽象
  │   ├── PipeTcpNet          — TCP 管道
  │   ├── PipeUdpNet          — UDP 管道
  │   ├── PipeSerialPort      — 串口管道
  │   ├── PipeSslNet          — SSL/TLS 管道
  │   └── PipeDtuNet          — DTU 透传管道
  ├── IMessage/               — 帧解析抽象（Phase B 新架构）
  │   ├── INetMessage         — 协议帧接口
  │   ├── ModbusTcpMessage    — Modbus TCP MBAP 帧
  │   ├── S7Message           — 西门子 TPKT 帧
  │   └── FinsMessage         — 欧姆龙 FINS 帧
  ├── Device/                 — 设备基类（Phase B 新架构）
  │   ├── DeviceCommunication — 组合 Pipe + Transform + Message
  │   ├── DeviceServer        — 虚拟服务器
  │   ├── DeviceTcpNet        — TCP 便利基类
  │   └── DeviceSerialPort    — 串口便利基类
  ├── ConnectionPool<T>       — 线程安全连接池
  ├── DataConverter           — 字节编解码（E-2 优化，10x 更快）
  └── CrcCalculator           — CRC16/LRC 校验
    │
    ├── Nexus.Modbus          — Modbus TCP/RTU/ASCII/UDP（深度实现）
    ├── Nexus.Siemens         — S7/FetchWrite/PPI/MPI/WebApi
    ├── Nexus.Mitsubishi      — MC3E Binary/Ascii/UDP/A1E/FX
    ├── Nexus.Omron           — FINS TCP/UDP/HostLink
    ├── Nexus.AllenBradley    — CIP/PCCC/DF1
    ├── Nexus.Keyence         — 上位链路/MC/Nano
    ├── Nexus.Beckhoff        — ADS 协议
    ├── ... 90+ 更多协议
    │
    └── Nexus.App (net8.0-windows)
         └── WPF 调试器应用（60+ 协议页面）
```

---

## 📋 支持的主要协议

### PLC 通信（深度实现）

| 厂商 | 协议 | 传输 |
|------|------|------|
| 🟢 Modbus | TCP / RTU / ASCII / UDP / RtuOverTcp / AsciiOverTcp | TCP / 串口 / UDP |
| 🟢 Siemens | S7 / FetchWrite / PPI / MPI / WebApi | TCP / 串口 / HTTP |
| 🟢 Mitsubishi | MC3E Binary / Ascii / UDP / A1E / FX Serial | TCP / 串口 / UDP |
| 🟢 Omron | FINS TCP / UDP / HostLink | TCP / UDP / 串口 |
| 🟢 Allen-Bradley | CIP / PCCC / DF1 | TCP / 串口 |
| 🟢 Keyence | MC / Nano / DLEN1 | TCP / 串口 |
| 🟢 Beckhoff | ADS/AMS | TCP |

### 其他设备

| 类型 | 协议 |
|------|------|
| 🔵 仪表 | DLT645 / CJT188 / RKC / Toledo / Yamatake |
| 🔵 电力 | DNP3 / IEC 60870-5-101/103/104 / IEC 61850 |
| 🔵 楼宇 | BACnet/IP / KNX / OPC UA |
| 🔵 半导体 | SECS/GEM (HSMS) |
| 🔵 RFID | Turck BLident |
| 🔵 传感器 | Geniitek VB31 振动 |
| 🔵 机器人 | KUKA / FANUC / ABB / Yaskawa / Staubli / UR |
| 🔵 IoT | MQTT / Redis / WebSocket / CoAP |
| 🔵 光源 | ShineIn LED 控制器 |
| 🔵 身份证 | SAM 二代证读卡器 |

---

## 🧪 运行测试

```bash
# 运行所有测试
dotnet test Nexus.slnx

# 运行单个协议测试
dotnet test tests/Nexus.Modbus.Tests

# 运行特定测试
dotnet test Nexus.slnx --filter "FullyQualifiedName~ModbusTcpTests"
```

---

## 🖥️ 运行 WPF 调试器

```bash
dotnet run --project src/Nexus.App
```

---

## 📁 项目结构

```
Nexus/
├── src/                         # 源代码
│   ├── Nexus.Core/              # 核心库（基类、接口、工具）
│   ├── Nexus.Modbus/            # Modbus 协议库
│   ├── Nexus.Siemens/           # 西门子协议库
│   ├── ...                      # 100+ 协议库
│   └── Nexus.App/               # WPF 调试器应用
├── tests/                       # 测试项目（3500+ 测试用例）
├── examples/                    # 快速上手示例
├── docs/                        # 文档
├── Nexus.slnx                   # 解决方案文件（XML 格式）
├── Directory.Build.props        # 全局构建配置
└── global.json                  # .NET SDK 版本锁定
```

---

## 🔧 技术规格

| 项目 | 规格 |
|------|------|
| 协议库框架 | `netstandard2.0`（兼容 .NET Framework 4.6.2+ / .NET Core 3.1+ / .NET 5-8）|
| WPF App 框架 | `net8.0-windows` |
| 测试框架 | xUnit 2.9.2 |
| MVVM | CommunityToolkit.Mvvm（source generators）|
| 版本管理 | MinVer（基于 Git tag）|
| 解决方案格式 | `.slnx`（XML）|
| 外部依赖 | 0（协议库无 NuGet 依赖）|

---

## 📜 开源许可

MIT License — 详见 [LICENSE](LICENSE)

本项目部分代码衍生自 [HslCommunication](https://github.com/GitHslAmateur/HslCommunication)（MIT, Copyright © Richard.Hu 2017-2025），详见 [NOTICE](NOTICE) 和 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

---

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

- 添加新协议：参考 [CONTRIBUTING_PROTOCOLS.md](CONTRIBUTING_PROTOCOLS.md)
- 迁移到新架构：参考 [docs/PHASE_B_MIGRATION_NOTES.md](docs/PHASE_B_MIGRATION_NOTES.md)
- 协议深化计划：参考 [docs/PHASE_C_ROADMAP.md](docs/PHASE_C_ROADMAP.md) 和 [docs/PHASE_D_ROADMAP.md](docs/PHASE_D_ROADMAP.md)
