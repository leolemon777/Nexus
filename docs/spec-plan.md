# Nexus-Rust 工业通信工作台 — 完整产品规格计划

> **主索引**。本文件是计划集合的入口,记录产品愿景、阶段划分、依赖关系和进度。
> 每个阶段有独立的详细规格文档(`phase-N-*.md`),按顺序执行。
>
> **对标参考**:
> - `E:\Desktop\Nexus2.0\Nexus\src\Nexus.Modbus\` — .NET 成熟实现(370+ 测试,6 传输,11+2 FC)
> - `E:\Desktop\Nexus2.0\i3195\HslCommunication-src\HslCommunicationDemo\` — HSL Demo(主站/从站/串口调试全产品形态)
> - `E:\Desktop\Nexus2.0\Nexus\src\Nexus.App\` — .NET WPF 调试器(虚拟服务器集群 + 扫描器 + 监控)

---

## 产品愿景

把 `Nexus-Rust` 做成一个**完整的工业通信工作台**,不只是协议库,而是对标 HslCommunication Demo 和专业 Modbus 调试工具(Modbus Poll / Modbus Slave)的**三合一产品**:

```
Nexus 工业通信工作台
├── ① Modbus 主站 (Modbus Master)      ← 类比 Modbus Poll
│   ├── 基础配置:数据类型、地址、校验码、波特率、站号
│   ├── 扫描功能:扫描站号、扫描波特率
│   ├── 指令设置功能
│   └── 连接方式:RS-232 / RS-485 / 网口(TCP/UDP)/ RtuOverTcp / AsciiOverTcp
│
├── ② Modbus 从站模拟 (Modbus Slave)    ← 类比 Modbus Slave 软件
│   ├── 基础操作:赋值、指令设置、置零
│   └── 串口参数:各种站号、波特率、奇偶校验
│
└── ③ 串口调试 (Serial Debug)           ← 类比 HSL 串口调试助手
    ├── 发送与校验:发送波特率、校验码、CRC 校验
    ├── 收发控制:允许接收、允许收发、加校验
    └── 数据处理:解析报文等功能
