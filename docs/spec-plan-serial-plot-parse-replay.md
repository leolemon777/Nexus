# spec-plan —— 串口数据可视化：曲线面板 / 自定义帧解析 / 会话录制回放

> 状态：草案（2026-08-30，待确认后登记进主索引与路线图）
> 目标：把串口调试页从"看报文"升级为"看数据"——多通道实时曲线、表单式自定义帧解析、整段会话录制与回放，全部真机可用。
> 原则：
> ① 零借鉴 Serial Studio 代码/资源/文件格式（其核心 GPL-3.0 且双许可要求商用购 Pro，本 spec 只对标功能形态，独立实现）；
> ② 解析逻辑一律进 Rust core（黄金向量 + 单测），渲染层只消费结构化数据；
> ③ UI 完全复用既有设计 token 与 30px 控件基线，新面板必须通过 audit-ui-layout.cjs；
> ④ 每个批次独立可交付，全量测试不回落；
> ⑤ 证据分级诚实：golden vectors 只算软件证据（S2/S3），真机曲线验收单列（L2）。
>
> 主索引：[spec-plan.md](./spec-plan.md)

## 背景

### 对标来源

Serial Studio（github.com/Serial-Studio/Serial-Studio）是 Qt/C++ 开源遥测仪表盘，核心能力为表单式免代码帧定义、多通道实时曲线、会话记录回放。其授权为 GPL-3.0 核心 + 闭源 Pro 双许可，条款激进（自编译 GPL 版商用也要求购 Pro）。**本 spec 仅对标功能形态，不复制其任何代码、文档、资源或 JSON 项目文件格式**；`THIRD_PARTY_NOTICES.md` 不新增条目。

### 现状核查（2026-08-30 源码）

| 能力 | 现状 | 位置 |
|---|---|---|
| 实时趋势图（主站页） | ✅ 已有：手写 Canvas 2D（`trendSeries`/`trendFeed`/`drawTrendChart`/`trendLoop`），统一 Y 轴、HiDPI、60s 窗口、300 点/通道、颜色池 | `src/main.js`、`index.html` `#trend-canvas` |
| 调试页曲线 | ❌ 无 | `#debug-view` 仅三卡：串口调试 / 收发记录 / 校验工具 |
| 在线/离线报文解析 | ✅ `parse_frame_online`/`parse_frame_offline`（`FrameInfo`）；离线读响应解析命令族（如 `parse_read_holding_registers`）已注册 | `rust-core/src/frame_parser.rs`、`rust-core/src/protocol.rs` |
| 自定义（非标协议）帧解析 | ❌ 无——非内置协议的帧只能看 hex | — |
| 帧记录 | 🟡 仅内存环形缓冲：主进程 `frameLog` 1000 条、渲染层 `traceHistory` 5000 条，关窗即失 | `electron/serial-debug-service.cjs`、`src/main.js` |
| CSV 导出 | ✅ `exportCsv`（点表/寄存器）+ `exportTraceLog`（TX/RX 追踪） | `electron/data-export-service.cjs` |
| 会话录制落盘 / 回放 | ❌ 从未规划（gap-analysis / ROADMAP 均无记录） | — |

结论：gap-analysis G4 的主站侧趋势图已落地；**调试页曲线、自定义帧解析、录制回放三块是净新增且互相配套**——非标设备的曲线必须先有解析，回放必须先有录制。这也是 Serial Studio 对标中 GPL 版就免费、而 Nexus 可做到"全开源内置"的差异化点（其把 Modbus 等放 Pro 收费）。

### 与既有文档的关系

- `gap-analysis.md` G4（缩放/单位/条件着色/趋势图）：主站侧已闭合；本 spec 补调试侧曲线，缩放与条件着色列为后续增强。
- `PRODUCT_COMPLETION_ROADMAP.md` 后续功能池「长期历史趋势与告警导出」「CSV 实时流」：本 spec 批次 3 覆盖录制与导出部分，长期历史库与告警仍在池内。
- `spec-plan-desktop-demo-hardware-p0.md`：RS-485 温度模块是批次 2 自定义帧解析的首个真机对象（温度值上曲线即 L2 证据）。

## 目标

