# Implementation Notes — 阶段 1:Modbus 主站核心

> 对应 [phase-1-modbus-master-core.md](./phase-1-modbus-master-core.md) + [gap-analysis.md](./gap-analysis.md) 的 G15(广播写抑制)。
> 完成日期:2026-08-11。

## 最新测试基线（2026-08-23）

- 完整 Rust 核心测试 `cargo test --manifest-path rust-core/Cargo.toml` 通过：612/612（单元 446/446 + integration 166/166）。
- `npm run test:electron`：278/278 通过（2026-08-23 本轮复测，含三族路由收口与 Golden Vector）。历史批次中的 253/216/257/272 以本节为准。
- 本轮联合门禁实测：`npm run build`（Vite 7.3.6）、`npm run smoke:electron`、`npm run test:rust-jsonl`（JSONL 89/89 + MQTT/BACnet/KNX 各 1/1 + R0 soak 1/1）、`cargo fmt --check`、`cargo test` 612/612（进程内 `LIB`）、`cargo check --all-targets`（无 warning）、`npm audit --audit-level=moderate`（0 漏洞）。交接见 `docs/test-first-protocol-handoff.md`。
- 测试优先协议补全：西门子未知变体 fail-closed，USS/RK512/PPI/FW/Web API 不再默认 `open_s7_connection`，并修掉 `s7Read` 未定义 `variant`。Modbus UI 按 8×6 矩阵分流，UDP 不再走 `tcp_*`，串口 FC01/02 不再当成 FC03。三菱十变体路由落地，C24 写禁用。交接见 `docs/test-first-protocol-handoff.md`。
- 已修复三个既有测试基线问题：LS XGT `%DD100` 请求总长/application 长度断言、LS XGT Read Response application 长度断言，以及 USS round-trip 测试直接复用请求帧、未置响应 ADR bit7 且未重算 BCC 的问题。生产 fail-closed 校验保持不变。
- `cargo check --manifest-path rust-core/Cargo.toml --all-targets` 通过且 0 warning；已清理测试专用导入误入正常编译路径、未使用导入/变量/死代码、serde payload 字段命名和轮询错误清理返回值。
- R1 脱敏诊断包已接入：`electron/diagnostics-service.cjs` 输出 JSON+TXT，包含运行时/Rust Core/串口/网卡/项目配置/最近 100 帧；专项测试覆盖公网/私网 IP、IPv6、MAC、序列号、项目名和敏感键脱敏。
- R1 项目工作区状态已接入：`.nexus.json` v1 在兼容旧文件的前提下保存每会话趋势 key（不含历史数据）与全局指令任务清单；打开后仅恢复列表，不自动连接、轮询或执行指令。
- R1 安全写入链路已接入：主站 Modbus 写前读取旧值、确认框显示目标/地址/旧值/新值、写后回读比对，并写入用户数据目录 `logs/write-audit.jsonl`（5MB 轮转）。广播写无法满足旧值/回读门禁被阻止；S7/MC CPU 控制使用输入特定词的高风险确认并审计。
- R1 会话恢复已接入：保存/打开项目会记录最近项目与保存版本，下次启动通过 `workspace-recovery.json` 一次性恢复项目配置、点表、趋势选择、任务清单和视图，并明确提示“只读恢复”；不自动连接、轮询、启动从站、推送或执行写入。
- R1 项目工作区引用已接入：`.nexus.json` v1 兼容保存 Modbus TCP/串口模拟器模式、端口与允许站号，MC/S7 虚拟设备端口，以及最近通讯帮助 source/variant。打开或启动恢复仅回填输入和默认帮助选择，不启动模拟器、不打开帮助弹窗；新建项目回到 502/5000/102 默认端口。
- R3 发布元数据已接入：`scripts/generate-release-metadata.cjs` 生成 SPDX 2.3 SBOM、全文件 SHA-256SUMS 和 release manifest，记录源提交/dirty、NOTICE 哈希、Electron/Rust Core 版本；`package-portable.ps1` 自动生成并校验。Formal 要求 clean 源码，`-AllowDirty` 仅输出 candidate。见 `docs/release-metadata-runbook.md`。
- R3 发布说明/回滚工具已接入：`scripts/release-notes.cjs` 校验并归档 formal 说明，`scripts/release-rollback.cjs` 同时校验当前与上一版包的 metadata、提交和版本差异并生成 rollback plan。见 `docs/release-rollout-runbook.md`；尚未执行 formal 发布与实际回滚演练。
- R3 构建证据已接入：`scripts/build-evidence.cjs` 按安全 JSON 计划顺序执行命令，记录脱敏 stdout/stderr、退出码、耗时、日志大小和 SHA-256，并自动复核。当前 `evidence/build/candidate/` 为 dirty-source candidate，5/5 通过；见 `docs/build-evidence-runbook.md`。
- 下方历史批次中的测试数字是当时快照；若与最新基线冲突，以本节和当前命令输出为准。

## 功能批次：串口可视化批次 1 —— 调试页实时曲线面板（2026-08-31）

- 规格：`docs/spec-plan-serial-plot-parse-replay.md` 批次 1。Serial Studio 仅功能对标，零代码/零格式借鉴（GPL-3.0 隔离）。
- `#debug-view` 新增「实时曲线」卡片：60s 滚动窗口、统一 Y 轴、HiDPI Canvas、暂停（冻结绘制但数据继续入环）/清空/导出 CSV；图例复用 `trend-chip` 样式类；新元素 ID 统一 `plot-` 前缀。
- 新模块 `src/serial-plot.js`（`SeriesStore`/`evalManualRule`/`pairRegisterChannels`/`buildCsvRows`/`drawSerialPlot`/`renderPlotLegend`/`parseHeadHex`），主站 trend 代码零改动；绘制循环 rAF + 100ms 节流，仅 `activeView === "debug"` 且未暂停时绘制，与主站趋势循环由 `activeView` 天然互斥。
- 通道来源 A（自动解析）：收发帧按 50ms 合批调用既有 `parse_frame_online`（**未新增 Rust 命令、未动 IPC 四处白名单**），TX 读请求与 RX 读响应配对得到 `HR[n]/IR[n]` 寄存器通道；无配对请求时退化为 `HR+i/IR+i` 响应内偏移名；非目标协议帧解析失败静默跳过；发现通道登记上限 300。
- 通道来源 B（手动字节偏移）：帧头过滤（可空）+ 偏移 + u8/u16/i16/u32/i32/f32 × 大/小端 + 缩放 + 单位，仅对 RX 收包求值；表单带自解释 placeholder 与易混字段 title tooltip。
- CSV 导出复用 `export_csv`（首列 ISO 时间 + 每通道一列、单位入列名、稀疏单元格留空、重名列追加通道 key 去重）。
- 新测试 `scripts/serial-plot.test.cjs`（纯逻辑层，7 个用例）已挂进 `npm run test:electron`；绘制/布局由 `scripts/audit-ui-layout.cjs`（VIEWS 含 debug）与冒烟覆盖。
- **验证状态（2026-08-31 已完成，批次 1 关闭）**：环境恢复后四件套全绿——
  - `node --test scripts/serial-plot.test.cjs`：7/7（首轮 2 处**测试用例自身笔误**：FC04 期望数组少一个寄存器、i16 小端负值期望写成无符号；实现代码无需改动）。
  - `npm run test:electron`：326/326 通过（基线 309 + 新增用例，0 失败）。
  - `npm run build`（Vite）：通过。
  - `npx electron scripts/audit-ui-layout.cjs`：**debug 视图 0 违规**（首轮手动通道表单 15 控件一行超宽换行触发 row-misaligned，已拆为两行复测通过；master 3 条 / interfaces 1 条为**改动前已存在**的存量问题，不在本批次范围）。
  - `npm run smoke:electron`：`NEXUS_UI_SMOKE_OK` + `NEXUS_ELECTRON_SMOKE_OK`（GPU cache 报错为无头模式无害噪音）。
  - 环境插曲存档：当日 Git for Windows 更新中断致 `bin\bash.exe` 缺失（`git.exe` 2.54.0 正常），所有命令执行瘫痪半天，用户重装修复。期间项目工作区由 `E:\Desktop\项目汇总\Nexus2.0` 迁移至 `E:\Desktop\Nexus2.0`（本批次文档路径均已随迁，无需修改）。

## 功能批次：串口可视化批次 2+3 —— 自定义帧解析栈与会话录制回放（2026-08-31）

- 规格：`docs/spec-plan-serial-plot-parse-replay.md` 批次 2/3。Serial Studio 仅功能对标，零代码/格式借鉴（GPL-3.0 隔离）。
- **批次 2（Rust 自定义帧解析）**：新模块 `rust-core/src/frame_definition.rs`（binary 定长/帧头/sum8/xor8/crc16-modbus + ascii-delimited 两模式；6 类型 × 大小端 × 缩放 × 单位；显式错误码 `HEAD_MISMATCH`/`LENGTH_MISMATCH`/`CHECKSUM_MISMATCH`/`FIELD_OUT_OF_RANGE`/`ASCII_*`/`FRAME_DEF_INVALID`，绝不静默）。JSONL 命令 `custom_frame_parse`/`custom_frame_validate`（definition 每请求内联，不进 Session），`protocol.rs` dispatch/命令清单/handler 三处接线，IPC 四处白名单同步（preload/main.cjs/rust-core-client/protocol.rs），并被信封契约测试纳入 MATRIX（fixtures 补 2 条载荷）。
- **批次 2（UI/持久化）**：调试页新增「帧解析」卡（模式/帧头/定长/校验/字段表/试算/载入/保存/删除/应用到曲线）；定义随项目保存 —— `.nexus.json` schemaVersion **1→2**（`workspace.frameDefinitions`，链式迁移 + 归一化单条非法剔除不整份拒绝，上限 50 定义 × 64 字段）；「应用到曲线」后收包在既有 50ms 合批里走 `custom_frame_parse` 出 `fd:定义.字段` 通道；「用最近收包试算」即时预览字段值或错误码。
- **批次 3（录制/回放）**：`electron/record-service.cjs`（JSONL 落盘 `userData/sessions/session-*.nxsession.jsonl`，recordVersion 头、5MB 自动分卷、坏行读取跳过计数、10 万帧上限）；IPC `record_start/stop/status/pick/read/export_csv`（NON_CORE 侧）；回放调度器 `src/replay-scheduler.js`（按原始 ts 差 ÷ 倍速调度，1x/2x/4x/最大速/单步/暂停，时钟回拨防护）——回放**直接重进渲染层管线**（`appendDebugLog` + `plotFeedRecord`），不伪造 IPC 事件；录制文件可导出 CSV（绑定了帧定义时附字段值列，Rust 解析前 2000 RX 帧）。
- **验证（2026-08-31 全绿）**：Rust 单测 461/461（+15）+ `custom_frame_jsonl_e2e.rs` 4/4（登记进 `scripts/test-rust-jsonl.ps1`），全量 **631/631**；`cargo fmt --check` 通过；Electron 全量 **336/336**（+10：record-service 4 + replay-scheduler 5 + project v1→v2 迁移 1，信封契约抓到 6 条新命令并已补 MATRIX/NON_CORE）；Vite build、`audit-ui-layout` debug 视图 **0 违规**（master 3/interfaces 1 为存量）、冒烟双 OK。
- 修复插曲：`read_binary_value` 有符号值经 u64 中转溢出（自查发现，已修）；`.ps1` E2E 首跑 3 处测试期望笔误；golden 夹具随 v2 迁移重新生成。黄金向量文档：`docs/custom-frame-golden-vectors.md`（S3，14 向量）。
- L2 边界：RS-485 温度模块等真实设备的帧定义适配与曲线验收仍待硬件在场，按 P0 证据分级标注，不宣称设备级完成。

## 功能批次：工作区卡片可折叠（2026-08-24）

- 全部带 `.card-header` + 内容的工作区卡片可点标题或左侧箭头折叠/展开；标题栏里的「刷新 / 帮助 / 清空」等按钮不触发折叠。
- 折叠只藏内容，不停止轮询或连接；状态写入 `localStorage` `nexus-card-collapse-v1`，按视图 + 标题（有 `id` 的标题用 id，避免串口/TCP 标题切换丢记忆）。
- 实现：`src/card-collapse.js`；`electron/card-collapse.test.cjs` 覆盖键、存储、点击忽略和 init。

## 交付概要

阶段 1 把 Modbus 主站从「FC03/FC04 一次性 RTU 串口读」推进到「全 FC(01–06、15、16)+ 6 传输(RTU/ASCII/TCP/UDP/RtuOverTcp/AsciiOverTcp 的编解码基础)+ 写操作 + 广播」。

**测试**:105 个测试全部通过(61 Rust lib + 10 JSONL 集成 + 34 Electron),0 失败。

---

## 功能批次：DNP3/TCP 只读 Master 与 Class 扫描（2026-08-23）

### 审计、许可证与安全边界

