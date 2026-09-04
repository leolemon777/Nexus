# 超越 HSL 拓展调研（2026-08-26）

> 目的：回答"Nexus 后面怎么拓展才能超过 HSL"。
> 方法：本地盘点 `i3195/HslCommunication-src`（Demo 12.9.1 + HSLSharp v5.x XML 文档）+ Nexus-Rust 当前基线（README 协议矩阵 / PRODUCT_COMPLETION_ROADMAP / gap-analysis）+ 网络调研（HSL 社区口碑、竞品格局、2025-2026 行业趋势、Rust 工业协议生态）。
> 本文档只做调研与战略建议，不改代码。

---

## 一句话结论

**不要按 HSL 的地图打仗。** HSL 是"给程序员引用的闭源通信库"（46 族 / 155+ 设备类 / 43 虚拟服务器，靠作者一人实机打磨，零单测）；Nexus 是"给现场工程师用的开源三合一工作台"。超过 HSL 的路径不是把设备类数量从 25 堆到 155，而是：**① 用真机证据体系把"可信度"做成 HSL 永远给不了的东西；② 攻占 HSL 和 Modbus Poll 都没做好的"点表工程 + 诊断"层；③ 借 UNS/Sparkplug B 趋势做北向网关；④ 库赛道用开源 Rust core 提供 HSL 的免费替代**。协议广度只在高价值缺口（机器人/CNC、写入路径、从站仿真）补刀。

---

## 1. HSL 能力地图（实测盘点，v12.9.1）

### 1.1 规模

| 类别 | 数量 | 说明 |
|---|---|---|
| 协议族 | ~46 | PLC 28 族 + 机器人/CNC 9 族 + 仪表/电力/物联 ~17 族 |
| 设备通信类 | 155+ | 客户端 ~115，虚拟服务器 43 |
| 三菱 MC | 20 变体 | 最全：MC Binary/Ascii/Udp/MC-R/A-1E/A-3C/CIP/FxSerial/FxLinks… |
| 西门子 | 15 变体 | S7 全系、Fetch/Write、PPI、MPI、S7-Plus、WebApi、DTU |
| 欧姆龙 | 13 变体 | FINS TCP/UDP、CIP、HostLink、C-Mode |
| AB | 9 变体 | CIP、ConnectedCip、MicroCip、PCCC、SLC、DF1、CIP Browser |
| 机器人/CNC | 13 变体 | EFORT、KUKA、安川 YRC、ABB WebApi、发那科（机器人+0i CNC）、埃斯顿、现代、雅马哈 |
| 半导体 | SECS/GEM | SecsHsms 客户端+Server |
| 仪表电力 | DLT645/698、IEC104、CJT188、RKC、宇电、托利多、SAM 等 ~17 | |

### 1.2 非协议基础设施

LogNet 日志、FileNet 文件仓库、自研 FTP、嵌入式 HTTP Server、**自研 MQTT 全家桶**（Server/Client/RPC/文件分发）、Redis 客户端、WebSocket、私有协议（NetSimplify/NetComplex/NetPush）、DTU 异形客户端集中管理、软件自动更新、SMTP 邮件、流水号生成、RSA/AES/授权体系、ByteTransform 字节序、线程安全容器、傅立叶/PID、HslReflection 对象映射、HslControls 控件库（拆分收费）。

### 1.3 架构

v12 引入 Pipe 管道体系（同一协议类可插拔跑在 TCP/UDP/串口/DTU/MQTT/SSL 通道上）+ Core.Address 地址族 + DeviceCommunication 通用基类；错误统一 `OperateResult<T>` 不抛异常。

### 1.4 短板（调研确认）