串口调试页新增三件套，分三批交付，每批独立可发布：

1. **批次 1 —— 曲线面板**：`#debug-view` 新增"实时曲线"卡片，多通道 Canvas 绘制；通道来源两路（内置协议自动解析 + 手动字节偏移规则）；支持暂停/清空/CSV 导出。
2. **批次 2 —— 自定义帧解析栈**：Rust core 新增 frame definition 解析模块（表单定义帧头/定长/校验/字段偏移），调试页表单 UI，定义持久化进 `.nexus.json`，曲线卡绑定命名字段。
3. **批次 3 —— 会话录制回放**：整段 TX/RX 会话落盘为 JSONL；回放控制器按原始时间轴把记录重放进现有 debug_frame 渲染管线（报文窗、曲线、解析一致重演）。

### 范围外

- FFT、GPS 地图、相机画面、Painter 类 JS 插件控件、仪表盘控件（后续按需）
- 脚本式解析（原计划 Lua/JavaScript 递延项；2026-09-07 以自研表达式语言交付，见 B.8——刻意不用 Lua/JS 运行时）
- Serial Studio 项目文件格式兼容（刻意不做，许可隔离）
- 长期历史数据库（SQLite 会话库）、告警导出（留在 ROADMAP 功能池）
- MQTT/BLE 等非串口数据源（协议矩阵另有规划）

### 就绪标准

批次 1：
- [ ] `#debug-view` 曲线卡片：≥2 通道同图、暂停/继续、清空，60s 滚动窗口与主站趋势一致
- [ ] 通道来源 A：RX 帧自动离线解析（复用 `parse_read_holding_registers` 等既有命令）→ 寄存器通道
- [ ] 通道来源 B：手动字节偏移规则（帧头过滤 + offset + 类型 + 缩放 + 单位）
- [ ] 曲线数据 CSV 导出（复用 `exportCsv` IPC）
- [ ] `audit-ui-layout.cjs` debug 视图无新增违规（溢出/过窄/row-misaligned/重叠）
- [ ] Electron 全量测试通过且基线不回落（2026-08-25 基线 309，以 implementation-notes.md 为准）

批次 2：
- [ ] Rust `frame_definition` 模块 + 黄金向量（二进制定长 + ASCII 分隔两模式，含 CRC 正/反例）
- [ ] JSONL 命令 `custom_frame_parse`/`custom_frame_validate` 接线，四处白名单同步登记
- [ ] 调试页表单 UI（自解释 placeholder + 易混字段 title tooltip）
- [ ] 定义持久化进 `.nexus.json`（schemaVersion 迁移，旧文件可打开）
- [ ] 曲线卡可绑定命名字段为通道
- [ ] Rust 单测与 JSONL E2E 全绿（基线 612 / 166 integration 不回落）

批次 3：
- [ ] 录制 start/stop → JSONL 落盘（含 `recordVersion` 头，5MB 轮转另起新文件）
- [ ] 回放：1x/2x/4x/最大速/单步/暂停，重放进渲染层管线（报文窗 + 曲线一致）
- [ ] 录制文件可导出 CSV（时间/方向/hex/字节数，绑定 definition 时附字段值列）
- [ ] record-service 与回放调度器 node:test 单测，冒烟全绿

## A. 批次 1：串口调试曲线面板

### A.1 UI

`#debug-view` 在"收发记录"卡之后新增"实时曲线"卡片，沿用 `.card/.card-header/.card-body/.form-row/.input/.btn-primary/.btn-ghost` 与 `--control-height: 30px` 基线。构成：

- `<canvas>` 绘制区（HiDPI `setTransform(dpr,…)`，与主站一致）
- 通道列表：名称 / 颜色点 / 最新值 / 单位（统一 Y 轴，单位在列表侧显示——沿用主站趋势口径）
- 工具行：添加通道、暂停/继续、清空、导出 CSV

新元素 ID 统一 `plot-` 前缀（与既有 `dbg-`/`trend-` 命名空间隔离，规避 ID 重命名回归类）。CSP `script-src 'self'` 禁外链，图表一律手写 Canvas 2D，不引入图表库。

### A.2 绘制引擎

