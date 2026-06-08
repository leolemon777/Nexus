# Nexus 超越 HSL 作战计划

**目标:** 20 个并行 Agent，覆盖传输层、协议深度、IoT、虚拟 PLC、UI 五大战场  
**原则:** 每个 Agent 有独立文件所有权，零冲突并行

---

## Agent 任务分配

### 第一梯队：基础设施（Agent 1-4）

| Agent | 任务 | 文件所有权 |
|-------|------|-----------|
| 1 | **SerialDeviceBase** — 串口基类，RS232/RS485，CRC16/LRC，超时，重连 | `Nexus.Core/SerialDeviceBase.cs` (重写), `Nexus.Core/CrcCalculator.cs` (新建) |
| 2 | **UdpDeviceBase** — UDP 基类，广播模式，超时，请求/响应匹配 | `Nexus.Core/UdpDeviceBase.cs` (新建) |
| 3 | **ConnectionPool** — 多设备连接池，健康检查，负载均衡 | `Nexus.Core/ConnectionPool.cs` (新建), `Nexus.Core/IConnectionPool.cs` (新建) |
| 4 | **True Async** — 替换所有 Task.Run 包装为真正的 async/await | `Nexus.Core/TcpDeviceBase.cs` (修改 async 部分) |

### 第二梯队：Modbus 全系列（Agent 5-8）

| Agent | 任务 | 文件所有权 |
|-------|------|-----------|
| 5 | **Modbus RTU** — 串口 RS485，CRC16，FC01-06/15/16，完整实现 | `Nexus.Modbus/ModbusRtuClient.cs` (重写), `Nexus.Modbus.Tests/ModbusRtuTests.cs` (新建) |
| 6 | **Modbus ASCII** — 串口 ASCII，LRC，FC01-06/15/16 | `Nexus.Modbus/ModbusAsciiClient.cs` (重写), `Nexus.Modbus.Tests/ModbusAsciiTests.cs` (新建) |
| 7 | **Modbus UDP** — UDP 模式，广播，完整功能码 | `Nexus.Modbus/ModbusUdpClient.cs` (重写), `Nexus.Modbus.Tests/ModbusUdpTests.cs` (新建) |
| 8 | **Modbus RTU Over TCP** — TCP 透传 RTU 报文 | `Nexus.Modbus/ModbusRtuOverTcpClient.cs` (新建), `Nexus.Modbus.Tests/ModbusRtuOverTcpTests.cs` (新建) |

### 第三梯队：协议深度（Agent 9-12）

| Agent | 任务 | 文件所有权 |
|-------|------|-----------|
| 9 | **Siemens S7 深度** — 批量读写、字符串编码、字节序、PDU 协商优化 | `Nexus.Siemens/SiemensS7Client.cs` (增强) |
| 10 | **Mitsubishi MC 深度** — 批量读写、随机读写、字符串编码、多型号 | `Nexus.Mitsubishi/Mc3EBinaryClient.cs` (增强) |
| 11 | **Omron FINS 深度** — 批量读写、字符串、FINS-UDP 实现 | `Nexus.Omron/FinsTcpClient.cs` (增强), `Nexus.Omron/FinsUdpClient.cs` (新建) |
| 12 | **AB CIP 深度** — Tag Fragmented 读写、PCCC、SLC500 | `Nexus.AllenBradley/AllenBradleyCipClient.cs` (增强), `Nexus.AllenBradley/PcccClient.cs` (新建) |

### 第四梯队：新协议（Agent 13-17）

| Agent | 任务 | 文件所有权 |
|-------|------|-----------|
| 13 | **OPC UA Client** — 完整 OPC UA 二进制协议，节点读写，订阅 | `Nexus.OpcUa/OpcUaClient.cs` (重写), `Nexus.OpcUa/OpcUaSession.cs` (新建) |
| 14 | **MQTT Client + Broker** — MQTT 3.1.1/5.0，QoS 0/1/2，内置 Broker | `src/Nexus.Mqtt/` (整个新项目) |
| 15 | **Redis Client** — 连接池，String/Hash/List/Set/Pub-Sub | `src/Nexus.Redis/` (整个新项目) |
| 16 | **IEC 60870-5-104** — 电力协议，IEC 104 Client | `src/Nexus.Iec104/` (整个新项目) |
| 17 | **BACnet/IP** — 楼宇协议，BACnet Client | `src/Nexus.Bacnet/` (整个新项目) |

### 第五梯队：虚拟 PLC + UI（Agent 18-20）

| Agent | 任务 | 文件所有权 |
|-------|------|-----------|
| 18 | **Virtual PLC 批量** — Siemens/Mitsubishi/Omron/AB 虚拟 PLC 增强 | 各协议目录下 `*VirtualServer.cs` |
| 19 | **WPF Monitor Dashboard** — 实时趋势图、历史回放、多地址监控 | `Nexus.App/Views/MonitorPage.xaml` (重写), `Nexus.App/ViewModels/MonitorViewModel.cs` (增强) |
| 20 | **Protocol Logger UI** — 报文日志可视化、连接状态指示器 | `Nexus.App/Controls/ProtocolLogViewer.xaml` (新建), `Nexus.App/Controls/ConnectionStatusIndicator.xaml` (新建) |

---

## 验证计划

每个 Agent 完成后:
1. `dotnet build` 该模块 — 零错误
2. 编写单元测试 — 核心逻辑覆盖
3. 最终全量 `dotnet test Nexus.slnx` — 全部通过

## 预期成果

完成后 Nexus 将具备:
- ✅ 完整传输层: TCP + UDP + Serial + RTU-over-TCP
- ✅ 19 个协议全部深度实现（批量/随机/字符串/字节序）
- ✅ 4 个新协议: MQTT, Redis, IEC 104, BACnet
- ✅ 连接池 + 真正 async
- ✅ 全协议虚拟 PLC
- ✅ 专业级监控 UI