1. **闭源收费**：v7.0.1 之后闭源，未授权运行 8 小时退出；企业商用需购买授权——社区最大争议点，也是大量用户寻找替代品的原因。
2. **零单元测试、零 CI**：整个仓库只有 Demo 工程，回归靠作者人肉。
3. **无 OPC UA、无 Sparkplug B**：MQTT 为自研实现（非标准栈）；HSLSharp 里的 OPC UA 是捆绑的第三方。
4. **Windows/.NET Framework 4.6.1 为主**，无 Linux/容器化叙事。
5. **数据库层薄**（仅 SqlServer + 文本）；无时序库、无报警系统模块。
6. **无 GUI 工作台**：Demo 是 WinForms 表单集合，无点表/趋势/证据概念；可视化控件单独收费。
7. **虚拟服务器质量参差**：核心协议完善，小众协议仅客户端。
8. **无网关编排**：PLC→MQTT 转发只是 Demo 示例，非产品。

---

## 2. Nexus 现状（v0.5.2，2026-08-25 批次后）

### 2.1 协议矩阵（README + rust-core 模块）

- **全读写 + 从站仿真**（深度完成）：Modbus（TCP/UDP/RTU/ASCII 主+从+轮询）、三菱 MC 12 变体 + 虚拟从站、西门子 10 通道（S7/FW/PPI/WebAPI/USS/RK512，CPU 启停/SZL）+ S7/PPI 从站。
- **只读首轮/软件验证**（S2-S4a 证据，L2 实机待验）：AB ENIP/CIP、Beckhoff ADS、Keyence HostLink、LS XGT、Panasonic MEWTOCOL、FATEK、Fuji SPH、GE SRTP；HostLink 两个通道；MQTT 3.1.1 只读订阅；IEC104、DNP3 只读 Master；DLT645、CJT188；BACnet/IP、KNXnet/IP；PROFINET DCP（pn_dcp.rs）。
- **地址 profile 层**：汇川/信捷/台达（走 Modbus 映射）。
- 通讯变体注册表 53 个；612 Rust 测试 + 309 Electron 测试全绿。

### 2.2 工程与产品能力（HSL 完全没有的）

三列工作台 UI + 协议报文面板、点表（CSV 批量导入 point-import.js）、轮询规划（poll-planner.js）、趋势、安全写入（写前读旧值/确认/回读/JSONL 审计）、项目文件 .nexus.json 版本化+脱敏导出、故障诊断包、长稳 soak、发布工程（SBOM/SHA-256/回滚）、真机证据分级（S1-S4/L2）与 evidence 目录。

### 2.3 已知缺口（自家 roadmap 记录）

- R2 真机验收矩阵只完成一部分（多条协议仍是"软件已实现/实机待验证"）。
- R3 formal 发布、installer、签名未完成。
- OPC UA 被 openssl-sys 阻塞（复活条件：纯 Rust TLS 或 open62541 FFI）。
- gap-analysis G1-G16 中若干项（点表编辑器深化、地址发现、Test Center 报文构建器、多会话多数据区等）分布在阶段池。

---

## 3. 正面对比：Nexus vs HSL vs Modbus Poll

| 维度 | HSL 12.9 | Modbus Poll/Slave | Nexus v0.5.2 |
|---|---|---|---|
| 形态 | 闭源 .NET 库 + WinForms Demo | 闭源收费 GUI（仅 Modbus） | 开源免费 GUI + Rust core |
| 协议广度 | 46 族/155+ 类 | 1 族 | ~25 族/53 变体 |
| 协议深度 | 全读写、多年实机 | Modbus 全功能 | 5 族全读写+从站；其余只读首轮 |
| 从站仿真 | 43 个 | 1 个（Modbus） | 4-5 个（Modbus/MC/S7/PPI） |
| 单元测试/CI | 无 | 无 | 612+309 全绿 + golden vectors |
| 真机证据 | 无体系（作者口头/issue） | 无 | S1-S4/L2 分级 + evidence |
| 点表工程 | 无 GUI 概念 | 弱（寄存器窗口） | CSV 导入/保存；发现、模板待做 |
| 诊断 | 无 | 基础 | 报文面板+诊断包+Ping；超时助手待做 |
| OPC UA / Sparkplug | 无/无 | 无/无 | 无（阻塞）/无 ← 双空白，趋势赛道 |
| 楼宇/电力 | IEC104/DLT645/CJT188 | 无 | IEC104/DNP3/DLT645/CJT188/**BACnet**/**KNX**（领先） |
| 机器人/CNC | 13 变体（领先） | 无 | 无（最大品类缺口） |
| 安全写入 | 普通读写 | 普通读写 | 写前读旧值+确认+回读+审计（独有） |
| 授权风险 | 高（软著+8h 限制） | 中（商业许可） | 无 |