新模块 `src/serial-plot.js`（不改动主站 trend 代码路径）：`SeriesStore`（每通道 `{name,color,unit,dataPoints,maxPoints:300}`，60s 窗口裁剪）+ 绘制函数（网格、统一 Y 轴、clip、单点画圆，参照 `drawTrendChart` 实现）。自持 rAF 循环 + 100ms 节流，仅在 `activeView === "debug"` 且未暂停且 `!document.hidden` 时绘制——与主站 `trendLoop` 由 `activeView` 天然互斥，不复制第二份常驻循环。

暂停语义：冻结绘制但数据继续入环（恢复后补显），清空 = 丢弃全部通道数据点。

### A.3 通道来源（批次 1 两路）

- **来源 A（内置协议自动解析）**：RX 帧到达时（`onDebugFrame` 回调处）以 ≤50ms 合批调用既有离线解析命令族（`parse_read_holding_registers` 等，`protocol.rs` 已注册），成功则把 `registers` 逐个建通道（命名如 `HR[0]`）；解析失败静默跳过（限频记日志）。若既有命令返回缺结构化数值字段，仅做增量扩展，不改既有字段名。
- **来源 B（手动字节偏移规则）**：渲染层轻量规则——帧头过滤（hex，可空 = 全帧）+ 字节偏移 + 类型（u8/u16/i16/u32/i32/f32，大/小端）+ 缩放系数 + 单位 + 通道名。批次 2 落地后，此规则保留为"快速模式"，并评估降级为"生成 frame definition 的快捷入口"，保持单一真源在 Rust。

### A.4 导出

曲线窗口数据导出 CSV（首列 timestamp/ISO 时间，每通道一列），复用 `nexus:export_csv`（payload `{rows, filename}`，含 BOM）。

### A.5 测试

- `src/serial-plot.js` 纯逻辑（SeriesStore 裁剪/暂停语义/单位）node:test 单测
- `audit-ui-layout.cjs`：debug 视图新增卡片过全部检查
- `smoke-electron.mjs` 全绿

## B. 批次 2：自定义帧解析栈（Rust）

### B.1 数据模型（frame definition，schemaVersion 1）

```json
{
  "schemaVersion": 1,
  "name": "RS485 温度模块",
  "mode": "binary",
  "head": "01 03",
  "length": 9,
  "checksum": { "type": "crc16-modbus" },
  "fields": [
    { "name": "temp1", "offset": 3, "type": "i16", "byteOrder": "be", "scale": 0.1, "unit": "℃" },
    { "name": "temp2", "offset": 5, "type": "i16", "byteOrder": "be", "scale": 0.1, "unit": "℃" }
  ]
}
```

ASCII 分隔模式：

```json
{
  "schemaVersion": 1,
  "name": "电子秤",
  "mode": "ascii-delimited",
  "lineEnding": "\n",
  "separator": ",",
  "fields": [
    { "name": "weight", "index": 1, "type": "f32", "unit": "kg" }
  ]
}
```

- binary v1：可选帧头（hex）+ 定长 `length` + 可选校验（`none | sum8 | xor8 | crc16-modbus`，覆盖市面常见温湿度/称重模块）+ 字段偏移/类型/字节序/缩放/单位。
- ascii-delimited v1：行结束符 + 分隔符 + 字段序号。
- 校验失败、长度不足、帧头不匹配、字段越界一律返回显式错误枚举，不静默。
- `lengthField` 动态长度、尾部定界、脚本解析列为 B.7 后续增强。

### B.2 Rust 模块

新文件 `rust-core/src/frame_definition.rs`：`FrameDefinition`/`FieldDef` 结构体（`#[derive(Deserialize)]` + `rename_all="camelCase"` + `deny_unknown_fields`，与 `protocol.rs` 既有风格一致）+ 纯函数 `parse_custom_frame(bytes, &def) -> Vec<ParsedField>` + 错误枚举。接线三件套：`lib.rs` 加 `pub mod`、`protocol.rs` 加 payload 结构体与 dispatch 臂、handler 返回 `success(request_id, json!({...}), false)`。

命令（definition 每次内联传入，不进 Session 状态）：