```

**核心原则**:
- **Rust 是协议权威** —— 所有协议字节(构建、解析、CRC/LRC、MBAP)都在 Rust core 里。
- **Rust 持有 socket** —— TCP/UDP 连接由 Rust core 管理,Electron 只做生命周期。
- **Electron 持有串口句柄** —— 串口(主站/从站/调试)的 COM 句柄归 Electron,Rust 做帧编解码。
- **渲染层永远不能提交任意串口字节** —— 除非是串口调试模式(明确切换)。
- **零运行时依赖** —— Rust core 保持仅 serde/serde_json/thiserror,用同步 `std::net` + 每连接一线程,**不引入 Tokio**。

---

## 当前基线(2026-08-11)

| 维度 | 现状 |
|---|---|
| 产品形态 | 仅 Modbus 主站(部分) |
| 功能码 | 仅 FC03 / FC04(一次性读) |
| 传输 | 仅 Modbus RTU 串口 |
| 写操作 | 无 |
| 轮询 | 无(UI 有占位但 disabled) |
| 数据类型 | 仅 UInt16 |
| 扫描 | 无(UI 有 `扫描站号` 按钮 disabled) |
| 从站模拟 | 无(UI 有 tab 占位) |
| 串口调试 | 无(UI 有 tab 占位) |
| 报文解析 | 无(UI 有 tab 占位) |
| Rust core 模型 | 单线程阻塞,1 请求 → 1 响应,无会话状态 |
| JSONL 命令 | 7 个 |
| IPC handler | 8 个(全部 one-shot) |
| 测试 | ~34 个 Rust + 5 个 Electron 测试文件 |
| 协议版本 | `PROTOCOL_VERSION = 1` |

---

## 已确认的关键约束(来自源码核查)

1. **`RtuFrame` 通用层已 FC 无关** —— 测试里已用 FC16(`0x10`)做过往返;加写功能码**无需改通用帧层**。
2. **`serve()` 是单线程阻塞、1 请求 → 1 响应** —— 轮询订阅需要新的流式协议(不能用当前 Promise 模型)。
3. **`PROTOCOL_VERSION = 1` 新增命令不需升版本** —— 加命令名不算破坏性变更;只有改信封结构才算(阶段 5 轮询需要升 v2)。
4. **`deny_unknown_fields` 在 `RequestEnvelope`** —— 新命令可自由加 payload 字段,但不能加顶层信封字段(否则触发协议升级)。
5. **Electron `serial-service.transact()` 写死了 `exceptionResponseLength=5`** —— TCP 路径必须绕开此文件,Rust 自己收发。
6. **UI 已有占位** —— `#poll-interval`、`连续轮询`、`写入数据`、`扫描站号`、`#display-type`、`#byte-order`、3 个 tab(从站模拟/串口调试/报文解析)都已存在但 disabled。
7. **UI 布局是 3 行 grid**(header / body / statusbar),body 是 2 列(sidebar 206px / module-shell)。module-shell 内有 4 个 tab 按钮(主站/从站模拟/串口调试/报文解析),但只有"主站"有内容,其余 3 个是空壳。
8. **无路由系统** —— `activateConsole()` 函数(main.js:444)是现有的 tab 切换模式,可复制扩展为 `activateView()`。
9. **状态管理是全局变量** —— `let busy` + `const stats` + `connectionPill.dataset.state`;无状态机,无框架。
10. **HSL 的 `HslDebug/` 文件夹是串口调试的完整规格参考** —— `FormSerialDebug`(串口终端)、`FormTcpDebug`/`FormTcpServer`(TCP 调试)、`FormSerialToTcp`/`FormTcpToTcp`(桥接)、`DebugControl`(共享 hex 渲染)、`FormByteTransfer`(字节编解码计算器)。
11. **HSL 的 `UserControlReadWriteDevice` 是主站 7-tab 模式的规格参考** —— 批量读 / 报文读 / 线程测试 / 数据表 / 数据导出 / 模拟 / 远程调试 + 可注入的 `AddSpecialFunctionTab`。
12. **.NET Nexus 的 `VirtualPlcManager` 是多协议从站集群的规格参考** —— 17 个协议虚拟服务器,共享内存,fleet 管理。

---

## 阶段划分

6 个阶段,严格顺序,每阶段结束的**就绪标准**是下一阶段的前提。

| 阶段 | 主题 | 详细文档 | 核心产出 | 工作量 | 状态 |
|---|---|---|---|---|---|
| **1** | Modbus 主站核心 | [phase-1-modbus-master-core.md](./phase-1-modbus-master-core.md) | 全 FC + 6 传输连接方式 + 基础配置 | 大 | 🟡 进行中(核心完成,待补 RtuOverTcp/UDP 命令 + UI 连接方式切换) |
| **2** | Modbus 主站高级 | [phase-2-modbus-master-advanced.md](./phase-2-modbus-master-advanced.md) | 扫描(站号/波特率)+ 指令设置 + 轮询 + 多数据类型 | 大 | ✅ 完成(28 数据类型 + 扫描站号/波特率 + 指令面板 + 轮询) |
| **3** | Modbus 从站模拟 | [phase-3-modbus-slave.md](./phase-3-modbus-slave.md) | Rust 虚拟从站 + 赋值/置零 + 多站号 + 串口参数 | 大 | 🟡 进行中(TCP 从站 + FC01-06/15/16 响应 + 内存区管理 + UI 完成,串口从站待做) |
| **4** | 串口调试 | [phase-4-serial-debug.md](./phase-4-serial-debug.md) | 收发控制 + CRC/LRC 校验 + 报文解析 + hex 终端 | 中 | ✅ 完成(hex 终端 + 收发开关 + CRC/LRC + 在线解析) |
| **5** | 协议升级 + 轮询订阅 | [phase-5-polling-protocol-v2.md](./phase-5-polling-protocol-v2.md) | 流式协议 v2 + 推送式订阅 + 并发会话 | **大** | ✅ 完成(streamId + 版本协商 + start/stop_poll_stream + subscriptions map) |
| **6** | 打磨与扩展工具 | [phase-6-polish-extras.md](./phase-6-polish-extras.md) | 报文解析器 + 数据导出 + 字节计算器 + 桥接工具 | 中 | ✅ 完成(frame_parser.rs + FC22/23/43/08 + data-export-service + UI 报文解析 tab) |