**结论**：协议广度 HSL 遥遥领先但护城河是"作者时间"；可信度（测试/证据/开源）Nexus 领先且 HSL 结构性无法追赶；楼宇/电力专业赛道 Nexus 反超；机器人/CNC 与北向 IT 生态是双空白。

---

## 4. 外部环境调研要点

### 4.1 社区对 HSL 的真实态度

- 争议集中在：**收费策略突变**（v7.0.1 后闭源）、**未授权 8 小时退出**、商用法律风险、"只想用通信功能却被迫带上一堆日志/邮件/流水号"。
- 技术吐槽：连接开关耗时影响读取速率（作者自述长短连接权衡）；个别协议实现 bug（GE-SRTP 地址、汇川 EIP 不支持等 issue 常年挂着）。
- 机会信号：**大量中文开发者在找 HSL 的免费/开源替代**（S7netplus/NModbus 常被推荐，但都只覆盖单协议）。

### 4.2 竞品格局

- 单协议工具红海：Modbus Poll/Slave、MThings、ModbusTool、QModMaster……全都只做 Modbus。
- 多协议 GUI 属于空白地带：Kepware 是"网关+驱动"而非"调试工作台"，且 PTC 定价引发替代潮（FlowFuse/NeuronEX/i-flow 都在抢）。
- 现场方法论共识："通用工具证明物理链路 → 协议专用工具验证语义 → 沉淀日志/脚本"。Nexus 的三合一 + 报文面板 + 证据导出恰好覆盖全链路。

### 4.3 趋势（2025-2026）

- **UNS（统一命名空间）+ MQTT Sparkplug B** 成为企业数据架构主流叙事；OPC UA FX 面向现场层，两者互补。
- 边缘网关市场因 Kepware 定价问题出现替代窗口。
- 现场痛点（多来源交叉验证）：点表手工管理易错（CSV 来回倒）、断连/数据缺口/时间戳漂移、需要 Wireshark 级报文分析能力排查协议故障。

### 4.4 Rust 生态可借力

open62541 绑定（OPC UA，C 库 FFI，绕开 openssl-sys 问题）、async-opcua（纯 Rust，需评估 TLS）、rust-ethernet-ip / libplctag（AB）、bacnet-rs、canopen-rs、EtherCrab（EtherCAT 主站）。**含义**：协议广度扩张不必全部手写，混合"自研核心 + FFI 借力"可提速，但须按真机证据分级标注来源。

---

## 5. "超过 HSL"的定义（战略选择）

HSL 的护城河 = 20 年协议数量 × 作者实机时间。正面拼数量必输（155 类 × 实机验证 = 无限人力）。超越的定义应该是三维的：

1. **可信度维度**（结构性优势，HSL 无法复制）：每个协议都有 golden vectors + 自动测试 + S1-S4/L2 真机证据表 + 开源可审计。对商业用户这是"敢用"的理由，对 HSL 是"不敢换"的理由。
2. **工程师体验维度**（HSL 无 GUI 基因）：点表工程 + 诊断 + 安全写入 + 报文面板 + 一键证据导出。让"用 HSL 写代码 30 分钟才能验证的事，Nexus 30 秒点出来"。
3. **北向生态维度**（双空白 + 趋势顺风）：Sparkplug B / OPC UA Server / WebSocket 推出——把 Nexus 从"调试工具"升级为"调试 + 轻量边缘网关二合一"，这是 HSL（无 Sparkplug、OPC UA 靠第三方）和 Modbus Poll（封闭单协议）都没站住的位置。