- `custom_frame_parse` `{definition, bytes}` → `{fields: [{name, value, unit}]}` 或显式错误
- `custom_frame_validate` `{definition}` → 表单实时校验（字段越界/类型合法/校验配置自洽）

### B.3 表单 UI

调试页新增"帧解析"卡片：模式选择（定长二进制 / 分隔 ASCII）→ 动态表单；"试算"按钮取收发记录最新 RX 帧调用 `custom_frame_parse` 预览字段值。UX 规范：placeholder 必须自解释（单位示例 `℃`、缩放示例 `0.1`），易混字段（offset 起算 = 帧首字节为 0）带 title tooltip。

### B.4 持久化

`buildProjectDocument()` 的 workspace 段新增 `frameDefinitions: []`；`project-file-service` schemaVersion 1→2 迁移（只增可选字段，旧文件打开不报错）；帧定义不含凭据，随现有导入/导出/脱敏机制走。

### B.5 曲线卡绑定

通道选择器列出当前 definition 的命名字段 → 添加为曲线通道（批次 1 来源 B 的长期替代形态）。

### B.6 黄金向量与测试

新增 `docs/custom-frame-golden-vectors.md`（头部 `日期：YYYY-MM-DD` + `证据等级：S3`），≥8 向量：定长二进制含 CRC 正/反例、长度不足、帧头不匹配、字段越界、ASCII 多分隔符、scale 换算、i16 负值、f32 字节序。Rust 内联单测对齐向量；新增 `rust-core/tests/custom_frame_jsonl_e2e.rs`（validate → parse 闭环）并登记进 `scripts/test-rust-jsonl.ps1` 清单。

### B.7 后续增强

- ✅ lengthField 动态长度（2026-09-04 交付：`lengthField {offset,fieldType u8/u16,byteOrder,adjust}`，与定长互斥，帧总长=raw+adjust、不含尾部定界）
- ✅ 尾部定界符（2026-09-04 交付：`tail` HEX，解析前剥离，不计入长度与校验；空串=不使用）
- ✅ 脚本解析引擎（2026-09-07 交付，见 B.8）
- ✅ 条件着色（2026-09-07 交付，见 B.9）
- ✅ Y 轴缩放（2026-09-07 交付，见 B.10）

### B.8 脚本解析引擎（表达式模式）

**选型决策**：自研微型表达式语言，纯 Rust 零第三方依赖，不引入 Lua/rhai/JS。
理由：① JS 进渲染层需 CSP 开 `unsafe-eval`，安全倒退；② rhai 等第三方脚本 crate 扩大供应链面（rust-core 现仅 serde/serde_json/thiserror 三个依赖）；③ 表单已覆盖定长/动态长度/校验场景，脚本的剩余价值是自由算术（BCD、位提取、合成字段、自定义校验表达式），表达式语言足够；④ 沙箱边界即语言本身——不可表达赋值/循环/函数定义/I-O，无内存访问。

数据模型（frame definition schemaVersion 1，增补可选字段）：

```json
{
  "schemaVersion": 1,
  "name": "BCD 温湿度",
  "mode": "script",
  "accept": "frame[0] == 0x55",
  "verify": "(sum(0, len - 2) & 0xFF) == frame[len - 1]",
  "fields": [
    { "name": "temp", "expr": "bcd(frame[1]) * 100 + bcd(frame[2])", "scale": 0.01, "unit": "℃" }
  ]
}
```

- 语言：数值单类型（f64，整型位运算内部转 i64）；变量 `frame[i]`（字节，越界=显式错误）、`len`；运算符 `+ - * / % & | ^ << >> ~ ( ) == != < <= > >= && || ! ?:`；字面量十进制/`0x` 十六进制。
- 内置函数（全纯函数）：`bit(x,n)` 取位、`bcd(x)` BCD→二进制、`sum(a,b)`/`xor(a,b)` 帧字节区间累加/异或（含端点）、`crc16(a,b)` 区间 CRC16-MODBUS、`abs/min/max`。
- 限额（防失控）：单表达式 ≤256 字符、AST 深度 ≤16、求值步数 ≤10000、除零/非有限结果/索引越界一律显式错误码（`EXPR_SYNTAX`/`EXPR_EVAL`/`EXPR_LIMIT`），绝不静默。
- 语义：`accept`（可空）求值 0=拒帧（`FRAME_REJECTED`）；`verify`（可空）求值 0=校验失败（`SCRIPT_VERIFY_FAILED`）；字段值 = `expr` 求值 × `scale`，单位沿用 `unit`；脚本模式不支持 head/length/lengthField/tail/checksum（validate 显式报问题）。优先级与 C 一致——`& | ^` 低于 `==`，位与比较要写 `(sum(0,len-2) & 0xFF) == frame[len-1]`（UI tooltip 同步提醒）。
- 接线：复用 `custom_frame_parse`/`custom_frame_validate` 命令（definition 内联传入），IPC 白名单零改动；UI 表单模式选择加「脚本表达式」，字段表在脚本模式下呈现 名称/表达式/缩放/单位 列。