> 另见:
> - [产品架构.md](./产品架构.md) — 三大产品形态(Master/Slave/SerialDebug)的产品级设计总览。
> - [gap-analysis.md](./gap-analysis.md) — 对照 Modbus Poll/Slave 官方功能 + 论坛 VOC 痛点的**缺口分析**,识别出 16 项补全点(G1–G16)。各阶段文档已纳入对应缺口。

### 依赖关系图

```
阶段 1 (主站核心:全FC + 6传输)
   │
   ├─→ 阶段 2 (主站高级:扫描 + 指令 + 轮询 + 数据类型)
   │       │
   │       └─→ 阶段 5 (协议升级 v2:流式订阅)  ← 需要阶段 2 的轮询需求驱动
   │
   ├─→ 阶段 3 (从站模拟)  ← 复用阶段 1 的 FC 编解码
   │
   └─→ 阶段 4 (串口调试)  ← 复用阶段 1 的 CRC/帧

阶段 6 (打磨)  ← 依赖所有前序阶段的产品形态就位
```

- **阶段 1 是一切的基础** —— 全 FC + 全传输编解码必须先就位。
- 阶段 2/3/4 可在阶段 1 完成后**部分并行**(它们用不同的 UI view,互不阻塞)。
- 阶段 5(协议升级)是架构变更,必须在阶段 2 的轮询需求明确后做。
- 阶段 3 的虚拟从站一旦就绪,阶段 2 的测试就不再依赖真机。

---

## 跨阶段工作(每阶段都要做)

- **文档**:每阶段更新本主索引的状态列 + `ARCHITECTURE.md` 的 "Later" 列表。
- **CHANGELOG**:每阶段一条记录。
- **测试矩阵**(四层):
  1. Rust 单元测试(`modbus_rtu.rs` / `modbus_tcp.rs` / `modbus_slave.rs` / ...)
  2. JSONL 集成测试(`rust-core/tests/jsonl_protocol.rs`,spawn 二进制端到端)
  3. Electron 测试(`electron/*.test.cjs`,`node --test`)
  4. 冒烟测试(`scripts/smoke-electron.mjs`)
- **不破坏现有**:RTU 串口路径 + 8 个现有 IPC handler 在每个阶段都保持可用。
- **实现笔记**:`implementation-notes.md` 记录决策、偏差、验证结果。

---

## 范围外(本计划不做)

- **真机验证(L2+)**:本计划全部是 L1(虚拟服务器/离线)证据。真机验证是独立工作。
- **连接池**:对标 .NET 的 `ModbusTcpConnectionPool` —— 留待后续。
- **非 Modbus 协议**:Siemens S7 / Mitsubishi / Omron 等是后续协议包,不在本计划。
- **Tauri 路径**:保持 Electron 为主,Tauri 原型仅作参考。
- **云端/Web 管理平台**:对标 .NET Nexus 的 `WebMonitorServer` —— 留待后续。

---

## 全局风险

1. **阶段 1 是最大架构转折**:Rust 从纯 codec 升级为协议引擎(TCP 持 socket + 会话状态)。`handle_line` 从纯函数变有状态。
2. **阶段 5 需要协议版本升级(v1 → v2)**:流式响应需要顶层 `streamId` 字段,触发 `deny_unknown_fields` 破坏。需要版本协商。
3. **不引入 Tokio**:所有并发用 `std::thread` + 同步 I/O。阶段 5 的 stdin 非阻塞读取在 Windows 上较 tricky。
4. **MBAP 事务 ID 并发安全**:多线程发 TCP 时 `next_transaction_id` 必须 `AtomicU16` 或加锁。
5. **从站模拟的串口监听**:Windows 上一个 COM 口不能被两个进程同时打开。从站串口模式需要 Electron 把句柄交给 Rust,或 Rust 直接用 `serialport` crate(会引入新依赖)。
6. **三大产品形态的 UI 重构**:当前单页 `master-workspace` 要变成多 view 切换,需要重构 `main.js`(可能拆分为 `src/views/master.js` / `slave.js` / `serial-debug.js`)。