---

## 6. 拓展路线建议（四个批次，按 ROI 排序）

### B1. 可信度收口（最高优先，不用买硬件也能推进大半）

把现有优势兑现成市场语言，对应 roadmap 的 R2/R3：

1. R2 实机矩阵：优先用手头已有硬件（Modbus TCP/RTU、三菱 FX/MC、西门子 S7/PPI、欧姆龙 FINS）把 L2 证据补满；SC09 重插后补 D0 读值。
2. 只读 → 读写升级：ENIP/CIP、ADS、Keyence、LS XGT、FATEK、MEWTOCOL 逐族过"写前读旧值/确认/回读/审计"安全写入框架（框架已就绪，边际成本低，每升级一族 README 矩阵就亮一格）。
3. 从站仿真补位：不必追 HSL 的 43 个；按"现场最常被仿真的设备"补 5-8 个：ENIP/CIP 从站（AB 仿真）、FINS 从站（欧姆龙）、IEC104 Outstation 已有脚本级 → 产品化、DLT645 表计仿真（走串口从站框架）、HostLink 从站。
4. R3 发布工程收尾：installer、签名、用户手册、兼容列表。

### B2. 点表工程中心（VOC 最大痛点，HSL/Modbus Poll 双空白）

1. 点表编辑器完整版：地址/名称/类型/字节序/比例/偏移/单位/着色阈值（G4+G5 回填）。
2. **活动地址发现**：扫描地址段 + 变化检测识别"活"寄存器（gap-analysis 已列为差异化项，Modbus Poll 没有，HSL 无 GUI 概念）。
3. 点表模板库：按"设备型号"沉淀社区点表（如某某变频器 40001-4xxxx 映射），配合地址 profile 层已有资产（汇川/信捷/台达）。
4. 点表跨协议复用：同一份点表在 Master/Slave 两端使用（Slave 直接按点表生成仿真数据源，含变化脚本）——这是三合一架构的独有化学反应，任何竞品做不到。

### B3. 北向网关 + UNS 赛道（趋势顺风，差异化最大）

1. **MQTT Sparkplug B 发布端**：把 Master 轮询的点表以 Sparkplug B metric 模型发布到任意 Broker（rumqttc 类栈），Nexus 一键变成"最小 UNS 边缘节点"。已有 MQTT 3.1.1 只读订阅 + golden vectors 作底。
2. **OPC UA Server 复活**：按 opcua-blocked.md 的复活条件走 open62541 FFI（C 库 Windows 预编译，绕开 openssl-sys），把点表暴露为 OPC UA 节点——直接吃 gap-analysis G6 标注的"工业 4.0 集成需求"。
3. WebSocket/CSV 实时推送（G6）：浏览器/Node-RED 可订阅 Nexus 数据。
4. 注意保持 roadmap 的安全边界：北向服务默认仅回环 + 显式开启 + 鉴权。

### B4. 协议广度补刀（只打高价值缺口，不撒胡椒面）

1. **机器人/CNC**（HSL 领先最大的品类）：发那科 FOCAS（CNC+机器人双用途，以太网库）、KUKA EthernetKRL/TCP、安川 YRC1000。机器人调试现场恰好缺一个"报文级"通用工具，与三合一定位契合。
2. **SECS/GEM**（半导体，HSL 有 client+server）：中国半导体设备国产化潮，需求上升。
3. 楼宇/电力继续领先：BACnet 写入/COV、DNP3 控制、DLT698、IEC104 控制+带时标——在这些专业赛道把 HSL 甩开两代。
4. 明确不做：EtherCAT/PROFINET IO 实时现场总线（需专用硬件，roadmap 已声明不承诺）、HSL 式"日志/邮件/流水号"大杂烩（保持工作台纯粹性）。