### B.9 条件着色（收发记录）

- 规则模型：`{ source, op, value, color }`；source ∈ `direction`（TX/RX）、`length`（帧字节数）、`field:<字段名>`（当前已应用帧定义的解析值）；op ∈ `== != > >= < <= contains`（contains 仅 direction/field 文本与 length 无意义时忽略）；color 必须为 `#RRGGBB`。
- 求值：渲染层纯模块 `src/frame-color-rules.js`（`validateColorRule`/`evaluateColorRules`，node:test 单测），规则按序首中即停。
- 两段式着色：行到达即应用 direction/length 规则；`field:` 规则在该帧异步解析完成（≤50ms 合批）后回填同一行（timestamp→行 索引）。
- 命中样式：行左侧 4px 色条（不动文字色，保可读性）。
- 持久化：`workspace.colorRules`（可选数组，上限 32 条，单条非法剔除）；随项目保存/恢复，脱敏导出保留（纯结构无凭据）。

### B.10 Y 轴缩放（曲线卡）

- `serial-plot.js` 抽出纯函数 `computeYRange(store, override)`：自动=现行为（窗口内 min/max + 8% padding + 退化保护）；override 为合法 `{min,max}`（有限且 min<max）时精确使用，不加 padding。
- UI：曲线卡工具行加「Y 轴」手动 min/max 输入（空=自动），即刻生效；会话级视图状态，不持久化（与主站趋势口径一致）。
- 测试：`scripts/serial-plot.test.cjs` 增 computeYRange 用例（自动/覆盖/非法忽略/退化）。

## C. 批次 3：会话录制与回放

### C.1 录制

新服务 `electron/record-service.cjs`：在 `serial-debug-service` 的 `onFrameCallback` 链上并行挂钩（不改 `_recordFrame` 函数体），追加写用户选定路径（dialog，默认 `userData/sessions/session-YYYYMMDD-HHMMSS.nxsession.jsonl`）。格式：

```
{"recordVersion":1,"transport":"serial","startedAt":"…"}     ← 首行头
{"ts":1724987123456,"dir":"RX","bytes":[1,3,8,0,25,…]}       ← 每帧一行
```

5MB 轮转参照 `write-audit` 先例（超限另起新文件，不丢弃）。IPC：`nexus:record_start` / `record_stop` / `record_status`（主进程落盘，崩溃恢复口径与 workspace-recovery 一致）。

### C.2 回放

渲染层回放控制器（曲线卡工具行）：选择录制文件（新增 `nexus:record_read`）→ 校验 `recordVersion` → 播放模式（1x/2x/4x/最大速/单步/暂停）→ 按相邻 `ts` 差调度（相对差，防系统时钟跳变），把记录逐条重放进**渲染层现有处理函数**（`appendDebugLog` + 曲线喂点 + 自动解析）——不伪造 `webContents.send`/IPC 事件（contextIsolation 安全边界）。

### C.3 导出

录制文件 → CSV（时间/方向/hex/字节数；若当前绑定 definition，附各字段值列）：新增 `nexus:record_export_csv`；既有 `exportTraceLog` 不动（向后兼容）。

### C.4 测试

- `record-service` node:test：临时目录、5MB 轮转、header 校验、坏行容错（跳过并计数）
- 回放调度器纯逻辑单测：ts 差计算、倍速、单步、暂停恢复
- `smoke-electron.mjs` 全绿