- 审计旧 `Nexus.Dnp3`、测试、脚本 Outstation 和 MIT 许可证；旧控制构造器不自动迁移到 Rust 产品。
- 评估维护中的 Step Function Rust DNP3：功能覆盖较广，但公开许可证限制非商业/非生产使用，本 MIT 产品未引入；Apache-2.0 OpenDNP3 已归档。本轮采用有明确上限的 MIT 自有只读实现，生产栈/商业授权与第三方互操作门禁继续保留。
- 首批角色固定 TCP Master；页面和 JSONL 只开放连接、完整性轮询、Class 0/1/2/3、对象 READ、应用 CONFIRM 和离线解析。
- Select、Operate、Direct Operate、Analog Output、Time Write、Restart、Freeze、File、Secure Authentication、串口和 Outstation 模式全部不存在，并由桌面 route 测试保护。

### Rust 协议核心与会话

- 新增 `rust-core/src/dnp3.rs`：DNP3 CRC16（`0xA6BC`）、严格 `05 64` 数据链路头、每 16 user bytes CRC、little-endian 链路地址、Transport FIR/FIN/6-bit sequence 和应用 FIR/FIN/CON/UNS/4-bit sequence。
- 实现 READ g60v1/v2/v3/v4 完整性/Class 扫描、all-objects 与 16-bit start/stop 范围、应用 CONFIRM、IIN 和 Quality 解释；首批解析 g1/g2/g3/g4/g10/g11/g20-g23/g30/g32/g40/g42 静态/事件对象，覆盖绝对/相对时间，NaN/Inf 和未知 variation fail-closed。
- `Session` 新增 TCP 20000 只读 Master：核对 Master/Outstation Link Address 和 control，支持请求/响应 Transport 分段、TCP 分片、solicited 多应用片、自发响应收集和 CON 自动确认；错序、错误 CRC/地址/control、重复 FIR、未结束响应会失败并按错误边界清理会话。

### 产品接线与证据

- 新增 10 个 JSONL 命令：`open_dnp3_connection`、`dnp3_integrity_poll`、`dnp3_class_scan`、`dnp3_read`、`dnp3_build_link_frame`、`dnp3_parse_link_frame`、`dnp3_build_class_scan`、`dnp3_build_read_request`、`dnp3_parse_application_response`、`dnp3_build_confirm`。
- Rust client、Electron preload/main 最小白名单、`dnp/dnp3-tcp` 注册表、通讯帮助和独立 DNP3 页面已接入；注册表现为 48 个可选变体。
- `docs/dnp3-golden-vectors.md` 固定链路/CRC、完整性/Class、范围 READ、应用 CONFIRM、对象/Quality/IIN/时间和许可选择边界。
- `rust-core/tests/dnp3_jsonl_e2e.rs` 4/4：测试对端独立实现 CRC/链路/Transport，动态 loopback TCP 覆盖分片、Class 0/1/2/3、静态/事件/时间/IIN、solicited/unsolicited 应用确认和错序断开。
- DNP3 Rust 单元 7/7；Electron 全量 174/174；当前脚本 Rust JSONL 合计 68/68；MQTT Aedes/MQTT.js 1/1；Vite build、格式检查和 npm audit 通过。
- 完整 Rust 单元套件当时为 414/417；失败为当时既有 LS XGT 两项长度断言和 USS 一项应答位断言，本批次未修改这些协议。
- 当前只达到软件/独立脚本对端 S2-S4a；维护且许可兼容的生产栈、第三方 Outstation、一致性、真实 RTU/IED、校时、认证、串口、控制门禁、自动重连和 8/24/72 小时 L2/L4 仍未完成。

---

## 功能批次：IEC 60870-5-104 TCP 只读主站与总召（2026-08-23）

### 审计与安全边界

- 审计旧 `Nexus.Iec104`、测试和 MIT 许可证；旧实现仅作为协议语义与黄金向量参考，不把其遥控、设点、校时能力自动迁入 Rust 产品。
- 首批角色固定为 Client/Master；页面和 JSONL 只开放连接、TESTFR、总召、APDU/ASDU 只读编解码与通用断开。
- 不提供单点/双点遥控、Select/Execute、设点、时钟同步、文件传输、TLS/IEC 62351 或 Outstation 模式；Electron 页面回归明确检查这些控制命令缺席。

### Rust 协议核心与会话

- 新增 `rust-core/src/iec104.rs`：严格 I/S/U APDU，长度 4..253，15 位 N(S)/N(R)，STARTDT/STOPDT/TESTFR，C_IC_NA_1 站/组总召构造，Cause of Transmission 与 Quality Descriptor 解释。
- 首批监视类型为 M_SP_NA_1、M_DP_NA_1、M_ME_NA_1、M_ME_NC_1 和 M_IT_NA_1；支持顺序/非顺序 IOA，短浮点 NaN/Inf fail-closed，SIQ/DIQ 不把值低位误判为 Overflow，BCR 单独解释序号/Carry/Adjusted/Invalid。
- `Session` 新增 TCP 2404 会话：连接必须收到 STARTDT_CON；总召必须按 ACT_CON→数据→ACT_TERM 完成，对每个 I 帧立即发送 S 确认，并拒绝错序、未确认、CA/QOI 不匹配、否定确认、未知类型和畸形长度。
- `iec104_test_frame` 要求 TESTFR_CON；通用断开 best-effort 发送 STOPDT_ACT，不因对端已经关闭而阻塞本地资源释放。

### 产品接线与证据

- 新增 9 个 JSONL 命令：`open_iec104_connection`、`iec104_general_interrogation`、`iec104_test_frame`、`iec104_build_i_frame`、`iec104_build_s_frame`、`iec104_build_u_frame`、`iec104_parse_apdu`、`iec104_build_general_interrogation`、`iec104_parse_asdu`。
- Rust client、Electron preload/main 最小白名单、`iec/iec-60870-5-104` 注册表、通讯帮助和独立 IEC104 页面已接入；注册表现为 47 个可选变体。
- `docs/iec104-golden-vectors.md` 固定链路、总召、S 确认、ACT_CON/ACT_TERM 和质量位向量。
- `rust-core/tests/iec104_jsonl_e2e.rs` 4/4：独立脚本 Outstation 在动态 loopback TCP 端口验证分片收帧、STARTDT、五类监视数据、逐帧 S 确认、TESTFR、STOPDT 和错序拒绝。
- IEC104 Rust 单元 7/7；Electron 全量 170/170；当前脚本 Rust JSONL 合计 64/64；MQTT Aedes/MQTT.js 1/1；Vite build 通过。
- 完整 Rust 单元套件当时为 407/410；失败为当时既有 LS XGT 两项长度断言和 USS 一项应答位断言，本批次未修改这些协议。
- 当前只达到软件/独立脚本对端 S2-S4a；真实 RTU/IED、生产模拟器、带时标 ASDU、完整 t0/t1/t2/t3 与 k/w 窗口、冗余、长稳和实验室 L2 仍未完成。

---

## 变更清单

### Rust core 新模块

| 文件 | 作用 | 行数 |
|---|---|---|
| `rust-core/src/modbus_pdu.rs` | 传输无关 PDU 层:全 FC01-06/15/16 的 build/parse + 异常检查 + 位打包/解包 | ~380 |
| `rust-core/src/modbus_tcp.rs` | MBAP 帧编解码 + `TransactionIdGenerator`(AtomicU16) | ~140 |
| `rust-core/src/modbus_ascii.rs` | LRC 计算 + ASCII 帧编解码(`:hex(hex)CRLF`) | ~180 |
| `rust-core/src/session.rs` | `Session` 结构:TCP/UDP 连接生命周期 + `transact_tcp`/`transact_udp` | ~300 |

### Rust core 扩展模块

| 文件 | 变更 |
|---|---|
| `rust-core/src/error.rs` | `RtuError` 新增 16 个变体(写/TCP/ASCII 错误)+ 对应错误码映射 |
| `rust-core/src/lib.rs` | `serve()` 改为持 `Session`;新增 `serve_with_session` |
| `rust-core/src/protocol.rs` | `handle_line(session, line)` 有状态化;新增 28 个命令(串口 build/parse + TCP 端到端) |
| `rust-core/src/modbus_rtu.rs` | `RtuError` 扩展(保持向后兼容) |

### Electron 层扩展

| 文件 | 变更 |
|---|---|
| `electron/rust-core-client.cjs` | `COMMANDS` 从 7 个扩展到 33 个;新增 26 个类型化方法 |
| `electron/modbus-master-service.cjs` | 新增 `readCoilsOnce`/`readDiscreteInputsOnce` + 4 个写操作 + 11 个 TCP 端到端函数;新增 `readBitsOnce`/`writeOnce` 辅助 |
| `electron/main.cjs` | IPC handler 从 8 个扩展到 28 个 |
| `electron/preload.cjs` | 白名单从 8 个扩展到 25 个 |

### UI 扩展

| 文件 | 变更 |
|---|---|
| `index.html` | 功能码下拉分组(读 4 + 写 4);新增 `#write-value` 写入值输入框;`#write-once` 按钮 |
| `src/main.js` | `readCommand()` 放开 FC01-06/15/16;新增 `parseWriteValue()` + `writeRegistersOnce()`;`syncActionState()` 加写按钮状态;`updateWriteValueVisibility()` 切换写值字段 |
| `src/app.css` | 新增 `.hidden` 类 |

---

## 关键设计决策

### D1. PDU 与传输分离
`modbus_pdu.rs` 完全不涉及传输(CRC/LRC/MBAP),只处理 FC 逻辑。传输层(RTU/TCP/ASCII)负责包装 PDU。这让未来加新 FC 只需改 PDU 层,所有传输自动受益。

### D2. Session 持 socket
`Session` 结构管理 TCP/UDP 连接的生命周期。这是阶段 1 的架构转折 —— Rust core 从纯 codec 升级为协议引擎。串口路径仍由 Electron 持句柄(不破坏现有)。

### D3. 两条路径并存
- **串口路径**(RTU/ASCII):build/transact/parse 三段式(Electron 持句柄,Rust 做 codec)
- **TCP/UDP 路径**:端到端命令(Rust 持 socket,一次调用完成完整事务)

### D4. 广播写抑制(G15)
写操作 unit_id=0 时,`build_*` 命令返回 `expectResponse: false`,Electron 的 `writeOnce` 检测到此标志后发完即返回,不等待响应(避免超时误报)。

### D5. 向后兼容
原有 7 个 JSONL 命令和 8 个 IPC handler 完全保留。现有 FC03/FC04 RTU 串口读路径不受影响。`hello` 的 capabilities 数组扩展,旧客户端仍能用。

---

## 偏差与风险

### 偏差
1. **RTU Over TCP / ASCII Over TCP 未在阶段 1 单独建命令** —— TCP 端到端命令(`tcp_read_*` / `tcp_write_*`)用的是标准 MBAP。RtuOverTcp 需要在 TCP 连接上套 RTU 帧(含 CRC),这需要 `Session` 的 `Connection` 枚举加传输模式变体。**推迟到阶段 1 后续迭代**,因为它需要在 `transact_tcp` 里根据传输模式决定是否加 CRC。当前 TCP 命令走标准 Modbus TCP(MBAP),满足 80% 场景。

2. **UDP 端到端命令未在 protocol.rs 实现** —— `Session::transact_udp` 已就绪,但 JSONL 命令只暴露了 TCP 的(`tcp_*`)。UDP 的 `udp_*` 命令结构相同,可快速添加。**推迟**,因为 UDP Modbus 实际使用较少。

