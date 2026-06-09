# Nexus 超越 HSL 长周期开发计划 (2026-2028)

## Context

**为什么需要这份计划：** 现有的 `OVERTAKE_HSL_PLAN.md` 和 `EXECUTION_PLAN.md` 已经定义了 Phase 0-6 的执行路线，但它们偏向"传统追赶"——协议数量、测试数量、文档覆盖。用户需要一份**更高维度的战略计划**，不仅要追赶 HSL，更要在"智能化"维度形成代差，建立 HSL 无法复制的护城河。

**当前基线（2026-06-08）：**
- 40 个协议库，39 个测试项目
- 0 个 NotImplementedException（Phase 0 已完成）
- 14 个 IBatchReadWrite，3 个 ISubscribeDevice，16 个虚拟 Server
- 核心基础设施已就绪：AutoReconnectGuard、HeartbeatGuard、SemaphoreSlim、AddressContext
- Modbus 为 Production Candidate，Siemens/Mitsubishi/Omron/AB 为 Usable

**HSL 基线：**
- ~100 协议模块，300 万 NuGet 下载，10 年生产验证
- 无智能化特性，无虚拟 PLC，无 AI 诊断

---

## 三大战略支柱

```
支柱 A: 协议深度 + 广度 — "每一个协议都可用、可靠、有文档"
支柱 B: 智能化 — "Nexus 会思考，HSL 不会"
支柱 C: 生态 + 开源 — "让用户离不开，让社区壮大的飞轮"
```

---

## 总体时间线（24 个月）

```
2026 Q3 (Month 1-3)   ─── [Wave 1] 稳固地基 + Top 5 生产化
2026 Q4 (Month 4-6)   ─── [Wave 2] 协议深度 + 智能基座
2027 Q1 (Month 7-9)   ─── [Wave 3] 智能诊断 + 生态发布
2027 Q2 (Month 10-12) ─── [Wave 4] 广度扩展 + AI 特性
2027 Q3 (Month 13-15) ─── [Wave 5] 差异化护城河 + 社区
2027 Q4 (Month 16-18) ─── [Wave 6] 工业级 AI + 垂直方案
2028 Q1 (Month 19-21) ─── [Wave 7] 平台化 + 国际化
2028 Q2 (Month 22-24) ─── [Wave 8] 事实标准
```

---

## Wave 1: 稳固地基 + Top 5 生产化 (Month 1-3)

> 目标：让 Nexus 的核心协议达到"工厂敢用"的级别

### 1.1 Top 5 协议生产化 (复用 EXECUTION_PLAN Milestone 2)

| 协议 | 关键任务 | 产出 |
|------|---------|------|
| Modbus | FC08 诊断、FC43 设备识别、性能基准、网关模式文档 | 参考包模板 |
| Siemens | S7 重连/心跳指导、PPI 审计、真实设备矩阵 | 生产级 S7 |
| Mitsubishi | MC3E ASCII/UDP 测试补齐、FX 范围确定、地址覆盖证明 | 生产级 MC |
| Omron | FINS 路由文档、HostLink 串口审计、节点配置示例 | 生产级 FINS |
| AllenBradley | CIP 标签工作流文档、PCCC/MicroLogix 覆盖、UDT 范围声明 | 生产级 CIP |

### 1.2 核心基础设施落地 (复用 EXECUTION_PLAN Milestone 3)

- `AutoReconnectGuard` → Modbus TCP + Siemens S7 实际接入 + 测试
- `HeartbeatGuard` → 每协议心跳策略文档
- `ConnectionPool<T>` → Modbus TCP 接入示例
- `AddressContext` → 主协议地址解析器集成
- `StructConverter` → Modbus + S7 示例 + 测试

### 1.3 WPF 调试器诊断增强 (复用 EXECUTION_PLAN Milestone 4)

- 通用报文解析/导出服务（超越 Modbus TCP 单页）
- 诊断包导出（App 日志 + TX/RX + 连接配置 + 失败时间线）
- MonitorPage 多设备工作流 + 趋势导出