## 测试要求（汇总，四层）

| 层 | 内容 | 基线（以 implementation-notes.md 为准） |
|---|---|---|
| Rust 单元 | `frame_definition.rs` 内联向量测试（≥12 用例） | 612（单元 446）不回落 |
| Rust JSONL E2E | `custom_frame_jsonl_e2e.rs` 新增并登记 ps1 清单 | integration 166 递增 |
| Electron（node --test） | serial-plot / record-service / 回放调度器 / schemaVersion 迁移用例 | 309 递增不回落 |
| UI | `audit-ui-layout.cjs`（debug 视图新卡片全检查）+ `smoke-electron.mjs` | 无新增违规 |

真机（L2，对齐 P0 证据分级）：RS-485 温度模块 + 批次 2 帧定义 → 温度曲线 + 录制文件作为证据；真机缺席时如实标注"待真机"，不宣称完成。

## 风险与注意事项

1. **GPL-3.0 隔离（最高优先）**：Serial Studio 核心 GPL-3.0 且双许可激进；本 spec 全部独立实现，不复制其代码/文档/资源/JSON 项目格式。对标仅引用其公开功能描述。
2. **渲染层巨石文件**：`src/main.js`（约 8800 行）/`index.html`（约 2000 行）为单文件；新逻辑一律独立模块（`src/serial-plot.js`、回放调度器），`main.js` 只做接线，缩小回归面。警惕既有三类 UI 回归（data-transport CSS 丢失、hidden vs CSS、ID 重命名）。
3. **双绘制循环**：主站 `trendLoop` 仅 master 视图绘制；调试页曲线循环必须同样受 `activeView` 门控，禁止第二份常驻 rAF 循环拖慢低端机。
4. **IPC 白名单四处同步**：新命令需同时登记 `preload.cjs` allowedCommands、`main.cjs` `ipcMain.handle` 透传、`rust-core-client.cjs` COMMANDS 表、`protocol.rs` dispatch；漏一处即"不允许调用桌面命令"。本 spec 新命令全集：`custom_frame_parse`、`custom_frame_validate`、`record_start`、`record_stop`、`record_status`、`record_read`、`record_export_csv`。
5. **性能**：来源 A 每帧离线解析按 ≤50ms 合批下发；曲线每通道 300 点上限；录制 5MB 轮转。
6. **回放真实感**：回放走渲染层管线而非伪造 `nexus:debug_frame`；倍速 = ts 差 ÷ 倍率。
7. **schemaVersion 迁移**：`.nexus.json` 1→2 只增可选字段；迁移测试覆盖 1→2 与缺省路径。
8. **证据诚实**：golden vectors 与 JSONL E2E 属软件证据（S2/S3）；真机 L2 验收依赖 RS-485 模块在场，缺席时按 P0 证据分级表标注。

## 对标参考

| 参考 | 位置 | 借鉴点（功能形态，非代码） |
|---|---|---|
| Serial Studio | github.com/Serial-Studio/Serial-Studio | 表单式免代码帧定义、多通道实时曲线、会话录制回放的产品形态；其 Modbus 等在 Pro 收费，Nexus 全开源内置是差异化 |
| Modbus Poll | `gap-analysis.md` G4 | Real time Charting 对标（主站侧已闭合，本 spec 补调试侧） |
| 主站页实时趋势 | `src/main.js` trend 实现 | 内部范本：Canvas 2D、HiDPI、60s 窗口、颜色池、rAF 节流 |
| .NET Nexus（工作区 `nexus/`） | `E:\Desktop\项目汇总\Nexus2.0\nexus` | 核查结论：无可视化/回放既成实现，无内部范本可抄 |

## 文档登记（采纳后执行）

1. `README.md`「技术文档」表加本文件行
2. `spec-plan.md` 进度日志加条目（不新增阶段号，本 spec 为专题批次）
3. `PRODUCT_COMPLETION_ROADMAP.md` 后续功能池「长期历史趋势」「CSV 实时流」标注由本 spec 批次 3 部分覆盖
4. 实施时 `implementation-notes.md` 顶部基线后追加 `## 功能批次：串口可视化三件套（YYYY-MM-DD）`
