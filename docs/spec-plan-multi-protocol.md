# Nexus 2.0 多协议支持 Spec Plan —— 三菱 + 西门子

> 本文档是继 Modbus 模块（已完成，223 测试全绿 + pymodbus 51 项交叉验证）之后的第二阶段总体规划。
> 目标：把 Nexus 从「Modbus 专用工具」升级为「多协议工业通讯工作台」。
> 详细协议字节级规范见附件：《三菱全协议设计文档.md》《西门子全协议设计文档.md》。

---

## 0. 现状基线（2026-08）

| 项 | 状态 |
|---|---|
| Modbus 主站 | ✅ 17 FC 全通（RTU/ASCII/TCP/UDP/RtuOverTcp/AsciiOverTcp 6 传输） |
| Modbus 从站 | ✅ TCP + RTU 串口 |
| 串口调试 | ✅ HEX/ASCII 收发、CRC/LRC、拼帧 |
| 报文解析 | ✅ 离线 RTU/ASCII/TCP/自动识别 |
| 轮询引擎 | ✅ 流式推送 + 点表批量合并优化（8 点 → 1 事务） |
| 趋势图/导出/持久化/多标签 | ✅ |
| 测试 | 223 自动化测试 + pymodbus 51 项第三方交叉验证，0 失败 |
| 架构 | Electron(UI) ⇄ JSONL(stdin/stdout) ⇄ Rust core(协议引擎) |

**核心资产**：协议引擎与 UI 完全解耦——新增协议只需扩展 Rust core 的命令分发 + UI 加页面，不动现有代码。

---

## 1. 协议选型结论（调研已完成）

### 1.1 三菱（MELSEC）

| 接口 | 协议 | 可实现 | 优先级 |
|------|------|--------|--------|
| 以太网 TCP:5000 | **MC Binary 3E/4E 帧** | ✅ 公开(手册 SH-080008) | **P0** |
| 以太网 TCP:5001 | MC ASCII | ✅ 公开 | P2 |
| 以太网 TCP:5000 | SLMP 1E 帧(FX5U) | ✅ 公开(SH080956) | P3 |
| 以太网 TCP:5560 | MELSOFT 私有 | ❌ 闭源 | 不做 |
| 串口 RS-232/485 | MC over C24 模块 | ✅ 公开 | P2 |
| 串口 | FX 专用协议(Computer Link) | ✅ 半公开 | P3 |
| 串口/USB-SC09 | FX 编程口协议 | ✅ 半公开 | P3 |
| USB | iQ-R/FX5U USB 口 | ⚠️ 走 MELSOFT，闭源 | 不做 |
| 现场总线 | CC-Link / CC-Link IE | ❌ 硬件层 | 不做 |

### 1.2 西门子（SIMATIC）

| 接口 | 协议 | 可实现 | 优先级 |
|------|------|--------|--------|
| 以太网 TCP:102 | **S7comm (ISO-on-TCP)** | ✅ 逆向完整(snap7 生态) | **P0** |
| 以太网 TCP:102 | S7comm-Plus (1200/1500) | ⚠️ 加密，逆向有限 | P3 |
| 以太网 TCP:4840 | OPC UA | ✅ 公开 | P3（独立大模块） |
| 以太网 | PROFINET RT/IRT | ❌ 二层实时协议 | 不做 |
| 串口 RS-485 | PPI (S7-200) | ✅ 半公开 | P1 |
| 串口 RS-485 | MPI (S7-300/400) | ✅ 半公开 | P2 |
| 串口 | Freeport 自由口 | ✅（已有串口调试覆盖） | 已有 |
| USB | USB-MPI/DP 编程电缆 | ⚠️ 内部转 RS-485，跑 PPI/MPI | 随 P1/P2 |
| 现场总线 | PROFIBUS DP/PA | ❌ 硬件层 | 不做 |

### 1.3 关键决策

1. **首战打以太网**：MC 3E（TCP 5000）+ S7comm（TCP 102）双线并进，覆盖两大阵营 95% 现役设备（iQ-R/iQ-F/FX5U/Q/L + S7-300/400/1200/1500）。
2. **串口协议次之**：PPI → MC串口 → MPI → FX 串口三兄弟。串口路径已由 Electron 持有（Modbus RTU 同款管道），复用成本低。
3. **明确不做**：一切私有加密（MELSOFT/S7comm-Plus）和硬件现场总线（CC-Link/PROFIBUS/PROFINET RT）。文档中写明原因与用户引导。