### 1.4 NuGet + CI 就绪

- Modbus 参考包发布到 nuget.org
- Top 5 全部打包元数据就位
- GitHub Actions CI 矩阵（build + test + pack）
- `HSL_MIGRATION_GUIDE.md` 完整化

**Wave 1 完成标志：**
- Top 5 全部 Promotion Candidate，有真实设备验证行
- `dotnet add package Nexus.Modbus` 可用
- 文档站上线（GitHub Pages + DocFX）
- 测试 3000+

---

## Wave 2: 协议深度 + 智能基座 (Month 4-6)

> 目标：把已有协议做到极致，同时建立智能化基础设施

### 2.1 协议深度提升 (复用 OVERTAKE Phase 2)

| 协议 | 目标行数 | 关键新增 |
|------|---------|---------|
| Modbus | 8000 | FC23 增强、Modbus Server 回调、Gateway 模式、性能 10K req/s |
| Siemens | 6000 | S7-1200/1500 优化块读写、DB 发现、PLCSIM 对接 |
| Mitsubishi | 6000 | 随机读写、批量优化、标签访问、FX5U MC 协议 |
| Omron | 6000 | FINS 串口、NX/NJ 专用协议 |
| AllenBradley | 5500 | Tag Fragmented、数组、UDT、EtherNet/IP 显式消息 |

### 2.2 B 级协议批量提升

Beckhoff ADS / Panasonic Mewtocol / Keyence KV / Yaskawa Memobus / Yokogawa / Inovance / Fatek / LsElectric / GeSrtp / Delta — 每个模块提升到 Usable+ 级别。

### 2.3 🔮 智能基座 — Nexus.Brain (核心创新)

这是超越 HSL 的**关键差异化起点**。建立智能化基础设施层：

```
Nexus.Core/
├── Diagnostics/
│   ├── DiagnosticEngine.cs          — 统一诊断引擎
│   ├── IDiagnosticRule.cs           — 诊断规则接口
│   └── DiagnosticReport.cs          — 诊断报告模型
├── Intelligence/
│   ├── ConnectionProfiler.cs        — 连接行为画像
│   ├── ResponseTimeTracker.cs       — 响应时间追踪
│   └── AnomalyDetector.cs           — 基于统计的异常检测
```

**智能特性 1: 连接自诊断**

```csharp
// 连接失败时，自动诊断原因并给出中文建议
var result = client.Connect();
if (!result.IsSuccess)
{
    var diagnosis = DiagnosticEngine.Diagnose(result);
    // 输出: "连接超时(192.168.1.100:502)。
    //        可能原因: 1.设备未上电 2.IP地址错误 3.防火墙阻断
    //        建议: 先 ping 192.168.1.100 确认网络可达"
    Console.WriteLine(diagnosis.Suggestion);
}
```

**智能特性 2: 响应时间画像**

```csharp
// 自动追踪每次读写的响应时间，建立性能基线
var tracker = new ResponseTimeTracker(client);
tracker.Start();

// 一段时间后自动分析
var profile = tracker.GetProfile();
// 输出: "Modbus TCP 192.168.1.100 响应画像:
//        平均 3.2ms, P99 12ms, 超时率 0.1%
//        异常: 06/08 14:30 响应骤增至 200ms（可能原因：PLC 扫描周期阻塞）"
```

**智能特性 3: 报文异常检测**

```csharp
// 基于统计模型自动检测异常报文
var detector = new AnomalyDetector(client);
detector.OnAnomalyDetected += (anomaly) =>
{
    // "检测到异常: 连续 5 次读返回相同值，设备可能处于停机状态"
    // "检测到异常: CRC 错误率从 0% 升至 2.3%，可能存在电磁干扰"
};
```

**Wave 2 完成标志：**
- Top 5 协议行数总和 > 30K
- 测试 5000+
- `DiagnosticEngine` 可对 Modbus/S7 连接失败给出智能建议
- `ResponseTimeTracker` 可在 WPF 中展示性能画像