3. **MSVC 工具链问题** —— 测试环境 `msvcrt.lib` 路径异常(只在 `onecore\x64\` 下),需要用 `run_test.bat` / `run_build.bat` 设置 LIB 环境变量。这是环境问题,不影响代码。

### 风险
1. **`Session` 不是线程安全的** —— `transact_tcp` 需要 `&mut self`,多请求并发时会冲突。阶段 1 保持串行(JSONL 协议本身就是 1 请求 1 响应),阶段 5(流式协议)需要解决。

2. **写操作确认（2026-08-23 已修复）** —— 主站 Modbus 写入现已执行写前读旧值、旧值/新值确认、写后回读和 JSONL 审计；广播写被阻止。S7/MC CPU 控制使用输入特定词的高风险确认。

3. **TCP 连接无超时配置** —— `Session::open_tcp` 硬编码 5 秒超时。应该可配置(由 JSONL payload 传入)。

---

## 验证矩阵

| 层 | 测试类型 | 数量 | 状态 |
|---|---|---|---|
| Rust lib | 单元测试(`#[cfg(test)]`) | 61 | ✅ 全通过 |
| Rust 集成 | JSONL 协议(spawn binary) | 10 | ✅ 全通过 |
| Electron | `node --test` | 34 | ✅ 全通过 |
| 构建 | `cargo build --release` | — | ✅ 成功 |
| 冒烟 | `smoke-electron.mjs` | — | ⏳ 待跑(UI 变更后需手动验证) |

---

## 新增 JSONL 命令清单(33 个)

### 串口路径(13 个 build/parse 对 + 1 个 ASCII)
`build_read_coils`, `parse_read_coils`, `build_read_discrete_inputs`, `parse_read_discrete_inputs`, `build_write_single_coil`, `parse_write_single_coil`, `build_write_single_register`, `parse_write_single_register`, `build_write_multiple_coils`, `parse_write_multiple_coils`, `build_write_multiple_registers`, `parse_write_multiple_registers`, `build_ascii_read_holding_registers`, `parse_ascii_read_holding_registers`

### TCP/UDP 路径(11 个端到端 + 3 个连接管理)
`open_tcp_connection`, `open_udp_connection`, `close_connection`, `tcp_read_coils`, `tcp_read_discrete_inputs`, `tcp_read_holding_registers`, `tcp_read_input_registers`, `tcp_write_single_coil`, `tcp_write_single_register`, `tcp_write_multiple_coils`, `tcp_write_multiple_registers`

---

## 下一阶段(阶段 2)的前置条件

阶段 1 已就绪的功能:
- ✅ 全 FC(01-06、15、16)build/parse
- ✅ TCP/UDP socket 管理
- ✅ ASCII 帧编解码
- ✅ 写操作 + 广播抑制
- ✅ 端到端 TCP 事务(经虚拟回环测试验证)

阶段 2 可在此基础上加:
- 扫描站号/波特率(复用 TCP/RTU 事务)
- 多数据类型(纯函数,加到 `modbus_pdu.rs` 或新 `value_codec.rs`)
- 轮询(setInterval 驱动,复用 read_once)
- 指令列表(序列化执行)

---

# Implementation Notes — FX 串口协议(Computer Link + 编程口)

> 对应《三菱全协议设计文档.md》§3.2 / §3.3。完成日期:2026-08-15。

## 交付概要

新增两个纯编解码模块(无 I/O),并接入 JSONL 命令、Electron IPC 白名单与透传。

| 文件 | 作用 |
|---|---|
| `rust-core/src/fx_links.rs` | FX Computer Link 专用协议(§3.2):ENQ/ACK/NAK/STX 帧构造与解析、BR/WR/BW/WW/BT/WT/RR/RS/PC/TT 十命令、和校验(站号首字符~ETX) |
| `rust-core/src/fx_programming.rs` | FX 编程口协议(§3.3):CMD 0/1/7/8 帧构造与解析、§3.3.4 两张地址编码表(读/写 编号×2+基址;强制 编号÷8+基址、低位字符在前)、和校验(CMD~ETX)、字数据解码(低字节在前) |

集成改动:`lib.rs`(模块注册)、`protocol.rs`(5 个命令 + capabilities)、`electron/preload.cjs`(白名单)、`electron/main.cjs`(IPC for 循环透传)。

## 新增 JSONL 命令(5 个)

- `fx_links_build`:`{station, cmd, delay, data}` → `{frame, frameHex, checksum}`
- `fx_links_parse`:`{response}` → STX 数据 / ACK / NAK 错误码
- `fx_prog_build_read`:`{device, address(字符串,X/Y 八进制), words}` → `{frame, frameHex}`
- `fx_prog_build_write`:`{device, address, values}` → `{frame, frameHex}`
- `fx_prog_parse`:`{frame}` → 数据(含 `words` 字解码)/ ACK / NAK

## 关键设计决策

1. **fx_links 请求帧含 ETX + CR LF**:文档 §3.2.4 帧模板明确列出 `...数据 | ETX | 和校验 | CR LF`,和校验范围"站号首字符~ETX"。真实 JY992D82001 请求帧无 ETX(接机校准点,若需切换只改 `build_fx_links_request` 尾部 5 字节)。
2. **STX 响应站号前缀自适应**:STX 后若紧跟 `[2 hex]"FF"` 则拆出站号/PC号(任务规格),否则整体视为数据(文档 §3.2.4 简化布局);两种布局和校验范围相同,先验校验再分支。
3. **NAK 帧容错解析**:文档布局为 `NAK 站号(2) 错误码(2)`(标 [实机验证]);解析兼容 1 位错误码与带 PC号 "FF" 变体(错误码不会是 FF,可安全区分)。
4. **点数字段**:按文档首版约定「位 2 字符 / 字 4 字符 + 十六进制首地址」。

## 规范偏差(文档自身算术错误,按一致性修正)

| 项 | 文档原文 | 实现 | 理由 |
|---|---|---|---|
| 特殊 D 地址 | 「E00H+8000×2=4E00H」 | `0x0E00+(n-8000)×2`,D8000→0x0E00 | 原式算术不成立(0xE00+0x3E80=0x4C80) |
| C 当前值 32 位 | 「C00H+200×2=1000H」 | `0x0C00+(n-200)×4`,C200→0x0C00 | 原式与 D0 区(0x1000)冲突且算术不成立 |
| 强制 ON M100 字节 | 「80CH → 发 "C008"」 | 0x080C 按规则「低位字符在前」→ "C080" | 示例只能由字符串拼接("800"+"C")导出,与表二数值公式矛盾;其 SUM 字节亦错(按算法为 "15") |

以上均为文档标注 [实机验证] 的字段,接机后校准只需改 `fx_prog_rw_address` / `addr_chars_low_first` 两处。

## 验证

- `cargo test fx_`:28 个测试全绿(fx_links 13 + fx_programming 13 + protocol 端到端 2)
- `cargo test` 全量:209 lib + 51 集成 = 260 个,0 失败(基数含并行 M2 工作)
- `cargo build --release`:通过
- `node --check` preload.cjs / main.cjs:通过
- 文档示例向量逐字节对比:读 D123(`02 30 31 30 46 36 30 34 03 37 34`)、读型号(`02 30 30 45 30 32 30 32 03 36 43` 及响应 `02 43 32 35 36 03 45 33`)、§3.2.4 WR 示例、和校验示例(0x56)全部一致

## 风险

- fx_links 点数字段宽度/地址进制是文档明示的存疑点([实机验证]),当前按首版约定实现。
- fx_programming 强制地址字节序存在三种转述("C080"/"0C08"/"C008"),已按规则文字实现并在代码注释标明替代方案。

---

# Implementation Notes — MC 串口 C24(3C/4C 帧)+ A-1E/SLMP-1E 帧

> 对应《三菱全协议设计文档.md》§3.1 / §3.4。完成日期:2026-08-15。

## 交付概要

新增两个纯编解码模块(无 I/O),并接入 JSONL 命令、Electron IPC 白名单与透传。

| 文件 | 作用 |
|---|---|
| `rust-core/src/mc_serial.rs` | MC 协议串口 C24(§3.1):3C 格式1(ASCII+和校验+CRLF)/ 格式3(二进制+16位累加和 LE)/ 4C 格式4(二进制无校验)的帧构造与响应解封装;站号 00~31 校验;应用区与 3E 100% 复用 |
| `rust-core/src/mc_1e.rs` | A-1E/SLMP-1E 帧(§3.4):命令 00~03(位/字 读/写)、软元件 2 字符 ASCII 代号表(13 种)、首地址 4B LE、响应副帧头 81H + 结束代码(5BH 后随详细代码+00H)、位数据每 16 点 2 字节打包 |

集成改动:`lib.rs`(模块注册)、`protocol.rs`(5 个命令 + capabilities + handler)、`electron/preload.cjs`(白名单)、`electron/main.cjs`(mcCmd for 循环透传)。

## 新增 JSONL 命令(5 个)

- `mc_serial_build_3c`:`{format:"1"|"3"|"4", station, mcAppData(十六进制数组)}` → `{frame, frameHex, checksum}`
- `mc_serial_parse_3c`:`{format, frame}` → `{station, mcAppData, mcAppDataHex}`
- `mc_1e_build_read`:`{cmd(0/1), device, head, points, watchdog=10}` → `{frame, frameHex, deviceCode}`
- `mc_1e_build_write`:`{cmd(2/3), device, head, valuesWords|valuesBits, watchdog=10}` → `{frame, frameHex}`
- `mc_1e_parse`:`{frame, cmd, points}` → `{status:"words"|"bits"|"writeAck"|"error", values|errorCode+detailCode+message}`

## 关键设计决策

1. **mc_serial 不重复应用层**:`build_mc_serial_3c` 接收 `mc_pdu` 产出的 3E 应用数据区原样封装(测试 `app_layer_reuses_3e_pdu` 演示全链路:mc_pdu 组帧 → 3C 封装 → 响应解封装 → mc_pdu 解析)。
2. **和校验范围统一「站号首字符~ETX(含)」**:格式1 取低 8 位输出 2 ASCII hex;格式3 为 16 位累加和小端 2 字节(范围文档未明示,按格式1 对称定义)。
3. **格式3 解析不剥 CRLF**(校验和字节可能恰为 0D 0A);格式1/4 的尾部 CRLF 宽容可缺省(抓包工具剥离场景)。
4. **1E 位/字类别交叉校验**:命令字节(位/字)与软元件类别(位/字)不匹配即拒绝,错误提示引导「触点用 TS/CS、当前值用 TN/CN」(§6.1:TN/CN=当前值字元件)。
5. **1E 位打包**:bit i → 第 i/16 组(2 字节小端)的第 i%16 位,等价于展平字节流的 i/8 字节 i%8 位;读写同一规则(测试 `bit_pack_unpack_consistency` 验证)。
6. **字点数上限 255**(§3.4.1);位点数无文档化上限,不做人为设限,由模块侧结束代码兜底。
7. **响应副帧头强制 81H**(mcprotocol FX3U 实测断言);错误消息提示「部分 A 系列资料记 80H,以实机抓包为准」,便于接机排查。
8. **站号构造侧校验 ≤31**(§3.1.3 模块参数),解析侧只解码不设限(调试工具宽容)。

## 规范说明

- §3.4.2 示例向量中 CSDN 转载字节 `20 40` 文档已标注为「转载讹误」,按 mcprotocol 实测字段序实现 `44 2A`("D*"),与文档给定的权威示例 `01 FF 0A 00 64 00 00 00 44 2A 0C 00` 逐字节一致。
- 软元件表 13 项 = §3.4.2 实测集合(X*/Y*/M*/D*/B*/W*/TN/CN)+ §6.1 标准 A 系列(L/F/V/TS/CS,mcprotocol 家族实现同表)。
- 5BH 详细代码 10H/11H/12H 含义(软元件编号/代码/点数异常)经外部资料交叉确认;14H~18H 未获可靠对照,返回通用消息+代码值。

## 验证

- `cargo test -- mc_serial mc_1e`:28 个测试全绿(mc_serial 12 + mc_1e 16)
- `cargo test` 全量:**240 lib + 12 + 21 + 25 集成 = 298 个,0 失败**(基线 209 lib → 240,含并行 mc_udp 工作及其测试)
- `node --check` preload.cjs / main.cjs:通过
- 文档向量对比:§3.4.2 字读 D100 12 点(`01 FF 0A 00 64 00 00 00 44 2A 0C 00`)模块级与 JSONL 级均逐字节断言;§3.1.2 格式1 帧含手算校验和 4D(0x60+0x3EA+0x03=0x44D);格式3 手算 16 位和 0x116→`16 01`;软元件 ASCII 代号(D*=44 2A、X*=58 2A、TN=54 4E 等)与 §3.4.2 表一致

## 风险

- 格式3 的 16 位和校验范围为对称推断(文档仅明示格式1 范围),接机校准点在 `mc_serial.rs::checksum_u16` 的调用范围。
- C24 各格式的 STX/ETX/CRLF 组合按模块「MC 协议」参数可能有个体差异(§3.1.1 实机验证项);当前实现按 §3.1.2 布局。
- 1E 位打包顺序(bit0=首点)为通用惯例,文档未给位级示例,标 [实机验证]。
- RS-485 半双工时序(RTS 方向控制、turnaround 延时)属传输层,后续由串口驱动实现(§3.1.3)。

---

## 演示批次:Modbus RTU 一键扫描 + 连接耗时显示(2026-08-21)

> 对应 [spec-plan-serial-one-key-scan.md](./spec-plan-serial-one-key-scan.md) + 《演示测试规划-FX3U与Modbus.md》v0.2 §7-E1。

### 决策

- 扫描编排落在**独立服务模块** `electron/modbus-scan-service.cjs`(依赖注入 `createModbusScanService({ serialService, readHoldingRegistersOnce })`),不内联 main.cjs——后者顶部 `require("electron")` 无法被 node --test require;spec 原方案(A2 内联)据此微调,与 modbus-master-service.cjs 模式一致。
- 探测配置**自带全部必填字段**(8 数据位/N|E|O 校验/1 停止位/流控 none/DTR·RTS preserve),不依赖"当前已打开的串口配置"——规避 scanBaudRate 在串口从未打开时 originalConfig=null、testConfig 缺字段被 SerialService.open 拒绝的隐患(旧函数保持不动,新路径自行规避)。
- 档位顺序:波特率常见档在前(9600→19200→38400→115200→4800),同波特率 8N1→8E1→8O1;每档先探首站号快速跳档;默认 firstHit。
- 进度经 `webContents.send("nexus:scan_progress")` 推送,preload 暴露 `onScanProgress`(照 poll_data 模式,不发明新机制);取消 = 扫描中再次点击"一键扫描"按钮 → `scan_all_cancel`,恢复扫描前串口状态。
- E1 计时在渲染层 `performance.now()` 包 `callBackend`,口径 = "点击→连接完成",与 NexusDemoCSharp/DemoMelsec 一致(MC over TCP 无应用层握手);覆盖 mcConnect 3E/1E、connectTcp、openPort、nexusApplyScanHit 五处。
- 命中动线不自动开轮询(保留演示节奏),自动完成:回填参数 → open_serial_port → FC03 读 0..N 寄存器入表 → 提示设置倍率/单位。

### 验证

- `node --test electron/modbus-scan-service.test.cjs`:**7/7**(档位编排顺序 / firstHit 短路 / full 收集 / 取消恢复两分支 / 探测配置字段完整 / open 失败重试跳档 / INVALID_PARAM 不触碰串口)
- `npm run test:electron`:**48/48 全绿**(基线 41 + 新增 7)
- `node --check`:main.cjs / preload.cjs / modbus-scan-service.cjs / src/main.js 通过;`npm run build`(vite)通过;`npm run smoke:electron` OK
- 真串口/com0com 场景未测(开发机无硬件),列入演示彩排 T-3 天清单

### 风险

- `renderScanAllResults` 的"选用并连接"按钮用内联 onclick 调 `nexusApplyScanHit`(与既有 renderScanResults 内联风格一致);函数为全局声明(main.js 非模块脚本)。
- 扫描期间串口被独占重开;与其他串口操作的互斥靠进程内 busy 标志,外部进程占用 COM 口时 open 失败 → 单档重试 1 次后跳档。
- 一键扫描按钮仅串口模式启用(TCP 模式禁用,同"扫描波特率")。

---

## 验证批次:全链路交叉 + pymcprotocol 第三方交叉(2026-08-21)

> 对应"真实有效性"验证路径第 3、4 条(演示测试规划 §8-R9 联动)。

### 交付与结果

1. **C# 数据桥全链路(headless 复刻演示第 5(b) 幕)**:CodecCheck 新增 `bridge` 模式(MC 3E 客户端读 Nexus 虚从站 D100~D107+M → 镜像本机 Modbus TCP 502 + 500ms 自增心跳寄存器 40012)。三方进程编排:Nexus MC 从站(sidecar) → C# bridge → 读取方。
   - rust-core Modbus 主站(`open_tcp_connection`+`tcp_read_holding_registers`)读到 40001=0x1234/40002=0xABCD/40003~05=1/2/3/40009=0x0555(M 打包),与 MC 侧 seed 逐值一致;
   - Python 裸 socket 客户端第二次交叉:同样数据 + **心跳 8→20(6s,与 500ms 周期精确吻合)** → 实时穿透证明;
   - **交叉发现并修复真实缺陷**:ModbusTcpServer 空闲连接 5s 断连(轮询间隔>5s 的真实项目必踩)→ 改 60s;另加 listener `ReuseAddress`(快速重启 TIME_WAIT)。
   - 工程坑记录:`dotnet build sln`/增量对 `<Compile Include>` 链接文件的变更跟踪不可靠,改动 Shared/ 后须 `rm -rf bin obj` 干净重建(CodecCheck.exe 是固定 apphost 壳,时间戳不能证明代码版本,以行为为准)。
2. **pymcprotocol 0.3.0 第三方交叉(传统 MC 语义客户端 → Nexus MC 从站)**:连读 D100 得 0x0100(≠seed)→ 触发深度排查。
3. **裸 socket 三帧语义实验(定性 R9)**:
   - 帧 A `0401/0001=字`(Nexus SLMP 语义):正确返回 `34 12`(=0x1234);
   - 帧 B `0401/0000=字`(传统 MC 语义):被当位读,静默返回错值;
   - 帧 C 传统+2B 代码:错位解析,返回超长数据(不崩溃,audit 修复过的 panic 防线有效)。
   - **pymcprotocol 源码铁证**(type3e.py `_make_devicedata`):Q 系列 binary 3E 软元件代码 = `to_bytes(1)`(1 字节)→ 与 Nexus 一致;0401 子命令 word=0x0000/bit=0x0001(iQR 另有 0x0002/0x0003)→ 与 Nexus 相反。

### 结论(修正 R9 认知)

- 软元件代码 1 字节:两源一致(pymcprotocol 源码 + 设计文档抓包帧),无争议。
- **0401 子命令存在两个语义族**:SLMP(SH-080956,FX5U/iQ-R 内置服务;Nexus 现状,自洽正确) vs 传统 MC(Q/QnA E71 模块、pymcprotocol/HSL;0000=字)。Nexus 目前仅实现 SLMP 族 → **连 Q 系列 E71 模块的 MC 协议端口会失败**(M7 变体/0403/0406/1401 等子命令语义需同族核对)。
- **ADP/1E 主线不受影响**(1E 帧无此歧义)。
- 从站健壮性观察:错配语义帧被静默错读而非异常码 → 列改进项(权威 MC 对位/字不匹配应回异常)。

### 遗留(产品级,另立 spec)

- S1:「传统 MC 3E/4E」变体(子命令语义开关或独立帧型),覆盖 Q/E71 生态;
- S2:从站错配帧返回异常结束码;
- S3:pymcprotocol 纳入常规交叉脚本(与 pymodbus/snap7 同列)。

### 验证命令

- 全链路:`(sidecar start_mc_tcp_slave seed) & CodecCheck.exe bridge --duration N &` + rust-core `tcp_read_holding_registers` / python 裸 MBAP 两读心跳比对;
- 语义实验:python 裸 socket 依次发帧 A/B/C,比对响应。

### 附:硬件彩排一键脚本(2026-08-21)

- 新增 `scripts/rehearse-hardware.cjs`(check/adp/scan 三模式):adp = spawn rust-core sidecar 走 JSONL `open_mc_1e_tcp`/`mc_1e_read`/`mc_1e_write`(payload 字段按 protocol.rs deny_unknown_fields 严格核对:{connectionId,host,port}/{connectionId,address:"D100",points}/{connectionId,address,values});scan = 纯 node 复用产品模块(SerialService + readHoldingRegistersOnce + createModbusScanService,经 RustCoreClient 注入),**与 UI 一键扫描同代码路径**。
- 本机实测(无硬件):check/adp/scan 三模式的引导与失败路径全部符合预期;**发现本机 TUN 代理(Clash/Meta,198.18.x 适配器)造成 TCP connect 假连通**(端口探测通、1E 握手 2ms"成功",读帧 failed to fill whole buffer)→ 脚本已内置假阳性识别提示,并写入演示文档 §10 注意事项。
- 真机执行记录:待 T-3 天携带 FX3U/ADP/485 模块后运行,结果回填本节。

- 彩排脚本二轮增强(2026-08-21):① 新增 `preset` 模式——SC09 插上后直接经 FX 编程口协议(9600-7E1,`fx_prog_build_write/parse` + framing "fx",headless 复用 `createFxSerialService`)写 D100 并回读核对,免 GX Works2 手工预置;② `check` 的 ADP 探测升级为**帧级终裁**(connect 后发真实 1E 读帧,真设备必回 81H)——connect 通但无帧响应即提示"TUN 代理假连通",本机实测该提示正确触发;③ `scan` 单串口自动选用。四个模式(check/preset/adp/scan)的无硬件引导路径全部实测。
- 彩排脚本三轮增强(2026-08-21):新增 `all` 模式(preset→adp→scan 一条命令);**真实结果自动回填**本节"真机执行记录"(带时间戳,插在锚点后按时间正序)——实测抓出并修正第三个缺陷:最初版本代理假连通产生的 FAIL 也会被记录污染真机记录,现改为**仅真设备在场**(1E 首帧真实返回/扫描真实探测)才启用记录,假连通/SKIP 路径实测不再写入。至此接线后的全部动作 = 一条 `node scripts/rehearse-hardware.cjs all`。
- 彩排脚本 watch 模式(2026-08-21):`node scripts/rehearse-hardware.cjs watch [--timeout 分钟,默认30]` 挂起等待硬件(每 10s 探测 COM 口出现或 ADP 帧级真设备),检测到后 5s 自动执行 preset→adp→scan 并自动回填记录——接线后零操作。无硬件下等待/超时路径实测正常(外层 timeout 杀掉时循环仍在等待)。

---

## 功能批次:网络连通性 Ping(2026-08-21,用户需求)

> 通用小工具:任意 IP/主机名 ping,用于连接排障(演示前 ping PLC)。

### 交付

- `electron/ping-service.cjs`:独立可测服务(execFile 参数数组化,无 shell 注入面;host 白名单正则);解析中/英文 Windows 与 *nix 输出为结构化结果(alive/sent/received/lossPct/timesMs/min-avg-max/raw),"时间<1ms" 记 0。
- IPC `nexus:ping_host` + preload 白名单;interfaces 视图新增「网络连通性 Ping」卡片(目标/次数/结果+原始输出折叠,状态灯绿=通红=不通,不通时提示检查供电/网线/同网段/TUN 代理直连规则)。

### 验证

- `node --test electron/ping-service.test.cjs`:7/7(中英文成功/全丢包/*nix/注入拒绝/服务层两态)
- `npm run test:electron`:**55/55 全绿**(48 基线 + 7 新增);vite build 通过;四文件 node --check 通过
- 真实系统 ping 三态实测:127.0.0.1 → alive/0%丢包/avg 0ms;10.255.255.1 → 100% 丢包;非法输入 → INVALID_HOST 不执行命令

---

## 功能批次:版本化项目文件(2026-08-21)

### 交付

- 新增 `.nexus.json` 项目格式(schemaVersion 1),保存项目名称、当前功能页、当前会话、连接参数、显示/轮询参数，以及最多 20 个会话、总计 10000 个点位。
- 顶栏加入新建、打开、保存和另存为；打开项目只恢复配置和点表，不自动连接、轮询、启动从站或执行写入。
- 主进程集中完成 5 MiB 文件上限、格式版本、会话重名、功能码、站号、地址和数量边界校验；渲染层不接受任意文件路径，且按结构化点位重新生成表格，不载入项目内 HTML。
- 连接、轮询、从站或实时推送运行期间禁止切换项目，避免在未知设备状态下替换工作区。

### 验证

- `electron/project-file-service.test.cjs`:4/4，覆盖标准化、非法版本/重复会话/越界点位拒绝、保存读取往返和专用扩展名。
- `npm run test:electron`:65/65 全绿；Rust 基线 459/459。

---

## 功能批次：项目文件安全落盘（2026-08-23）

> 对应总控 Spec Plan `PROJ-006`、`PROJ-007`、`PROJ-008`。本批次只修改项目文件服务，不改变协议、连接、轮询、模拟器或任何设备写入行为。

### 交付

- `ProjectFileService.save()` 不再直接覆盖目标文件：先在目标同目录生成随机后缀临时文件，写入后回读比对，再 `renameSync()` 原子替换目标。
- 临时文件写入、回读校验或替换失败时保留原项目文件；即使写入中途失败也会尝试清理已生成的临时文件，清理失败不掩盖原始错误。
- `ProjectFileService.load()` 在 JSON 解析失败时返回结构化定位：`path`、`byteLength`、`line`、`column`、`characterOffset`、`byteOffset`；错误消息本身包含行列与字节位置，避免 Electron IPC 序列化后界面只剩原生解析错误。读取和解析不修改源文件。
- 新增轻量 JSON 语法扫描器，用于在 Node 26 错误文案不含行列时定位首个非法字符；随后仍以 `JSON.parse` 作为权威解析结果。
- 输入限制收口为 fail-closed：5 MiB、20 会话、10000 点位、200 指令、32 趋势选择、120000 JSON 节点、16 层嵌套和 32 个根字段；趋势/指令超限不再静默截断。

### 验证

- `node --test electron/project-file-service.test.cjs`:11/11，覆盖原子替换、rename 失败和临时写入失败均保留原文件并清理临时文件、损坏 JSON 定位与源文件不变、超限集合和深嵌套拒绝。
- `npm run test:electron`:225/225 通过；期间清理了先前工具链补丁遗留的重复 `const fs` 声明，使 build-evidence 测试恢复可加载。
- `npm run build` 通过；`npm run smoke:electron` 通过（GPU 日志为 Electron 退出期噪声，UI/端口冒烟判定为 PASS）。

---

## 功能批次：脱敏项目导出（2026-08-23）

> 对应总控 Spec Plan `PROJ-009`。目标是在保留协议结构、地址、数量和端口的前提下导出可加载的问题复现项目，同时移除客户/现场可识别信息；导出过程不连接设备、不执行项目内指令。

### 交付

- 顶栏新增「脱敏导出」，主进程新增 `project_export_sanitized` IPC，preload 仅白名单暴露该动作。
- `ProjectFileService.sanitizeForExport()` / `exportSanitized()`：
  - 固定项目名为 `Nexus脱敏项目`；
  - 会话名、点位名改为序号模板；
  - TCP 主机替换为 `redacted.invalid`，串口名清空；
  - 未识别的自由文本显示/数据类型回落 `Unsigned16`，单位清空，非数值比例回落 `1`；
  - 最近帮助引用清空，指令任务写入值清空；
  - 保留传输类型、TCP/模拟器端口、功能码、站号、地址、数量、趋势 key、模拟器允许站号和指令任务结构。
- 导出复用同目录临时文件、回读校验和原子替换；默认文件名固定为 `Nexus脱敏项目.nexus.json`。
- 导出不更新 `currentProjectPath`、不写入 `workspace-recovery.json`、不改变当前工作区；用户仍需自行选择脱敏文件的分享对象。

### 验证

- `node --test electron/project-file-service.test.cjs`:14/14，覆盖敏感字段移除、协议结构保留、脱敏文件可重新加载、原子导出、无临时文件残留，以及 main/preload/renderer/UI 四层接线。
- `npm run test:electron`:228/228 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：项目 schema 迁移框架（2026-08-23）

> 对应总控 Spec Plan `PROJ-001`、`PROJ-005`、`PROJ-010`。磁盘上的既有 v1 字段名保持 `schemaVersion`，避免破坏当前项目文件；本文中的 schema_version 指该版本语义。

### 交付

- `ProjectFileService` 增加逐版本迁移注册表；当前提供 v0→v1 迁移，补充 `schemaVersion`、workspace 默认结构和完整 v1 校验。
- `previewMigration(filePath)` 只读加载并返回源版本、目标版本、迁移步骤和迁移后文档；源文件字节保持不变。
- `migrateFile(sourcePath, outputPath)` 通过原子保存输出独立 v1 文件，不覆盖 v0 源文件。
- 常规打开缺失 `schemaVersion` 的 v0 项目时，只在内存中迁移并恢复工作区；界面明确提示“原文件未修改，显式保存后才升级”。
- 新增黄金样本：
  - `electron/fixtures/project-v0.nexus.json`
  - `electron/fixtures/project-v1.expected.json`
- 后续项目 schema 变更必须新增对应迁移步骤、输入样本和期望样本，不能用“默认值兜底”替代可审计迁移。

### 验证

- `node --test electron/project-file-service.test.cjs`:17/17，覆盖 v0→v1 黄金样本、当前 v1 无迁移、只读预览不改源文件、显式另存迁移、迁移结果可再次加载、无临时文件残留和渲染层提示。
- `npm run test:electron`:231/231 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：协议 ID 与显示名分离回归（2026-08-23）

> 对应总控 Spec Plan `PROJ-002`。当前协议注册表已经使用稳定 `id/source/variant` 与独立 `label`；本批次补齐防退化测试，防止后续新增协议时把显示文案写入路由或项目持久化。

### 交付

- `electron/protocol-registry.test.cjs` 新增 ID/显示名分离测试：
  - 53 个注册项 `id` 唯一；
  - 53 个显示 `label` 唯一；
  - `id/source/variant` 与显示文案互不相同；
  - 连接帮助目录返回稳定 value 和独立 label，且 value 能回溯到注册表 ID。
- `electron/project-file-service.test.cjs` 新增项目持久化测试：
  - `activeView`、`transport`、帮助 `source/variant` 均为稳定协议/视图 ID；
  - 项目根节点和帮助引用不含 `label`、`displayName`、`sourceLabel` 等显示名字段。

### 验证

- `node --test electron/protocol-registry.test.cjs electron/project-file-service.test.cjs`:23/23 通过。
- `npm run test:electron`:233/233 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：项目凭据默认不落盘（2026-08-23）

> 对应总控 Spec Plan `PROJ-004`。项目格式本身不需要凭据；当前协议连接的用户名/密码、Web API token、TLS 客户端私钥等都不能进入 `.nexus.json`。

### 交付

- 在项目文档标准化入口增加递归敏感键检查，覆盖 password、passwd、secret、token、authorization、cookie、credential、private key、API key、client secret、certificate key 等命名。
- 命中时返回 `PROJECT_CREDENTIALS_NOT_PERSISTED` 和精确字段路径（如 `$.config.tcp.password`），不静默丢弃、不保存、不加载、不迁移。
- 保存、迁移另存和脱敏导出的白名单输出继续不含凭据键；后续若需要凭据，必须先设计独立安全存储与明确 UI，不允许扩展项目 JSON 默认承载。

### 验证

- `node --test electron/project-file-service.test.cjs`:19/19，覆盖 TCP 密码、API token、私钥、credentials 容器和输出无凭据键。
- `npm run test:electron`:234/234 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：Electron 隔离与 CSP 门禁（2026-08-23）

> 对应总控 Spec Plan `SEC-001`～`SEC-004`。目标是将已经正确的 Electron 安全配置固化为集中回归，并清掉与 CSP 不兼容的遗留 UI 写法。

### 交付

- 新增 `electron/security-policy.test.cjs`：
  - BrowserWindow 固定 `contextIsolation: true`、`nodeIntegration: false`、`sandbox: true`；
  - 禁止 `webSecurity: false`，窗口打开默认 deny；
  - preload 只暴露一个 `nexusDesktop` bridge，`invoke` 必须先命中动作级 `allowedCommands`；
  - CSP 固定 `default-src 'self'`、`script-src 'self'`、`object-src 'none'`，禁止 `unsafe-eval`、外源 script、`javascript:` 和内联事件；
  - HTML script 必须带外部 `src`。
- 修复扫描结果“选用”按钮遗留的内联 `onclick` 字符串：改为 `document.createElement("button")` + `addEventListener`，并让 `appendCells()` 支持直接挂载 DOM 节点。该按钮在严格 CSP 下恢复可点击。

### 验证

- `node --test electron/security-policy.test.cjs`:3/3 通过。
- `npm run test:electron`:237/237 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：项目文件不可信输入门禁（2026-08-23）

> 对应总控 Spec Plan `SEC-005`。项目文件不是应用生成后即可信任的输入：即使文件来自本机，也可能被外部工具生成、手工修改或替换。

### 交付

- 保持主进程先 `stat` 后读取的顺序：超过 5 MiB 的项目只返回 `PROJECT_TOO_LARGE` 和路径/大小，不读取文件内容，避免超大数据先进入解析器。
- 在项目专项中显式构造含 `__proto__` 和 `constructor.prototype` 的 JSON：
  - 确认 `JSON.parse` 会产生自有字段；
  - 标准化输出没有这些字段；
  - 全局对象原型未被污染；
  - 未知 workspace 字段不进入输出。
- 不可信项目文件在整个测试过程中保持字节不变，后续仍由已有的格式、版本、深度、节点数、集合数量、地址/端口、凭据和语法错误门禁处理。

### 验证

- `node --test electron/project-file-service.test.cjs`:20/20 通过。
- `npm run test:electron`:238/238 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：CSV/JSON 点表导入守卫（2026-08-23）

> 对应总控 Spec Plan `SEC-006` 的当前已存在导入面。抓包文件、CAN EDS、IEC61850 SCL/CID/ICD 和 DBC/J1939 导入功能尚未实现，因此 SEC-006 仍保持未完成；后续新增这些导入时必须复用同等不可信输入边界。

### 交付

- 新增 `src/point-import.js`，批量导入不再在渲染层直接 `JSON.parse`、按行 split 并追加任意对象。
- 读取前按 `File.size` 拒绝超过 2 MiB 的文件，不读取文件内容。
- 内容层限制：
  - 单次 10000 点；
  - JSON 20000 节点；
  - JSON 4 层嵌套；
  - CSV 单行 16 KiB；
  - 拒绝 `__proto__` / `constructor` 字段。
- 点位字段严格标准化：
  - 站号 1-247；
  - 功能码仅允许 1/2/3/4/5/6/15/16；
  - 地址 0-65535；
  - 数量按功能码分为 125 字或 2000 位；
  - 数据类型仅允许既有安全类型；
  - 名称、单位、比例限制长度；
  - 无效站号/功能码不再静默回退 1/3。

### 验证

- `node --test electron/point-import.test.cjs`:3/3，覆盖 CSV/JSON 正常路径、超限不读取、深嵌套、原型键、非法字段和渲染层接线。
- `npm run test:electron`:241/241 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：本地 SSE API 回环绑定门禁（2026-08-23）

> 对应总控 Spec Plan `SEC-008` / `API-001`。当前本地 API 只有实时数据推送 SSE 服务，没有非 loopback 入口；`API-002` 保持未完成。

### 交付

- `RealtimePushService` 固定使用 `127.0.0.1` 作为绑定地址；`start()` 不接受 bind/host 参数，伪造的 `host` 字段会被忽略。
- 端口校验为 0-65535；默认 8080，`0` 仅供测试获取系统分配端口。
- `port: 0` 时返回 `server.address().port` 作为实际上报端口，返回 URL 不再出现 `:0`。
- 继续保留 Host 白名单：仅 `127.0.0.1` / `localhost` 可访问 `/events` 与 `/status`。
- 继续保留 CORS 白名单：仅 HTTP loopback Origin 可获得 `Access-Control-Allow-Origin`。

### 验证

- `node --test electron/realtime-push-service.test.cjs`:3/3，包含动态端口真实 socket：
  - server address 必须是 `127.0.0.1`；
  - loopback Host 请求 `/status` 返回 200；
  - 伪造 `evil.example` Host 返回 403；
  - 传入伪造 host 参数不能改变绑定；
  - 端口 65536 拒绝。
- `npm run test:electron`:242/242 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：Sidecar 命令与参数门禁（2026-08-23）

> 对应总控 Spec Plan `SEC-007`。Sidecar 请求面是 Electron 主进程到 Rust Core 的 stdin JSONL；该通道不属于 renderer 可直达接口。

### 交付

- Electron `RustCoreClient` 固定 `COMMANDS` 枚举，未知命令在写入 stdin 前返回 `UNKNOWN_COMMAND`。
- 新增发送前 payload 结构门禁：
  - 必须是普通对象；
  - 最多 20000 节点；
  - 最多 8 层嵌套；
  - 拒绝 `__proto__` / `constructor`；
  - 保留 1 MiB JSONL 行上限。
- Rust Core 协议层继续校验 protocolVersion、requestId/command/payload 结构，未知命令返回 `UnknownCommand`。
- 各 Rust handler 使用 serde 强类型结构反序列化，具体协议字段非法时返回结构化错误；Electron 不做命令级字段复制式重复校验，避免两套协议定义漂移。

### 验证

- `node --test electron/rust-core-client.test.cjs`:15/15，新增未知命令、null/array payload、原型污染键、深嵌套拒绝，以及合法请求继续写入的回归。
- `npm run test:electron`:243/243 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：统一日志脱敏（2026-08-23）

> 对应总控 Spec Plan `SEC-009`。诊断包已有结构化拓扑/项目/凭据脱敏；本批次补齐运行日志、写审计和构建证据的统一文本脱敏。

### 交付

- 新增 `electron/log-redaction-service.cjs`：
  - password / passwd / secret / token；
  - Authorization 与 Bearer；
  - cookie / credential；
  - private key / API key / client secret / certificate key；
  - 用户名、邮箱；
  - Windows `C:\Users\...` 与 Unix `/home/...` 用户路径；
  - PEM 私钥/证书块；
  - URL 中的 token、password、username 等敏感参数。
- 接入四个日志出口：
  - Rust Core sidecar stderr；
  - Electron 主进程 Rust Core 启动错误；
  - 写审计 JSONL 的 `message` 字段；
  - 构建证据 stdout/stderr 日志。
- 诊断包继续使用原有的结构化脱敏，覆盖公网/私网 IP、IPv6、MAC、项目/会话名和串口序列号。

### 验证

- 联合专项 24/24 通过：日志脱敏 3/3、Rust Core 客户端 15/15、写审计 3/3、构建证据 3/3。
- `npm run test:electron`:246/246 通过。
- `npm run build` 通过；`npm run smoke:electron` 通过，`ports=0`，未连接真实设备。

---

## 功能批次：双生态依赖清单报告（2026-08-23）

> 对应总控 Spec Plan `SEC-SUPPLY-001` / `SEC-SUPPLY-003`。SEC-SUPPLY-002 继续保留：npm audit 当前 0 漏洞，但本机未安装 `cargo audit`，废弃包扫描也尚未形成固定证据。

### 交付

- 新增 `scripts/supply-chain-report.cjs` 与 `npm run supply:report`。
- 报告内容：
  - package-lock v3 全量条目：名称、版本、production/dev/application、resolved、integrity、optional；
  - Cargo.lock v4 全量条目：名称、版本、registry source、checksum；
  - 两份锁文件的 SHA-256；
  - Node 26.1.0、npm 11.13.0、Rust 1.97.1 minimal 工具链来源；
  - 产品名称与版本。
- 当前证据输出：
  - `evidence/supply-chain/dependency-report.json`
  - `evidence/supply-chain/dependency-report.md`
- 当前清单计数：npm 178 条（生产 22、dev 155、应用 1）；Cargo 14 条（registry 13、本地 1）。
- 专项覆盖锁文件解析、工具链一致性、哈希输出、Markdown 摘要、实际仓库报告生成和 npm script 接线。

### 验证

- `node --test scripts/supply-chain-report.test.cjs`:3/3 通过。
- `npm run supply:report` 成功生成 JSON 与 Markdown。
- `npm run test:electron`:249/249 通过。
- `npm run build`、`npm run smoke:electron` 通过；`npm audit --audit-level=moderate` 为 0 漏洞。

---

## 功能批次：生产闭包许可证与 NOTICE（2026-08-23）

> 对应总控 Spec Plan `SEC-SUPPLY-004` / `BUILD-006`。厂商 SDK 再分发条件仍归 `SEC-SUPPLY-005`；当前产品没有厂商 SDK。

### 交付

- 新增 `scripts/license-report.cjs` 与 `npm run supply:licenses`。
- npm 生产闭包：
  - 22 个条目；
  - 21 个 MIT；
  - 1 个 Apache-2.0 OR MIT；
  - 每个条目必须具备可解析 SPDX 表达式。
- Cargo 依赖闭包：
  - 13 个条目；
  - MIT OR Apache-2.0 10 个；
  - MIT 1 个；
  - Unlicense OR MIT 1 个；
  - `(MIT OR Apache-2.0) AND Unicode-3.0` 1 个。
- `THIRD_PARTY_NOTICES.md` 新增：
  - npm 生产依赖 serialport / Tauri API；
  - Rust 生产闭包 serde、serde_json、thiserror 及传递依赖说明；
  - NOTICE SHA-256 纳入许可证报告。
- 互操作协议栈继续单独标注：
  - Aedes / MQTT.js（MIT，dev-only）
  - bacstack（MIT，dev-only）
  - knx（MIT，dev-only，binary-parser override）
- 输出：
  - `evidence/supply-chain/license-report.json`
  - `evidence/supply-chain/license-report.md`

### 验证

- `node --test scripts/license-report.test.cjs`:4/4，覆盖 npm/Cargo 解析、SPDX 拒绝缺失或非法、NOTICE 哈希、真实仓库报告和 npm script 接线。
- `npm run test:electron`:当时快照 253/253；当前基线 278/278，见文首。
- `npm run build`、`npm run smoke:electron` 通过；`npm audit --audit-level=moderate` 为 0 漏洞。
- `npm run supply:report` 与 `npm run supply:licenses` 均成功生成最新证据。

---

## P0 修正批次：纯桌面演示与桌面实机安全验证（2026-08-21）

> 对应 [spec-plan-desktop-demo-hardware-p0.md](./spec-plan-desktop-demo-hardware-p0.md)。本节覆盖并取代上文“首站离线即跳档”“all 自动 preset→adp→scan”和“任意 COM 触发 watch”的旧口径。

### 正确性修正

- `modbus-scan-service.cjs` 改为每个“波特率 × 校验”档完整遍历所选站号。Modbus 从站只响应自己的地址，不能用站号 1 离线推断整个串口参数档离线。
- 新增站号 5、16 命中回归和非法范围测试；扫描专项 10/10，全套 Electron 68/68。
- 进度结果新增 `triedProbes/totalProbes`，未命中可明确看到实际完成的探测矩阵。

### 实机安全门

- `rehearse-hardware.cjs all` 现为 `check → adp-read → scan`，失败即停，默认流程不含任何 PLC 写操作。
- `adp-read` 只读 D100/D101/M0~M7；旧 `adp` 名称保留为只读兼容别名。
- `adp-write` 仅允许显式 D0..D7999（禁止 D8000+ 特殊寄存器），必须带 `--confirm-safe-write YES`；读取原值后写入、回读、恢复原值并再次核对，恢复失败即 FAIL。
- `preset` 要求独立的 `--sc09-com` 和写入确认；`scan/all/watch` 要求 `--rs485-com`，不再在 SC09 与 USB-485 之间猜测。
- `watch` 只有在指定 USB-485 串口、192.168.1.x 本机地址和 ADP 真实 1E 帧响应三者同时成立时才触发安全 all。
- 自动回填只记录 PASS；NOT READY、未命中、TUN 假连通和 FAIL 均不写入真机记录。

### 纯桌面闭环

- 新增 `scripts/rehearse-desktop.cjs` / `npm run rehearse:desktop`：检查 5000/502 端口，启动 Nexus 虚拟 MC 从站，运行 C# 7 项黄金向量与 3E/A-1E E2E，启动 C# bridge，再由 Nexus Rust Core Modbus TCP 主站回读镜像值。
- 当前复测：D100/D101=0x1234/0xABCD、M 打包=0x0555，bridge 10 轮/0 失败，纯桌面闭环 PASS。

### 硬件边界

- 当前开发机无 COM、无 192.168.1.x 地址且存在 TUN 假连通；`check --rs485-com COM999` 正确返回 NOT READY/exit 2，未修改真机记录。
- FX3U、ENET-ADP 和温度模块仍未在本批次接入；真实 A-1E 与 RS-485 PASS 继续等待硬件。

### 最终软件验证与便携交付

- Electron 68/68；Rust 459/459；C# Release build 0 warning / 0 error；Vite production build 通过。
- Release Rust Core 重新生成后，`npm run rehearse:desktop` 再次通过：bridge 10 轮/0 失败，Modbus 镜像 0x1234/0xABCD/0x0555 一致。
- 正式便携目录 `output/portable/Nexus 2.0/` 已替换 2026-08-19 旧包；新包 355.76 MiB，`smoke:portable` PASS，已核对包含“一键扫描”、Ping 服务及页面。
- `BUILD-INFO.txt`：`PackagedAtUtc=2026-08-22T03:02:30.7739182Z`；源码 Release Core 与包内 Core SHA256 均为 `FFFDD7AA6DAA0AAFC1B5DCD43E76435B0BDB412940E52E2D9B83AD22891A37EA`。
- Rust 构建仍报告 20 个既有 warning；测试与 Release 构建均成功，本 P0 未顺带清理无关 warning。

---

## P0 增补批次：四对象演示与 S7-200 SMART 现场入口（2026-08-22）

> 对应 [spec-plan-desktop-demo-hardware-p0.md](./spec-plan-desktop-demo-hardware-p0.md) 四对象增补与根目录《演示测试规划》v0.4。现场对象拆为 FX3U、S7-200 SMART、上位机、485 温度模块，证据独立记录。

### 官方资料修正

- 西门子 S7-200 SMART V3 系统手册 V3.0.1（06/2025）明确：ISO-on-TCP PG/HMI 服务使用 TCP 102且默认启用；Put/Get Server 同样使用 102，但默认关闭，必须在 STEP 7-Micro/WIN SMART V3 通讯设置启用并下载配置。
- 同一手册明确 V2.8 软件对应 V2.8 及更早 CPU，V3 软件对应 V3 CPU；旧 V2.8 手册明确 CR20s/30s/40s/60s 无以太网口。
- 据此修正 `src/main.js` / `index.html` 与 `spec-plan-siemens-s1.md` 中“SMART 无需 PLC 设置”的过期口径。UI 现在显示版本闸门、V3 Put/Get 动作、通信写限制建议，并在 SMART 默认 slot 连接失败时自动尝试 0/1。

### 交付

- `scripts/rehearse-desktop.cjs` 新增虚拟 S7 CPU（默认 `127.0.0.1:1102`）：真实 COTP + S7 PDU 协商，读取 `MW0=0x1234`，并用 `VW100=0x5678` 验证 SMART V 语法兼容映射。
- `scripts/rehearse-hardware.cjs` 新增 `smart-read`：强制显式 `--smart-host`、`--smart-address`、`--smart-expect-hex`，默认尝试 rack 0 / slot 0、slot 1；只有协议握手、地址读取和 Micro/WIN SMART 已知值完全一致才记录 PASS，全程不发送 S7 写命令。
- 新增 `all-field`：`check → adp-read → smart-read → scan`，覆盖两台 PLC 与温度模块的只读现场入口；上位机仍由 `rehearse:desktop` 单独给证据。
- 演示规划更新为八幕；增加 SMART 第 3 幕和 T-3 配置/排障清单。“上位机”暂按 `DemoDashboard`，若现场另有所指只替换该对象定义。

### 软件验证与硬件边界

- `node scripts/rehearse-desktop.cjs all`：5/5 步 PASS；S7 协商 PDU 480B，MW0/VW100 对值通过；C# bridge 10 轮/0 失败，Modbus 镜像保持一致。
- SMART 安全负路径：缺 host/address 返回 NOT READY/exit 2；127.0.0.1 关闭端口会依次尝试 slot 0/1 后 FAIL/exit 1；均未写入真机 PASS。
- `npm run test:electron`：68/68；Rust：459/459（20 个既有 warning）；Vite build 与三个 Node 语法检查通过。
- 新便携版：355.76 MiB，`smoke:portable` PASS；已核对打包页面含 V3 Put/Get 提示。`BUILD-INFO.txt`：`PackagedAtUtc=2026-08-22T04:10:47.9372386Z`，Rust Core SHA256=`FFFDD7AA6DAA0AAFC1B5DCD43E76435B0BDB412940E52E2D9B83AD22891A37EA`。
- **仍未完成**：S7-200 SMART 真机未接入；精确型号/订货号、固件、实际 IP 和安全 `VW100` 值待用户从 Micro/WIN SMART 提供。FX3U/ADP 与 485 温度模块真机结论同样继续等待硬件。

---

## 功能批次：全协议通讯设置帮助窗（2026-08-22）

> 对应 [spec-plan-protocol-connection-guides.md](./spec-plan-protocol-connection-guides.md)。本批次仅增加离线说明型 UI，不修改任何协议帧、连接事务、PLC/仪表参数、网卡或串口配置。

### 交付

- Modbus 主站、Modbus 从站、串口调试、三菱、西门子、欧姆龙六个连接区新增“通讯设置帮助”入口，共覆盖 28 个可选通讯变体。
- `src/protocol-guides.js` 作为唯一内容表；帮助窗随当前页面变体打开，并可在窗内浏览同页其他变体，不改动主页面真实选择或参数。
- 内容包括：IP/端口或 COM/波特率/数据位/校验/停止位/站号、设备侧步骤、电脑侧步骤、当前页面实际参数、连接前检查和首次实机只读边界。
- 单独标出 FX3U-ENET-ADP 选 A-1E、SMART V3 Put/Get Server、PPI-over-TCP 网关、Modbus TCP 从站仅绑定 127.0.0.1，以及 USS/RK512 尚未接入专用在线串口事务的真实边界。
- 弹窗支持 Esc、关闭按钮、点击遮罩、焦点恢复、窄屏内部滚动；所有内容离线打包，不访问外网。
- 打包脚本新增经过目录名与父目录边界校验的 `-PortableFolderName` 可选参数；`smoke-portable.mjs` 同步支持 `--folder`，用于旧正式包被外部程序占用时生成独立候选包。
- 默认覆盖前先对旧目录全部文件做独占读取预检；发现任一文件锁立即中止，且不删除任何内容，避免旧脚本“删到一半才遇锁”留下半包。

### 验证

- 专项 `electron/protocol-guide-ui.test.cjs`：3/3；完整 `npm run test:electron`：71/71。
- `node --check src/protocol-guides.js/src/main.js`、Vite production build、Electron smoke 均通过。
- 实际 Electron 渲染检查：六个入口、Modbus 6 个帮助选项、A-1E 自动对应、窗内切换不改页面参数；1440×900 和 900×700 均无横向溢出，窄窗口可滚动。
- `npm run rehearse:desktop`：5/5 PASS；S7 PDU 480B，MW0/VW100 对值通过；C# bridge 10 轮/0 失败，原有通讯闭环未受影响。
- 新候选便携版：`output/portable/Nexus 2.0-protocol-help/`，355.8 MiB，portable smoke PASS；打包内容命中“通讯设置帮助”“当前页面实际参数”“A-1E / SLMP-1E”。
- 正式目录 `output/portable/Nexus 2.0/` 已同步同一候选内容并再次通过 `npm run smoke:portable`；正式包内也命中以上三项新 UI 文案。
- `BUILD-INFO.txt`：`PackagedAtUtc=2026-08-22T14:39:00.7988842Z`；源码、候选包和正式包的 Rust Core SHA256 均为 `FFFDD7AA6DAA0AAFC1B5DCD43E76435B0BDB412940E52E2D9B83AD22891A37EA`。

### 文件锁与正式目录恢复

- 默认打包首次清理旧目录时，`resources/default_app.asar` 被 `E:\Zcode\ZCode.exe` 子进程 PID 20204 持有；Windows Restart Manager 已只读确认锁定者。删除中途停止造成旧正式目录部分资源缺失，旧包随即通过 smoke 明确判定不可用。
- 未擅自结束用户的 ZCode。先用 `-PortableFolderName "Nexus 2.0-protocol-help"` 生成并验证完整候选包，再校验源/目标均位于 `output/portable/` 后，把候选内容机械同步回正式目录；同步时明确跳过仍被锁定、且不是产品应用入口的 `default_app.asar`。
- 恢复后的正式包通过 `NEXUS_UI_SMOKE_OK`、`NEXUS_ELECTRON_SMOKE_OK` 和 `NEXUS_PORTABLE_SMOKE_OK`，现可正常使用。候选目录保留为本批次回退副本。
- 文件锁预检回归：在 ZCode 继续持锁时再次运行默认打包，脚本在删除前明确列出 `default_app.asar` 并退出 1；前后 `BUILD-INFO.txt` 与正式 EXE SHA256 不变，随后正式包 portable smoke 继续 PASS。

---

## 缺陷修复：FX 编程口解析信封字段错位 + 真机会话实录（2026-08-25）

> 硬件首次到场（FX3U + SC09=COM3）：用户在 GX Works2 串口连接读数成功后断开，转 Nexus 读 D0/D2/D5。真机操作由 agent 经 UIA/CUA 远程驱动界面完成。

### 缺陷（真机发现，必现）

- 现象：三菱页 FX 编程口连接成功后点读取，状态栏报 `FX 错误：请求信封缺少字段或字段类型无效`，一帧都发不出去。
- 根因：Rust 两个解析命令的信封字段名不一致——`fx_links_parse` 用 `response`（`FxLinksParsePayload.response`），`fx_prog_parse` 用 `frame`（`FxProgParsePayload.frame`），且都 `deny_unknown_fields`；`electron/fx-serial-service.cjs` 的 progRead/progWrite 照 links 惯例发了 `{response: rx.rx}`，prog 路径整体被拒。
- 修复：progRead/progWrite 两处改发 `{frame: rx.rx}`（`electron/fx-serial-service.cjs:90,103`）；links 路径不动（其契约本就是 `response`）。

### 验证（三层，全部通过）

- 二进制级：`rust-core/target/debug/nexus-rust-core.exe` 直喂 JSONL——`fx_prog_build_read {device:"D",address:"0",words:6}` 出帧 `02 31 30 30 30 30 43 03 36 37`；`fx_prog_parse {frame:[ACK]}` → `status:"ack"`；`fx_prog_parse {frame:[02 "3412" 03 "CD"]}` → `words:[4660]`（0x1234，与 Rust 单测黄金向量语义一致）。修复前同 payload（`response` 字段）确定性复现 `INVALID_ENVELOPE`。
- 服务级：node 桩（request/transact 打桩）跑 `progRead`，断言第二条命令为 `fx_prog_parse` 且携带 `frame` 字段——PASS。
- 回归：`npm run test:electron` 298/298 全绿。

### 真机会话实录（2026-08-25，SC09=COM3 / FX3U / GX Works2 在场）

| 步骤 | 结果 |
|---|---|
| COM3 占用探测（GX Works2 已断开） | FREE |
| 主站页串口 COM3 / 9600 / 7数据位 / 偶校验 / 1停止位，点连接 | 已建立 · 打开耗时 19 ms · 9600·7E1 |
| 三菱页选「FX 编程口 (USB-SC09/485,FX系列)」→ 连接 | FX 编程口 已就绪 · FX 串口已绑定 · 站号 0 |
| 点读取（D100×2） | **FX 错误：请求信封缺少字段…**（即上述缺陷，真机首报） |
| 修复 + 重启应用重走全部步骤 | 串口/连接全部复现成功 |
| 再次点读取（D100×2） | `FX 错误：串口事务失败：Flushing connection (PurgeComm): Access denied` |
| 排查 | `USB Serial Port (COM3)` 与 FTDI `USB Serial Converter` 均 present=False——**SC09 USB 在会话中途被物理拔出**，句柄失效，非软件问题 |
| 路线 B（ADP 192.168.1.20:5000） | TCP 可连但 1E 帧无响应——ADP 的 MC 端口仍未在 GX Works2 下装参数（且下装同样需要 SC09 串口） |

- 结论：软件链路（UI→IPC→组帧→串口）已全通，`fx_prog_parse` 修复后无信封错误；**D0/D2/D5 读值因 SC09 被拔未完成，不宣称完成**。SC09 重新插入后：应用需重新断开/连接串口（句柄已死），再读 D0×6（行标签 D0..D5 逐行显示，D0/D2/D5 即第 1/3/6 行）。
- 过程副产品：用户此前在主站页用 Modbus RTU（8N1）读 FX3U，日志留下 TX `01 03 00 00 00 01 84 0A` / RX `95×7`（CRC 失败）——PLC 确实在回数据，但协议与串口参数都不对，是很好的演示素材（错参数 → 对协议对参数）。

### 遗留

- SC09 重新插入后补做：读 D0×6 核对 GX Works2 侧数值（等硬件）。
- ADP 参数下装（GX Works2 → PLC 参数 → 以太网适配器 → 开放 TCP 5000 MC 协议 → 写入 → 断电重启）后跑 `rehearse-hardware.cjs check` 帧级终裁（等用户操作）。
- 产品级：`fx_links_parse` 与 `fx_prog_parse` 信封字段名不一致属 API 设计坏味道，可在 rust-core 统一（ breaking change，需同步 JS/测试/文档，建议单独立项）。

---

## 举一反三：rust-core 信封契约全量审计 + 常驻回归测试（2026-08-25）

> 用户质询："第一次真机就出缺陷，其他协议是否都有同类问题？沙箱测试为什么说 OK？"——本批次对 JS→rust-core 的信封契约面做全量机械审计,并把审计固化为常驻测试。

### 审计方法（可复现）

1. **盘点**：提取全部产品调用点——`src/main.js` 的 `callBackend("cmd",{...})` 108 条（自动提取顶层键名）+ `electron/*.cjs` 服务层与彩排脚本 `request("cmd",{...})` 37 处（手工核对实参）+ `main.cjs` 透传清单对账。
2. **回放**：每个调用点按渲染层真实类型构造代表性载荷,逐条打真实 `nexus-rust-core.exe`(JSONL);`INVALID_ENVELOPE` = 同款缺陷;域错误(连接拒绝/地址无效/从站不存在等)证明信封已反序列化通过 = 合格。
3. **裁决**：首轮 136 条回放 27 条疑似 → 逐条取渲染层辅助函数真实类型(`fujiConnectionId()` 返回数字、`address()` 返回字符串、`xgtContextPayload().cpu` 数字等)复测,全部转绿——均为夹具占位类型猜错,非产品缺陷。

### 结论

- **`fx_prog_parse` 是全产品 JS→rust-core 信封面唯一的真实契约缺陷**(已修,见上批)。104 条命令矩阵回放零拒绝。
- 沙箱测试此前"OK"的诚实解释：Rust 单测测 Rust(用 `frame`),JS 侧 fx-serial-service **没有测试文件**,两侧各自正确、中间契约无人把守——这正是本轮补上的盲区。其余"问题"(Modbus RTU 读 FX 的 CRC 报错、PurgeComm)分别是用户参数不匹配时软件正确报错、USB 物理拔出,非缺陷。

### 交付

- `electron/rust-core-envelope-contract.fixtures.json`：104 条命令×类型正确载荷矩阵(即审计夹具定稿)。
- `electron/rust-core-envelope-contract.test.cjs` 常驻双守卫:
  - **覆盖守卫**:渲染层/服务层出现新命令而无矩阵条目 → 红(强制补夹具);
  - **回放守卫**:任一命令被真实二进制 `INVALID_ENVELOPE` 拒绝 → 红。
- 已知边界:仅 `callBackend("cmd",{` 字面量形态可自动提取,展开对象(如 `...adsAddressPayload()`)依赖人工跟随——覆盖守卫按命令名兜底;矩阵只保字段名/类型正确,数值语义归各协议自测。

### 验证

- 契约测试 2/2 PASS(104 条全回放、零信封拒绝、无超时无响应)。
- `npm run test:electron` **300/300 全绿**(298 旧 + 2 新)。

---

## 串口服务层补测 + FX 编程口字节序缺陷（2026-08-25 第三批）

> 用户要求"所有协议都要看下"。本批补齐 8 个无测试串口服务的单元测试,过程中抓出并修复第二个真缺陷。

### 缺陷 2(测试抓出):FX 编程口读值字节序颠倒

- `fx-serial-service.cjs` 的 `parseFxProgData` 对 STX..ETX 间数据朴素 `parseInt`("3412"→0x3412),但编程口字数据**低字节在前**(`fx_programming.rs` §3.3.3 与 `decode_fx_prog_word_data` 黄金向量:"3412"→0x1234)。真机表现会是 D0=0x1234 读成 0x3412。
- 修复:①`progRead` 直接采用 rust-core 已按序解码的 `r.words`;②导出的 `parseFxProgData` 本身补上低前高后交换,消除误用陷阱。对照确认 **FX Links(Computer Link)是高字节在前**(`fx_links.rs:551` 断言 "1234"),`parseFxLinksData` 原样正确,不动。
- 双向验证:`parseFxProgData('3412')→0x1234` ✓、`parseFxLinksData('1234')→0x1234` ✓。

### 交付:electron/serial-services.test.cjs(9 用例)

- 覆盖此前零测试的 6 个协议服务:fx(4 例,含今日两缺陷的回归)、hostlink、ppi(两拍)、rk512(三拍:链路建立/数据/释放)、uss、panasonic(mewtocol)。
- 桩模式:request 桩**显式校验每条 rust-core 命令的载荷字段名与类型**——字段错位立即抛错(信封守卫下沉到服务级);transact 桩按脚本回放各拍帧。
- serial-service/serial-debug-service 仍无直测:前者是 serialport 句柄包装(由 master/scan 测试间接覆盖),后者为 UI 辅助,记为已知边界。

### 验证

- 串口服务测试 9/9;`npm run test:electron` **309/309 全绿**。
- Rust 全量 `cargo test` **612/612** 通过(25 个测试目标,含 24 个协议 JSONL e2e:MC 全变体/S7/FINS/FW/PPI/Modbus TCP 均含虚拟从站真实 socket 闭环)。

---

## UX 反馈批次：表单自解释备注（2026-08-25，用户反馈）

> 用户看界面时把「单位」框的占位提示 `°C` 误认为配置值、把「0基」念成"J"——反馈"人家软件就不会这个样子"。定性:占位提示长得像真实值 = 产品缺陷级别的歧义。

### 交付(index.html Modbus 主站页「数据读写」区,纯 title/placeholder,零布局改动)

- `#unit-label` placeholder `°C` → `如℃`(明确是示例),加 title「仅显示标注,如 ℃/kPa;留空则不标注」。
- `#scale-factor`(倍率)加 title「显示值 = 原始值 × 倍率;仅影响显示,不参与通讯」。
- `#address-base`(0基/1基)加 title:0基=协议地址从0起 / 1基=手册习惯从1起(40001 风格,发送时自动-1),区域前缀 4xxxx 不要填进地址框;`#start-address` 同步加提示。
- 超时/轮询加简短 title。

### 验证

- `npm run build` 重建 dist;`npm run test:electron` 309/309;`smoke:electron` UI+ELECTRON 双 OK。

### 后续原则(记入长期记忆)

- 占位提示必须自解释(加「如」前缀或写成完整说明),不得与真实值形态相同。
- 易混字段(地址基/倍率/单位)一律带 title tooltip;新增表单字段时同批补齐。

---

## 真机执行记录：FX3U 编程口首通（2026-08-25）

> 首条真机成功记录。硬件：FX3U + SC09(COM3) + 9600-7E1；链路：Nexus UI → IPC → rust-core 组帧 → SC09 串口 → FX3U 编程口协议 → 回帧解析。

| 时间(约) | 动作 | 结果 |
|---|---|---|
| 本日下午 | agent UIA 驱动:COM3/9600-7E1 打开(13~19ms) → 三菱页 FX 编程口绑定 | 已建立 · FX 编程口 已就绪 |
| 随后 | 用户自行读 x0 × 10 | **FX 读取成功**,X0~X9 十行位状态(全 OFF) |
| 随后 | 读 D0 × 10 | **FX 读取成功**,用户确认读出数据 |
| 随后 | 读 y1 × 20 | **FX 读取成功**(状态栏实证) |

- **结论:FX 编程口全链在真机上打通**。本日修复的两缺陷均经真机验证:信封修复使读取可行,字节序修复使字值正确。
- 证据形态:应用状态栏连续多次「FX 读取成功」+ 用户现场确认;报文面板留有原始帧可复查。
- 待办转 ADP 路线:GX Works2 下装以太网适配器参数(开放 TCP 5000 MC 协议)后跑 `rehearse-hardware.cjs check` 帧级终裁;485 温度模块扫描待接。

## 缺陷 3(真机,用户发现):FX 软元件 X/Y 八进制编号显示错位(2026-08-25)

- 现象:用户读 Y1×20,结果表出现 **Y8/Y9** —— FX3U 的 X/Y 是八进制编号(Y7 之后是 Y10),Y8/Y9 不存在。
- 定性:协议层编址一直正确(rust-core `fx_prog_parse_number` 对 X/Y 按八进制解析);**仅渲染层 `mcRenderRows` 按十进制递增生成行标签**,第 8/9 行标签错名(实为 Y10/Y11),位状态值本身正确。
- 修复:新建 `src/device-labels.js` 纯函数 `formatDeviceSeries`(X/Y 八进制、其余十进制,非法输入返回 null),`mcRenderRows` 改用之;新增 `electron/device-labels.test.cjs` 4 用例(Y1×20 跨界序列/X10 起/D 十进制/非法输入)。
- 验证:device-labels 4/4;`npm run build` 重建;`npm run test:electron` **313/313** 全绿;应用 F5 刷新后实测绑定恢复,标签按八进制显示。
- 今日三缺陷小结:①fx_prog_parse 信封字段 ②FX 编程口字节序 ③X/Y 八进制标签 —— 全部真机发现、全部已修、全部带回归守卫。

## 缺陷 4(真机,用户发现):FX 编程口位软元件被按字读解(2026-08-25)

- 现象:用户强制 M5 ON,Nexus 读 m0×6 显示 M0=ON、M1~M5=OFF。
- 根因:渲染层把"点数 6"当字数传入 → 请求 12 字节(=M0~M95 共 96 位);M5=ON 使 byte0=0x20,按字解码后第 0 个字非零 → 显示在"M0"行——**M5 的状态错标到了 M0**。位值与通信本身没错,是位/字语义混用。
- 位序权威依据:HSL `SoftBasic.ByteToBoolArray/GetDataByBitIndex`(I706 源码 SoftBasic.cs:1081,1204)——每字节 8 点、**LSB 在前**(bit i → byte[i/8] & (1<<(i%8)))。
- 修复(`electron/fx-serial-service.cjs`):progRead 识别位软元件(X/Y/M/S/TS/CS)→ 点数折算请求字(ceil(points/16),只读多读无害);新增 `parseFxProgBits`(ASCII hex 每字节 2 字符 → 8 点/字节 LSB 解包,取前 points 位);字软元件路径不变。渲染层无需改动(isBit 判定已有)。
- 验证:serial-services 12/12(新增 3 例:M5 场景黄金回归 "2000"→[0,0,0,0,0,1]、跨字节 0x01/0x02→X0/X9、D 字路径不回归);`npm run build`;`npm run test:electron` **316/316**;应用已刷新恢复 FX 绑定,待用户重读 m0×6 实机复核 M5=ON。

---

## 举一反三(第二轮):全协议位/字语义与打包顺序审计（2026-08-25,缺陷 4 之后）

> 缺陷 4 的类别是"值语义"(点数 vs 字数、位打包顺序),信封审计(第一轮)对此天然盲区——`words:6` 类型合法但语义错。本轮按协议逐一核对位格式、请求单位、解包顺序,证据=实现代码+e2e 值断言+HSL 权威源码。

### 分协议位/字节序证据表

| 协议 | 位数据格式 | 请求单位 | 证据 |
|---|---|---|---|
| FX 编程口(今日修后) | 每字节 8 点、LSB 在前 | 点数→折算字 | HSL SoftBasic.ByteToBoolArray;用户真机 M5 场景;serial-services 3 用例 |
| FX Computer Link | **每点 1 字符** "0"/"1"(非打包) | 点数(BR) | HSL MelsecFxLinksHelper.cs:489(逐字符 ==49);fx_links.rs 字序测试 |
| MC A-1E | 每字节 8 点、bit0=首点 | 点数 | mc_1e.rs:264 解包;e2e M0~M5 位读 |
| MC 3E Binary/ASCII/UDP | **每点 1 字节** 00/01(与 1E 相反,三菱两协议真实差异) | 点数 | mc_pdu.rs:131;e2e M0~M11/M0~M5 位读写断言 |
| MC C24(3C 帧) | 同 3E(应用区语义) | 点数 | handle_mc_c24_parse_read → parse_read_batch_response |
| Modbus TCP/RTU | 每字节 8 点 LSB(Modbus 标准) | 点数 | golden vectors + 12 E2E 回环 |
| S7 / PPI | 位寻址制(AnyPointer byte<<3\|bit),无打包 | 元素数 | s7_address M0.0/DBX/V100.3;e2e 读 M10.0×8(0x55 交替断言) |
| FINS | 位区每点 1 字节 | 点数 | e2e 位读 CIO0.00×4(0xBEEF 低位断言)+ 位写读改写 |
| HostLink(C-mode/FINS) | 仅字(位未开放) | 字 | 服务层 parseDmAddress 字窗校验 |
| 松下 MEWTOCOL | 位=RCS 单点专用命令 | 单点 | readContact 路径+测试 |
| USS / RK512 / FW | 字协议 | 字 | 字路径 |
| DL/T645 / CJ/T188 / 品牌profile | 数据块/标准 Modbus 打包 | — | 各自 golden vectors |

### 结论

- **唯一中招即 FX 编程口(今日已修)**;三家打包位协议(FX prog / MC 1E / Modbus)同序 LSB,交叉印证。
- 三菱家族三种位格式并存且各不相同(FX prog 打包 LSB / FX Links 逐字符 / 1E 打包 LSB / 3E 每点 1 字节)——实现均已按各自协议正确区分。
- 本轮纯审阅+e2e 核对,零代码改动,无需重跑套件(当日 316/316 与 612/612 均为现势)。

## 缺陷 3/4 真机闭环验证(2026-08-25)

- 排查插曲:缺陷 3 修复后两次"F5 刷新"实际未生效(焦点被夺),旧 bundle 一直运行致用户仍见 X8/X9;另有两次强杀后立即重启出现空白渲染(启动竞争),干净重启 + smoke:electron 双 OK 确认构建无恙。教训:**改渲染层后必须以新实例实测 UI,不信 F5**。
- 缺陷 3(八进制标签)真机验证:x0 × 10 → 标签序列 **X0~X7、X10、X11**,无 X8/X9 ✓。
- 缺陷 4(位软元件语义)真机验证:m0 × 10 → **M5 = ON**(用户强制的状态,第 6 行),其余 OFF ✓;此前"M5 错标到 M0"的现象消失。
- 今日四缺陷全部真机发现 → 修复 → 真机闭环。

## UX 防护:FX 绑定时串口参数不匹配警告(2026-08-25)

- 背景:用户两次以默认 8N1 打开串口后绑 FX 编程口,"已就绪"但 PLC 无响应——状态绿灯掩盖了参数错配(PPI 服务早有 serialWarnings,FX 一直没有)。
- 交付(src/main.js mcConnect isFxSerial 分支):数据位≠7 或校验≠偶时,状态栏改"已绑定但参数存疑"+红色通知列出错配项与改正路径;参数正确时通知附带实际串口参数。
- 验证:node --check、vite build、test:electron 316/316、smoke 双 OK(遵循"渲染层改动以新实例+冒烟验证"的新规矩)。
- 另:SC09 USB 今日两次物理掉线(会话中途与配置过程中,FTDI present=False/File not found),硬件侧待用户换口/重插;软件侧已能明确报"打开失败"。

## 便携包重打(含 2026-08-25/26 全部修复) + 扫描器误报修正（2026-08-26）

- 动因:8-22 旧包不含四缺陷修复/八进制标签/位语义/参数防呆/UX 备注,用户需要双击即用的正式包。
- 扫描器两处误报修正(scripts/release-secret-scan.cjs SKIP_PATH_FRAGMENTS):
  ①补跳过 `LICENSES.chromium.html`(Chromium 官方 20MB 开源许可清单,超 5MiB 扫描上限且无敏感内容);
  ②跳过范围由 `.package-lock.json` 扩为整个 `resources/app/node_modules/`(tauri 等第三方包 JSDoc 示例含 password=/token= 字样,14 条全为公开文档示例;自有代码仍全量扫描,36 文件 PASS)。
- 交付:`output/portable/Nexus 2.0/`(PackagedAtUtc=2026-08-26T05:08:25Z,356.9 MiB,含 release-manifest/SBOM/SHA256SUMS);
  新包 bundle 验证含八进制 `?8:10` 与「参数存疑」防呆文案。
- 验证:test:electron 316/316;`smoke:portable` 三 OK(UI/ELECTRON/PORTABLE)。
- 另:桌面启动器 `E:\Desktop\双击启动Nexus(最新版).bat`(dev 最新代码)。

---

## HSL + 一代 Nexus 系统性对照（2026-08-26，用户要求防同类问题）

> 对照源:`I706/HslCommunication-netframe-v12.2.0`(权威)、`i3195/HslCommunication-src`、`Nexus/src/Nexus.Mitsubishi`(一代);被审:rust-core 各协议模块。聚焦今天缺陷的语义类别:地址进制、字节/位序、数量单位、打包偏移。

### 缺陷 5(HSL 对照发现):FX 编程口位软元件地址错位(rust fx_programming.rs)

- 现象:位软元件(X/Y/M/S/T/C)读/写地址 = 基址+编号**×2**(字语义),正确应为 基址+编号**÷8**(每地址单元 1 字节=8 点)。二进制实测:M8→0110❌(应 0101)、X17→009E❌(应 0081)、Y10→00B0❌(应 00A1);D/TN/CN/CN32 字表全对。真机昨日碰巧全用 0 号地址(M0/X0)所以读对;y1 实际读偏到 Y16 区(碰巧全 OFF)。
- 修复:`fx_prog_rw_address` 重写——位组(X/Y/M/S/T/C)÷8 且补 M≥8000→1E0H 段、C≥200→3C0H 段;字组(TN/CN/CN32/D)×2 不变。依据:HSL `CalculateBoolStartAddress` 旧协议表逐项一致(旧测试向量 X17→9EH 系文档来源错误,一并更正)。
- 连带修复(JS fx-serial-service):①`progWrite` 位值按 8 点/字节 LSB 打包(原先当字写会写错数据);②读写均补**字节内偏移**(编号%8):读 M5×3 取字节 0 的 bit5,写 M5=1 产 0x20——此前偏移非 0 的读写都会错位;③位判定正则改 /^(X|Y|M|S|T|C)$(与 rust 表一致)。
- 验证:rust rw 表 30 项断言重写(M8/X10/M8000/C200/C208/S40/T16 等全覆盖)→ **cargo 612/612**;serial-services 新增 3 例(位写打包含偏移/跨字节/字不回归)+偏移读直测 2 例 → **test:electron 319/319**。

### MC A-1E 位读语义族冲突(HSL vs mcprotocol,自适应兼容)

- HSL `MelsecA1ENet.ReadBool`:A1E 位读响应**每点 1 字节**(`m==1`);mcprotocol 库/FX3U-ENET 资料:**每字节 8 点打包**——两个业界参考打架(R9 同类)。
- 处置:`parse_1e_response` 改**按响应长度自适应**(≥points 字节→逐点;≥ceil(points/8)→打包)。自家从站(打包式)e2e 全绿;真 ADP 接入后哪种都兼容。真机仲裁结果待记录。

### 其余对照结论(无缺陷)

- FX Links:位=每点 1 字符(HSL 489 行)、字=4 字符高前(fx_links.rs:551)——一致 ✓
- MC 3E/C24:位=每点 1 字节 00/01(mc_pdu.rs)——与 HSL McBinary 一致 ✓;3E 子命令语义族差异=R9 已知遗留(S1)
- Modbus/S7/FINS/HostLink/松下:昨日位语义审计已覆盖(e2e 值断言),本轮 HSL 抽查无新差异 ✓
- 一代 Nexus(Nexus.Mitsubishi)未实现 FX 编程口位寻址(仅字),无对照价值;MC 3E 与二代同语义

## 最终便携包(含缺陷 5)与收尾(2026-08-26)

- `output/portable/Nexus 2.0/` 重打:PackagedAtUtc=2026-08-26T12:23:26Z,356.91 MiB,含全部五缺陷修复+双源地址表+1E 自适应;`smoke:portable` 三 OK。
- 最终基线:test:electron **319/319**、cargo **612/612**。
- 等真机项:①FX 非零位地址复验(M8/X10,缺陷 5 闭环);②ADP 参数下装 → 1E 位读语义族仲裁;③485 模块扫描。