---

## 进度日志

| 日期 | 阶段 | 动作 | 备注 |
|---|---|---|---|
| 2026-08-11 | — | 创建计划集合 | 主索引 + 产品架构 + 6 阶段规格文档 |
| 2026-08-11 | — | 缺口分析 | 对照 Modbus Poll/Slave 官方功能 + 论坛 VOC 痛点,识别 16 项缺口(G1–G16),补齐 FC07/11/12/17 + 2 种 UDP 传输 + 28 种显示格式 + Enron 模式 + 点表管理 + 缩放 + WebSocket 推送。详见 [gap-analysis.md](./gap-analysis.md) |
| 2026-08-11 | 1 | 核心实现 | modbus_pdu/modbus_tcp/modbus_ascii/session 四个新模块 + protocol.rs 扩展到 33 个 JSONL 命令 + Electron 层全扩展(28 个 IPC handler)+ UI 功能码下拉 + 写表单。105 个测试全绿。详见 [implementation-notes.md](./implementation-notes.md) |
| 2026-08-11 | 1 | 补齐偏差 | Session 加 TcpFraming(Standard/RtuOverTcp/AsciiOverTcp)+ UDP 端到端命令(8 个)+ open_tcp/udp 接受 framing 参数 + UI 传输方式选择器(RTU/ASCII/TCP/UDP/RtuOverTcp/AsciiOverTcp 单选)+ TCP 连接/断开 UI。62+10+34=106 测试全绿。 |
| 2026-08-11 | 2 | 数据类型 | value_codec.rs(28 显示格式 + 4 字节序 + 缩放/偏移)+ decode_values JSONL 命令 + Electron decodeValues + UI 数据类型下拉激活(16/32/64 位 + 浮点 + 字符串)。73+34=107 测试全绿。 |

---

## 附录:对标差距矩阵

### 功能码覆盖(修订后,补齐 G10 缺口)
| FC | .NET Nexus | Modbus Poll | Rust 当前 | 目标阶段 |
|---|---|---|---|---|
| FC01 读线圈 | ✅ | ✅ | ❌ | 阶段 1 |
| FC02 读离散输入 | ✅ | ✅ | ❌ | 阶段 1 |
| FC03 读保持寄存器 | ✅ | ✅ | ✅ | — |
| FC04 读输入寄存器 | ✅ | ✅ | ✅ | — |
| FC05 写单线圈 | ✅ | ✅ | ❌ | 阶段 1 |
| FC06 写单寄存器 | ✅ | ✅ | ❌ | 阶段 1 |
| **FC07 读异常状态** | ❌ | ✅ | ❌ | **阶段 6**(G10) |
| FC08 诊断 | ✅(TCP) | ✅ | ❌ | 阶段 6 |
| **FC11 获取通信事件计数** | ❌ | ✅ | ❌ | **阶段 6**(G10) |
| **FC12 获取通信事件日志** | ❌ | ✅ | ❌ | **阶段 6**(G10) |
| FC15 写多线圈 | ✅ | ✅ | ❌ | 阶段 1 |
| FC16 写多寄存器 | ✅ | ✅ | ❌ | 阶段 1 |
| **FC17 报告从站 ID** | ❌ | ✅ | ❌ | **阶段 6**(G10) |
| FC22 屏蔽写 | ✅ | ✅ | ❌ | 阶段 6 |
| FC23 读写多(原子) | ✅ | ✅ | ❌ | 阶段 6 |
| FC43/14 读设备标识 | ✅(TCP) | ✅ | ❌ | 阶段 6 |

**修订**:从原计划的 11+2 个 FC 扩展到 **15+2 个 FC**(补 FC07/11/12/17,对标 Modbus Poll 完整覆盖)。