---

## Wave 3: 智能诊断 + 生态发布 (Month 7-9)

> 目标：让 Nexus 成为"会说话"的工控库，正式公开发布

### 3.1 智能诊断体系完善

**智能特性 4: 协议报文自动解析器**

```csharp
// 输入任意十六进制报文，自动识别协议并解析
var analyzer = new ProtocolAnalyzer();
var result = analyzer.Analyze("00 01 00 00 00 06 01 03 00 00 00 0A");
// 输出: "Modbus TCP Response | Unit=1 | FC=03 | 起始地址=0 | 数量=10 | 数据: ..."
```

- 支持自动识别: Modbus TCP/RTU/ASCII、S7、MC3E、FINS、CIP
- WPF 调试器中集成：粘贴报文 → 即时解析
- 支持批量报文文件分析

**智能特性 5: 设备指纹识别**

```csharp
// 连接后自动探测设备类型
var fingerprint = DeviceFingerprint.Probe(client);
// 输出: "设备指纹匹配: 西门子 S7-1200 (固件 V4.5)
//        支持特性: S7通信、DB读写、PLC控制
//        推荐配置: Rack=0, Slot=1, TSAP=01.00"
```

**智能特性 6: 智能地址推荐**

```csharp
// 根据设备类型推荐常用地址
var addresses = AddressAdvisor.GetRecommendedAddresses(deviceInfo);
// 输出: "S7-1200 常用地址:
//        DB1.DBW0  — 系统状态字 (UInt16)
//        DB1.DBD2  — 温度 (Float)
//        DB1.DBX6.0 — 运行标志 (Bool)"
```

### 3.2 正式开源发布

| 交付物 | 描述 |
|--------|------|
| NuGet 全量发布 | Top 5 + B-tier 15+ 个包在 nuget.org |
| 文档站 v1.0 | DocFX 生成，含快速入门、协议文档、API 参考、迁移指南 |
| 示例仓库 | Nexus.Samples — 30+ 示例覆盖主要场景 |
| 视频教程 | Bilibili + YouTube：5 分钟上手系列 |
| GitHub Release | v1.0 正式版，MIT 协议，Contributing 指南 |

### 3.3 HSL 迁移工具

```csharp
// Nexus.Tools.HslMigrator — 命令行迁移工具
// 扫描 C# 源码，自动识别 HSL API 调用并建议 Nexus 替换
dotnet run --project Nexus.Tools.HslMigrator -- --source ./MyApp --dry-run
// 输出:
//   Program.cs:42 - HSL: new ModbusTcpNet("192.168.1.100")
//   → Nexus: new ModbusTcpClient("192.168.1.100", 502, station: 1)
//   Found 47 replacements across 8 files. Run with --apply to apply.
```

**Wave 3 完成标志：**
- Nexus v1.0 正式发布在 GitHub + NuGet
- 智能诊断可在 WPF 中展示
- 设备指纹识别覆盖 Top 5 协议
- 1000+ NuGet 下载（首月）
- 测试 6000+

---

## Wave 4: 广度扩展 + AI 特性 (Month 10-12)

> 目标：协议覆盖面逼近 HSL，引入真正的 AI 能力

### 4.1 新协议实现 (复用 OVERTAKE Phase 3)

**国内高需求协议：**
| 协议 | 重要性 | 行数目标 |
|------|--------|---------|
| Schneider Modicon M580/M340 | ⭐⭐⭐⭐⭐ | 2000 |
| S7 Plus (TIA Portal) | ⭐⭐⭐⭐ | 1500 |
| Omron NX/NJ | ⭐⭐⭐⭐ | 1500 |
| Inovance H5U/EasyWeb | ⭐⭐⭐⭐ | 1000 |
| Xinje XC/XG 完整 | ⭐⭐⭐ | 800 |
| Delta DVP 完整 | ⭐⭐⭐ | 800 |

