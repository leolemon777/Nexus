# Changelog

本文件记录每个版本的主要变更。

## [Unreleased]

### Added
- 58 个协议模块（完整列表见 README）
- 3,623 单元测试，37 个虚拟服务器
- 示例代码显示：每个协议页面显示可复制的 C# 使用代码
- Modbus 报文诊断工具（ModbusDiagnostics）
- Modbus 透明网关（ModbusGateway）
- IEC 61850 ASN.1 BER 编解码器 + TPKT/COTP + MMS Associate 握手
- IEC 104 时钟同步、组召唤、计数器读取
- VirtualPlc 增强：JSON 场景加载、S7/Modbus 内存模型、PID/正弦/随机模拟
- PacketRecorder：TX/RX 报文录制、JSONL 导出、响应时间分析
- DataAcquisitionEngine：多点采集、CSV 导出
- ProtocolBridge：S7/MC3E/FINS/AB 源 + CSV/Redis/InfluxDB 目标
- GitHub Actions CI：Windows + Linux 双平台，全量 NuGet 打包
- 13 个新增测试项目（Delixi, EcFan, Freedom, Geniitek, Knx, MegMeet, OpenProtocol, Sam, Toyopuc, OpcUa, Vigor, Yamatake, YuDian）
- DocFX 文档站配置 + 52 篇文档
- 10 个示例项目（Modbus, Siemens, Mitsubishi, Omron, AllenBradley, Schneider, Beckhoff, Inovance, LS, Yokogawa）

### Fixed
- Redis nullable 警告（CS8618/CS8625/CS8600/CS8604）
- 12 个桩客户端 IReadWriteDevice 方法补全（Efort, Toledo, Yamaha, CJT188, DLT645, FanucRobot, KUKA, Yaskawa, SECS, RKC）
- MC3E ASCII/UDP 客户端完整实现（位操作、PLC 控制、随机读写、自动分片）
- Siemens S7 Timer/Counter 读写

### Changed
- Directory.Build.props 增强 NuGet 元数据（Source Link、符号包）
- .gitignore 补全（docs/_site, docs/api, nupkgs, .env, appsettings.Development.json）

## [0.1.0] - 初始版本

### Added
- 核心框架（IReadWriteDevice, OperateResult, TcpDeviceBase, SerialDeviceBase, UdpDeviceBase）
- Modbus TCP/RTU/ASCII/UDP/RtuOverTcp
- Siemens S7/FetchWrite/PPI
- Mitsubishi MC3E/A1E/FX
- Omron FINS TCP/UDP/HostLink
- AllenBradley CIP/PCCC
- WPF 调试工具