### 传输方式(修订后,补齐 G-new 缺口)
| 传输 | .NET Nexus | Modbus Poll | Rust 当前 | 目标阶段 |
|---|---|---|---|---|
| RTU 串口(RS-232/485) | ✅ | ✅ | ✅ | — |
| ASCII 串口 | ✅ | ✅ | ❌ | 阶段 1 |
| TCP | ✅ | ✅ | ❌ | 阶段 1 |
| UDP | ✅ | ✅ | ❌ | 阶段 1 |
| RtuOverTcp | ✅ | ✅ | ❌ | 阶段 1 |
| AsciiOverTcp | ✅ | ✅ | ❌ | 阶段 1 |
| **RtuOverUdp** | ❌ | ✅ | ❌ | **阶段 1** |
| **AsciiOverUdp** | ❌ | ✅ | ❌ | **阶段 1** |

**修订**:从 6 种传输扩展到 **8 种传输**(对标 Modbus Poll)。

### 产品形态(修订后,纳入 gap-analysis 的 G1-G16 缺口)
| 形态 | Modbus Poll | Rust 当前 | 目标阶段 | 缺口 ID |
|---|---|---|---|---|
| 主站 — 基础读写 | ✅ | 部分(仅读) | 阶段 1 | — |
| 主站 — 扫描站号 | ✅ | ❌ | 阶段 2 | — |
| 主站 — 扫描波特率 | ❌ | ❌ | 阶段 2 | — |
| 主站 — 指令设置 | ✅ | ❌ | 阶段 2 | — |
| 主站 — 轮询 | ✅ | ❌ | 阶段 2/5 | — |
| 主站 — 28 种显示格式 | ✅ | ❌(仅 UInt16) | 阶段 2 | **G1** |
| 主站 — 缩放+单位+条件着色 | ✅ | ❌ | 阶段 2 | **G4** |
| 主站 — 实时趋势图 | ✅ | ❌ | 阶段 6 | **G4** |
| 主站 — 地址基 0/1 切换 | ✅ | ❌ | 阶段 1 | **G3** |
| 主站 — ENRON/DANIEL 模式 | ✅ | ❌ | 阶段 1/2 | **G2/G11** |
| 主站 — RTS 自动切换(RS-485) | ✅ | ❌ | 阶段 1 | **G7** |
| 主站 — 广播写确认抑制 | ✅ | ❌ | 阶段 1 | **G15** |
| 主站 — 批量读 | ✅(MDI) | ❌ | 阶段 6 | **G8** |
| 主站 — 多窗口/多会话 | ✅(MDI 100 窗) | ❌ | 阶段 6 | **G8** |
| 主站 — Test Center(报文构建) | ✅ | ❌ | 阶段 4 | **G9** |
| 主站 — 点表管理+批量导入 | ❌ | ❌ | 阶段 2/6 | **G5** |
| 主站 — 寄存器自动发现 | ❌ | ❌ | 阶段 6 | **G5 差异化** |
| 主站 — 超时/负载率助手 | ❌ | ❌ | 阶段 2 | **G12** |
| 从站 — TCP 模拟 | ✅(100 实例) | ❌ | 阶段 3 | — |
| 从站 — RTU 串口模拟 | ❌ | ❌ | 阶段 3 | — |
| 从站 — 赋值/置零 | ✅ | ❌ | 阶段 3 | — |
| 从站 — 多站号 | ✅ | ❌ | 阶段 3 | — |
| 从站 — 客户端会话列表 | ❌ | ❌ | 阶段 3 | — |
| 串口调试 — hex 终端 | ✅ | ❌ | 阶段 4 | — |
| 串口调试 — CRC/LRC 校验 | ✅ | ❌ | 阶段 4 | — |
| 串口调试 — 报文解析 | ✅ | ❌ | 阶段 4/6 | — |
| 串口调试 — 收发控制 | ✅ | ❌ | 阶段 4 | — |
| 报文解析 — 离线解析器 | ❌ | ❌ | 阶段 6 | — |
| 桥接 — 串口↔TCP | ❌ | ❌ | 阶段 6 | — |
| 字节编解码计算器 | ❌ | ❌ | 阶段 6 | — |
| **数据推送 — WebSocket 实时流** | ❌(用 VBA) | ❌ | 阶段 6 | **G6** |
| **数据导出 — CSV/JSON/SQLite** | ✅(Excel) | ❌ | 阶段 6 | — |
| 寄存器变化高亮+日志 | ✅(条件色) | ❌ | 阶段 2 | **G16** |