**行业专用协议：**
| 协议 | 领域 | 行数目标 |
|------|------|---------|
| DNP3 | 电力 | 1500 |
| IEC 61850 | 智能变电站 | 2000 |
| BACnet MSTP | 楼宇自动化 | 1200 |
| CANopen | 汽车/自动化 | 1500 |

**IoT 增强：**
- MQTT 5.0 完整支持 + 内置 Broker
- OPC UA 安全模式 + 证书管理 + 方法调用
- IEC 104 平衡式传输 + 总召唤

### 4.2 🔮 AI 特性 — Nexus.AI

**智能特性 7: 自然语言设备查询 (NLQ)**

```csharp
// 用自然语言查询设备数据
var engine = new NexusQueryEngine(client);
var result = engine.Query("读一下1号变频器的运行频率和电流");
// → 自动映射到: client.ReadFloat("40100"), client.ReadFloat("40102")
// → 返回: "运行频率: 50.0 Hz, 电流: 12.5 A"

result = engine.Query("把2号泵的启动命令置为ON");
// → 自动映射到: client.Write("00001", true)
```

技术路径：
- 本地规则引擎 + 嵌入式意图识别（无需联网）
- 用户自定义地址别名表
- 支持中英文查询

**智能特性 8: 自适应轮询优化**

```csharp
// 自动学习数据变化模式，优化轮询频率
var optimizer = new AdaptivePollingOptimizer();
optimizer.AddPoint("40001", interval: 1000);  // 初始 1 秒
optimizer.Learn(TimeSpan.FromHours(1));
// → 自动调整为: 每 5 秒轮询（数据变化率 < 1%/min）
// → 但当检测到快速变化时自动加速到 200ms
```

**智能特性 9: 报文模式学习**

```csharp
// 学习正常通信模式，检测异常
var learner = new TrafficPatternLearner(client);
learner.Learn(TimeSpan.FromHours(24));  // 学习 24 小时正常模式

// 之后自动检测偏差
learner.OnAnomalyDetected += (anomaly) =>
{
    // "异常: 通常该设备每 100ms 响应一次，但最近 5 分钟平均 800ms"
    // "异常: 寄存器 40100 通常在 0-100 范围波动，当前值 65535（可能传感器故障）"
};
```

**Wave 4 完成标志：**
- 协议总数 65+
- Nexus.AI 包含 NLQ + 自适应轮询 + 模式学习
- 测试 8000+
- 源码 120K+ 行

---

## Wave 5: 差异化护城河 + 社区 (Month 13-15)

> 目标：建立 HSL 完全无法复制的功能壁垒

### 5.1 🔮 虚拟 PLC 生态 (HSL 完全没有)

```
Nexus.VirtualPlc/
├── Core/                    — 虚拟 PLC 框架
│   ├── IVirtualPlc          — 统一接口
│   ├── MemoryModel          — 线圈/寄存器/DB/定时器/计数器
│   ├── LadderEngine         — 简易梯形图引擎
│   └── ScenarioRunner       — JSON 场景脚本
├── S7/                      — S7 虚拟 PLC（完整协议栈模拟）
├── Modbus/                  — Modbus 虚拟 PLC
├── Mitsubishi/              — 三菱虚拟 PLC
└── Omron/                   — 欧姆龙虚拟 PLC
```

场景脚本：
```json
{
  "name": "温度 PID 控制",
  "plc": "S7-1200",
  "initial_state": { "DB1.DBD0": 25.0, "DB1.DBD4": 0.0 },
  "rules": [
    { "trigger": "DB1.DBX10.0", "action": "set DB1.DBD4 = pid(DB1.DBD0, setpoint=50)" },
    { "trigger": "every 1000ms", "action": "DB1.DBD0 += random(-0.5, 0.5)" }
  ]
}
```