---

## 2. 阶段划分

### Phase M1 —— 三菱 MC Binary 3E（P0，预计 8 人日）

**交付**：能连 iQ-R/FX5U/Q 系列，读写 D/M/X/Y。

| 任务 | 模块 | 要点 |
|------|------|------|
| 地址解析器 | `mc_address.rs` | `"D100"`→(device code 2B, 头设备号 3B 小端)；按区域分进制：X/Y 八进制、B/W 十六进制、其余十进制 |
| 指令层 | `mc_pdu.rs` | 0401 成批读(字/位)、1401 成批写(字/位)；子命令 0000/0001；**全小端，禁用 Modbus 的 to_be_bytes** |
| 帧层 | `mc_frame.rs` | 3E 帧副帧头 5000H/响应 D000H；访问路径 5B(网络00+PC FF+IO 03FF+站00)；监视定时器；响应结束代码解析 |
| 命令分发 | `protocol.rs` | `build_mc_*`/`parse_mc_*`（离线）+ `mc_tcp_read`/`mc_tcp_write`（在线） |
| 连接模型 | `session.rs` | `Connection::McTcp { route, frame_type }` 变体；`open_tcp_connection` 增加 `protocol:"melsec"` 字段 |
| 轮询泛化 | `session.rs` | `PollOp::Melsec { device, head, points, is_bit }`（Modbus 轮询零改动） |
| 虚拟从站 | `mc_slave.rs` | 内存模型按 device 区映射（对齐 modbus_slave.rs 模式），供自测 |
| 测试 | `tests/` | 报文向量对比（读 D100 期望 `50 00 00 FF FF 03 00 0D 00 ...`）+ E2E 主从回环 |
| 前端 | `index.html`/`main.js` | 侧边栏加「三菱 MC」入口；连接卡片(IP+网络号+PC号+IO号+站号)；地址输入框支持 `D100` 语法；读写按钮复用 |

**验收标准**：① 虚拟从站回环读写全通过 ② 与 HslCommunication 报文逐字节一致 ③ UI 可完成 连接→读 D100→写 M100→轮询 全流程。

### Phase S1 —— 西门子 S7comm（P0，预计 10 人日）

**交付**：能连 S7-1200/1500/300/400，读写 M/DB/I/Q 区。

| 任务 | 模块 | 要点 |
|------|------|------|
| 连接握手 | `s7_client.rs` | TCP 102；TPKT(4B)+COTP(7B 连接请求/确认)；ISO-on-TCP 建链 |
| S7 头与作业 | `s7_pdu.rs` | S7 头 10-12B（protocol id 0x32 + rosctr + pdu ref + ack）；Job 0x04 读/0x05 写 |
| 地址编码 | `s7_address.rs` | `M0.0`/`DB1.DBW0`/`IB0` 解析 → S7 AnyPointer（区域码 I/Q/M/DB + 地址计算：字节×8+位）；**西门子大端** |
| TSAP 配置 | `s7_client.rs` | 本地/远程 TSAP（0x0102/0x0201 等），按型号默认值 + 可配置 |
| PDU 协商 | `s7_client.rs` | 连接后协商 PDU 长度（0x0601 Request PDU），读块大小自适应 |
| 读写实现 | `s7_pdu.rs` | Read Var / Write Var；位/字节/字/双字；多 item 请求 |
| 命令分发 | `protocol.rs` | `s7_connect`/`s7_read`/`s7_write`/`s7_disconnect` |
| 虚拟 S7 服务端 | `s7_slave.rs` | 用于自测（TSAP 握手 + 内存区回填） |
| 测试 | `tests/` | snap7 报文向量对比 + E2E 回环 |
| 前端 | `index.html`/`main.js` | 侧边栏「西门子 S7」；连接卡片(IP+机架+槽位+TSAP)；地址框 `DB1.DBW0`/`M0`；PUT/GET 提示 |

**验收标准**：① 回环读写全通过 ② 报文与 snap7 抓包逐字节一致 ③ UI 全流程可用。

### Phase M2 —— 三菱进阶（P1，预计 5 人日）