### 横向原则（贯穿所有批次）

- 库赛道副产品：Rust core 本身就是"开源 HSL 替代"的种子。等 B1 读写族多了以后，把 JSONL sidecar 接口文档化 + crates.io 发布计划提上日程（零成本收获"找 HSL 替代"的搜索流量）。
- 每个新协议默认走 golden vectors + S 分级，实机缺席绝不宣称完成（既有纪律）。
- HSL 的 Pipe 管道思想值得吸收：Nexus 已有 PDU/transport 分离，可继续把"通道"(TCP/串口/DTU) 与"协议"正交化，为将来 DTU 透传通道铺路。

---

## 7. 风险与边界

| 风险 | 应对 |
|---|---|
| B2-B4 吸引大量工作量冲击 R2/R3 收口 | 纪律：B1 未收口前不开新协议族；B2/B3 属于产品能力可并行小步 |
| OPC UA FFI 引入供应链/构建复杂度 | 走 portable 自带预编译 DLL 路线，SBOM 已有工程可承载；先 PoC 再定 |
| Sparkplug B 规范复杂（出生死亡/状态机） | 先支持发布端 metric 子集 + 官方 TCK 验证，不宣称完整网关 |
| 机器人协议多为厂商私有 SDK（FOCAS 需库文件） | 只做以太网明文协议（EthernetKRL/YRC1000 走 HostLink 变体）；FOCAS 标注"需厂家库"或跳过 |
| "超越 HSL"变成数量攀比 | 以三维定义（可信度/体验/北向）度量，不以设备类数量为 KPI |

---

## 8. 参考来源

- 本地：`i3195/HslCommunication-src`（Demo FormPanelLeft.cs 46 组导航树、HslCommunication.xml 148 类型文档、Download 产物）
- 本地：`Nexus-Rust/README.md` 协议矩阵、`docs/PRODUCT_COMPLETION_ROADMAP.md`、`docs/gap-analysis.md`、`docs/opcua-blocked.md`
- HSL 争议与授权：[胡工科技 FAQ](http://www.hsltechnology.cn:7900/Home/FAQ)、[HslCommunication 官网](http://www.hslcommunication.cn/)、[作者博客园自述](https://www.cnblogs.com/dathlin/p/10390311.html)、[GitHub Issues](https://github.com/dathlin/HslCommunication/issues)
- 竞品：[Modbus Poll](https://www.modbustools.com/modbus_poll.html)、[MThings](https://www.cnblogs.com/mthings/articles/19228021)、[Kepware 替代讨论](https://www.reddit.com/r/PLC/comments/t3281s/kepware_alternative/)
- 趋势：[OPC UA FX vs Sparkplug B (2026)](https://iotdigitaltwinplm.com/opc-ua-fx-vs-mqtt-sparkplug-b-unified-namespace-2026/)、[HiveMQ UNS 分析](https://www.hivemq.com/blog/beyond-mqtt-fit-and-limitations-other-technologies-in-uns/)、[Software Toolbox UNS](https://softwaretoolbox.com/resources/what-is-unified-namespace)
- 现场痛点：[Wireshark 工业以太网排障](https://industrialmonitordirect.com/blogs/knowledgebase/wireshark-tutorial-industrial-ethernet-packet-analysis-guide)、[FlowFuse CSV 日志痛点](https://flowfuse.com/blog/2025/10/how-to-log-plc-data-to-csv-files/)
- Rust 生态：[open62541 crate](https://crates.io/crates/open62541)、[async-opcua](https://github.com/freeopcua/async-opcua)、[rust-ethernet-ip](https://crates.io/crates/rust-ethernet-ip)、[EtherCrab](https://github.com/ethercrab-rs/ethercrab)、[libplctag](https://libplctag.github.io/)