**价值：**
- 开发者无需硬件即可完整调试上位机
- 培训/教育场景：学生用虚拟 PLC 学习工控通信
- CI/CD 自动化测试：每次提交都跑虚拟 PLC 集成测试
- 预置 20+ 工业场景（传送带、电机控制、PID 温控、仓储物流……）

### 5.2 🔮 数据采集引擎

```csharp
public class DataAcquisitionEngine : IDisposable
{
    void AddPoint(string id, IReadWriteDevice device, string address,
                  string dataType, int intervalMs);
    void Start();
    event EventHandler<DataPointChangedEventArgs> OnDataChanged;
    IDataSink DataSink { get; set; }
}

// 内建存储后端
SqliteDataSink      — SQLite 存储
CsvDataSink         — CSV 文件滚动
InfluxDbDataSink    — InfluxDB 时序数据库
MqttDataSink        — MQTT 转发（对接 Grafana）
```

### 5.3 🔮 协议网关

```csharp
// 一行代码桥接不同协议
ProtocolBridge.CreateModbusToMqtt(modbusClient, mqttClient, "factory/line1/");
ProtocolBridge.CreateS7ToOpcUa(s7Client, opcUaServer, "ns=2;s=PLC1.");
ProtocolBridge.CreateModbusToRedis(modbusClient, redisClient, "device:");
```

### 5.4 🔮 报文录制/回放/分析

```csharp
// 录制现场报文 → 回放到开发环境 → 离线调试
var recorder = new PacketRecorder();
recorder.Attach(client);
recorder.StartRecording("现场调试_20270115.jsonl");
// ... 现场操作 ...
recorder.StopRecording();

// 回放
recorder.Replay("现场调试_20270115.jsonl", virtualPlc);

// 自动分析
var analysis = recorder.Analyze("现场调试_20270115.jsonl");
// "总报文: 1247 | 平均响应: 3.2ms | 异常: 3次超时, 1次CRC错误"
```

### 5.5 社区建设

| 活动 | 描述 |
|------|------|
| GitHub Discussions | 问答区，替代 QQ 群（HSL 的痛点） |
| 贡献者指南 | 清晰的 PR 流程，协议贡献模板 |
| 定期 Release Notes | 每月版本更新，变更日志 |
| 工控论坛合作 | 与中华工控网、工控人家园合作推广 |
| 企业用户支持 | 提供 SLA 支持选项（开源免费 + 付费企业支持） |

**Wave 5 完成标志：**
- 虚拟 PLC 生态可用（4 种 PLC + 20 场景）
- 数据采集引擎 + 3 种存储后端
- 协议网关覆盖 Modbus/S7/MQTT/OPC UA
- GitHub Star 1000+
- NuGet 下载 1 万+

---

## Wave 6: 工业级 AI + 垂直方案 (Month 16-18)

> 目标：从"通信库"进化为"智能工业数据平台"

### 6.1 🔮 Nexus.Copilot — AI 编程助手

```csharp
// IDE 集成：在 Visual Studio / VS Code 中提供 AI 补全
// 用户输入注释: "// 读取3号炉的温度和压力，温度超限则报警"
// Copilot 自动生成:
var temp = client.ReadFloat("DB1.DBD0");
var pressure = client.ReadFloat("DB1.DBD4");
if (temp.IsSuccess && temp.Content > 120.0f)
{
    alarmService.Trigger("3号炉温度超限", temp.Content);
}
```

实现路径：
- 训练专用小模型（基于 Nexus API 文档 + 示例代码）
- VS Code Extension + Visual Studio Extension
- 上下文感知：根据已连接设备类型推荐 API

### 6.2 🔮 预测性维护模块

```csharp
// 基于时序数据的设备健康评估
var predictor = new PredictiveMaintenance(client);
predictor.Watch("40100", "电机温度");
predictor.Watch("40102", "振动幅度");
predictor.Watch("40104", "电流");

var health = predictor.GetHealthReport();
// "电机健康报告:
//   敼体评分: 82/100 (良好)
//   温度趋势: 过去7天上升 3°C (注意)
//   振动趋势: 异常 (过去3天出现间歇性峰值)
//   预测: 按当前趋势，轴承可能在 15-20 天后需要维护
//   建议: 安排下周停机检查轴承"
```