- 0403 随机读 / 0406 多块读 / 1402/1406 对应写
- 4E 帧（序列号，UDP 场景）
- MC ASCII 帧（端口 5001）
- 远程 RUN(1002)/STOP(1006)/RESET(1001)、时钟读写、回送测试
- 结束代码 → 人类可读错误信息映射表

### Phase S2 —— 西门子串口 PPI（P1，预计 5 人日）

- PPI 帧完整实现（SD 68H ... FCS ED 16H）
- 复用 Electron 串口管道（Modbus RTU 同款 transact 模式）
- S7-200 Smart 支持验证

### Phase M3 —— 三菱串口 + FX（P2，预计 6 人日）

- MC over 串口（C24）
- FX 专用协议（Computer Link：ENQ/ACK/NAK + 和校验）
- FX 编程口协议（STX 格式 + 地址编码公式 ×2+1000H）

### Phase S3 —— 西门子 MPI + 进阶（P2，预计 4 人日）

- MPI 协议（RS-485, 187.5kbps）
- S7 CPU 控制（启停/状态）
- S7-1200/1500 保护等级绕过指引（PUT/GET 使能说明，写入文档不写代码）

### Phase X —— 统一体验层（贯穿各阶段）

| 任务 | 说明 |
|------|------|
| 地址 DSL | 统一地址语法：`modbus://hr/100`、`melsec://D/100`、`s7://DB1.DBW0`（供点表/轮询复用） |
| 协议抽象接口 | Rust trait `Protocol { build_read, build_write, parse_resp }`，三种协议实现同一接口 |
| 点表协议无关化 | 点表记录增加 `protocol` 字段，轮询引擎按 PollOp 分派 |
| 报文解析器扩展 | 离线解析器认出 MC/S7 帧（副帧头 5000H / TPKT 0300）自动路由到对应解析器 |
| 示例代码生成 | 三语言模板增加三菱/西门子分支 |

---

## 3. 架构扩展原则（不变式）

1. **Rust core 是唯一协议引擎**——Electron 不碰协议字节，只转发 JSONL。
2. **协议模块自治**——`mc_*.rs`/`s7_*.rs` 不 import `modbus_*.rs`，字节序冲突（大端 vs 小端）物理隔离。
3. **现有 Modbus 零回归**——每阶段结束跑全量 223+ 测试，任何 Modbus 测试变红即阻塞发布。
4. **每个协议必须有虚拟对端**——`mc_slave`/`s7_slave` 先于在线功能交付，保证无真机也能 E2E。
5. **交叉验证优先**——三菱对比 HslCommunication 报文，西门子对比 snap7 报文；期望值只来自官方手册/权威开源，禁止从自家实现反推。

---

## 4. 里程碑与验收

| 里程碑 | 内容 | 验收门槛 |
|--------|------|---------|
| **M-M1** | 三菱 MC 3E 读写 | 虚拟从站 E2E + 报文向量全绿 + Modbus 223 无回归 |
| **M-S1** | 西门子 S7 读写 | 同上（对比 snap7） |
| **M-双协议版** | UI 双页面 + 轮询/点表支持双协议 | 手动全流程 + 持久化 |
| **M-M2/S2** | 进阶指令 + PPI | 各自 E2E |
| **1.0 发布** | 打包 exe + 三协议文档 | 真机各 1 台验证 |

---

## 5. 风险与开放问题

| 风险 | 缓解 |
|------|------|
| S7-1200/1500 默认禁 PUT/GET | 文档写清 PLC 侧设置步骤；连接失败给出精确提示 |
| 三菱监视定时器单位各系列有差异 | 默认 0010H，做成可配置，官方手册值写入文档 |
| FX 编程口地址编码易错（×2+1000H 公式） | 单元测试覆盖边界（地址 0、最大地址） |
| S7comm-Plus 加密无法支持 | 明确告知用户走 S7comm（1200/1500 兼容模式） |
| 多协议并发内存增长 | 每 Connection 独立 buffer，轮询流互不共享 |
| 字节序混用（MC 小端/S7 大端/Modbus 大端） | 模块物理隔离 + 编译期类型区分 + 专项字节序测试 |

---

## 6. 立即行动（下一步）

1. ✅ 等两份附件文档产出（三菱/西门子全协议设计文档）
2. **Phase M1 开工**：`mc_address.rs` → `mc_pdu.rs` → `mc_frame.rs` → 测试 → 在线 → UI
3. Phase S1 紧随：可与 M1 并行（不同模块，无文件冲突）