### 6.3 垂直行业方案包

| 方案 | 描述 |
|------|------|
| Nexus.Factory | 数字工厂模板：多设备采集 + 看板 + 报警 |
| Nexus.Energy | 能源管理：电表采集 + 功率分析 + 峰谷优化 |
| Nexus.Building | 楼宇自控：BACnet + Modbus + 空调照明集成 |
| Nexus.Water | 水务：PLC + RTU + IEC104 + 泵站监控 |
| Nexus.Robot | 机器人集成：多品牌机器人统一接口 + 轨迹管理 |

### 6.4 Web 管理平台

```
Nexus.Web/
├── REST API          — 设备读写/监控/报警
├── SignalR Hub       — 实时数据推送
├── Blazor Dashboard  — Web 监控看板
└── gRPC Gateway      — 高性能跨语言调用
```

**Wave 6 完成标志：**
- Nexus.Copilot VS Code 扩展可用
- 预测性维护模块有 3+ 设备类型模型
- 2+ 垂直行业方案可演示
- Web 管理平台 MVP
- NuGet 下载 5 万+

---

## Wave 7: 平台化 + 国际化 (Month 19-21)

> 目标：从中国走向全球，成为跨语言跨平台解决方案

### 7.1 多语言绑定

```
Nexus.Python      — Python 绑定 (pybind11 / CFFI)
Nexus.Node        — Node.js 绑定 (Node-API)
Nexus.Go          — Go 绑定 (cgo)
```

技术路径：通过 gRPC/FlatBuffers 跨语言通信层，核心逻辑仍用 C#，绑定层为薄 wrapper。

### 7.2 云原生支持

```yaml
# Kubernetes 部署模板
apiVersion: apps/v1
kind: Deployment
metadata:
  name: nexus-gateway
spec:
  template:
    spec:
      containers:
      - name: nexus
        image: nexus/gateway:latest
        env:
        - name: Nexus__Devices__0__Type
          value: ModbusTcp
        - name: Nexus__Devices__0__Host
          value: "192.168.1.100"
```

- Helm Chart
- Docker Compose 模板
- Azure IoT Edge Module
- AWS Greengrass Component

### 7.3 国际化

- 全英文文档站 + 中文文档站（双语言）
- 英文社区（Discord + Reddit r/plc）
- 国际工控展会曝光
- ISA（国际自动化学会）标准对标

**Wave 7 完成标志：**
- Python/Node.js 绑定可用
- 云原生部署模板
- 双语文档站
- NuGet 下载 20 万+

---

## Wave 8: 事实标准 (Month 22-24)

> 目标：成为 .NET 工控通信的事实标准

### 8.1 行业认证

- 功能安全认证（IEC 61508 适合 SIL 等级）
- 工业安全认证（IEC 62443）
- 代码签名证书

### 8.2 企业级特性

- 集群模式：多实例负载均衡 + 故障切换
- 审计日志：所有操作可追溯
- 权限管理：基于角色的设备访问控制
- 配置中心：远程设备配置管理

### 8.3 生态完善

- 100+ 协议支持
- 50+ 虚拟 PLC 场景
- 100+ 示例代码
- 10+ 垂直行业方案
- 官方培训认证体系

**Wave 8 完成标志：**
- NuGet 下载 50 万+
- GitHub Star 5000+
- 协议 80+，测试 10000+
- 至少 3 家企业用户公开背书

---

## 智能化特性路线图总览

| Wave | 智能特性 | 类型 |
|------|---------|------|
| 2 | 连接自诊断 | 规则引擎 |
| 2 | 响应时间画像 | 统计分析 |
| 2 | 报文异常检测 | 统计分析 |
| 3 | 报文自动解析器 | 规则引擎 |
| 3 | 设备指纹识别 | 模式匹配 |
| 3 | 智能地址推荐 | 规则引擎 |
| 4 | 自然语言查询 (NLQ) | NLP + 规则 |
| 4 | 自适应轮询优化 | 统计学习 |
| 4 | 报文模式学习 | 统计学习 |
| 6 | AI 编程助手 | ML 模型 |
| 6 | 预测性维护 | ML 模型 |

---

## 执行策略

### 并行工作流

```
Stream A (协议)  ──── 持续推进协议深度/广度，按优先级流水线执行
Stream B (智能)  ──── 智能特性独立迭代，依赖协议 API 但不阻塞协议开发
Stream C (生态)  ──── 文档/示例/NuGet/社区，跟随协议和智能特性节奏
Stream D (WPF)   ──── 调试器持续增强，集成新协议和智能特性展示
```

### 每月节奏

```
Week 1: 规划 — 确认本月目标，分解任务
Week 2-3: 执行 — 并行开发，每日 build + test
Week 4: 集成 — 合并、测试、文档、Release Notes
```

### 质量门禁（每个 Wave 必须通过）

```
✅ dotnet build Nexus.slnx          — 零警告
✅ dotnet test Nexus.slnx           — 全部通过
✅ 新代码有对应测试                  — 覆盖率 > 80%
✅ 文档同步更新                      — 无过时文档
✅ 无 NotImplementedException        — 零容忍
✅ 性能无回归                        — BenchmarkDotNet 对比
```

---

## 关键指标仪表盘

| 指标 | 当前 | W1 后 | W3 后 | W5 后 | W8 后 |
|------|------|-------|-------|-------|-------|
| 协议数 | 40 | 40 | 55 | 65 | 80+ |
| 测试数 | ~1300 | 3000 | 6000 | 8000 | 10000+ |
| 源码行数 | ~48K | 60K | 90K | 120K | 150K+ |
| 智能特性 | 0 | 0 | 3 | 6 | 11 |
| 虚拟 PLC 场景 | 0 | 0 | 0 | 20+ | 50+ |
| NuGet 下载 | 0 | 1000 | 1万 | 5万 | 50万+ |
| 文档页 | 10 | 50 | 150 | 200 | 300+ |
| 示例代码 | 2 | 30 | 50 | 70 | 100+ |

---

## 风险与应对

| 风险 | 影响 | 应对 |
|------|------|------|
| AI 特性依赖模型训练，可能不够准确 | 智能化卖点打折扣 | 先用规则引擎，逐步引入 ML；明确标注"建议"而非"结论" |
| HSL 快速迭代追赶 | 差异化被抹平 | 智能化和虚拟 PLC 是结构性优势，HSL 的架构难以快速复制 |
| 协议实现法律风险 | 开源合规问题 | 严格遵守 clean-room 原则，只参考公开协议规范 |
| 真实设备验证困难 | 生产可信度不足 | 与高校/工厂合作，建立设备验证联盟 |
| 社区增长缓慢 | 影响开源飞轮 | 主动推广：技术博客、视频教程、工控论坛、会议演讲 |

---

## 验证方式

每个 Wave 完成时的统一验证：

```bash
# 1. 构建 + 测试
dotnet build Nexus.slnx
dotnet test Nexus.slnx

# 2. 特定协议验证
dotnet test tests/Nexus.Modbus.Tests
dotnet test tests/Nexus.Siemens.Tests
dotnet test tests/Nexus.Mitsubishi.Tests

# 3. WPF 构建验证
dotnet build src/Nexus.App

# 4. NuGet 打包验证
dotnet pack src/Nexus.Modbus
dotnet pack src/Nexus.Siemens

# 5. 智能特性验证（Wave 2+）
dotnet test tests/Nexus.Core.Tests --filter "FullyQualifiedName~Diagnostic"
dotnet test tests/Nexus.Core.Tests --filter "FullyQualifiedName~Intelligence"
```
