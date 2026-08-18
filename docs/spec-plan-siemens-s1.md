# Phase S1 实施蓝图 —— 西门子 S7comm over ISO-on-TCP（v1.0 定稿）

> 状态：**定稿**。三路调研（2026-08-15）已返回并完成对账：
> ① `docs/research/siemens-protocol-deep-dive.md`（协议字节级，三重来源印证）
> ② `docs/research/siemens-opensource-landscape.md`（7 库源码级对比）
> ③ `docs/research/siemens-voc.md`（GitHub 529 issue + 中文现场画像）
> 主文档《西门子全协议设计文档.md》已同步修订 12 处（勘误注见其头部）。
> 对账记录：`docs/research/siemens-doc-audit-notes.md`（D1-D8 全部定论）。

## 1. 目标与范围

**做**：S7-300 / 400 / 1200 / 1500（非优化块）/ **200 SMART** 的以太网读写，TCP 102，S7comm (0x32)。

- 存储区：I(0x81) / Q(0x82) / M(0x83) / DB(0x84) / T(0x1D) / C(0x1C) + PI/PQ(0x80)；位/字节/字/双字
- SMART 并入本阶段（调研定论：走标准 S7comm，V 区=DB1，零 PLC 配置）：
  - 地址语法额外接受 `VB100`/`VW100`/`VD100`/`V100.3` → 自动映射 DB1
  - rack=0/slot=0（或 1）；单次读自动按 ≤200B 分片
- 连接参数：IP + rack + slot（内部换算 TSAP，公式见 §2）+ 型号下拉（VOC 需求，自动填默认）
- PDU 协商：请求 480（snap7 默认），采用 CPU 响应值
- 读写：单 Item 与多 Item（≤20）；超限自动分片
- 虚拟 S7 服务端（自测 + UI 演示 + 「第一分钟体验」：SMART 模式免配置连从站）
- UI：西门子页 + 连接向导（前置检查清单）+ 错误文案系统 + S7 报文解析面板

**不做**（S1 明确排除）：
- S7comm-Plus (0x72)（plain/integrity 已有开源逆向，但 PUT/GET 路线更稳，等社区趟平）
- 优化块符号寻址（SyntaxId 0xB2 变体，开源库均未实现）；用户引导建标准 DB
- 密码登录（S7-300/400 XOR 0x55 链式编码已有字节级资料，留 S2+）
- CPU 启停控制（Stop/HotStart 真实帧已拿到，实现成本低，可作 S1 可选项）
- PROFINET / MPI / PPI 串口（PPI 面向经典 S7-200，现场已被 PPI-以太网网关替代，P6）

## 2. 已定论的关键协议常量（实现直接采用）

### 2.1 连接层（COTP CR，22 字节模板）

```
03 00 00 16  11 E0 00 00 01 00 00  C0 01 0A  C1 02 <local_tsap>  C2 02 <remote_tsap>
```
- SrcRef=**0x0100**（S7 要求非 0）、DstRef=0x0000、Class 字节=**0x00**（非 RFC 的 0x40）
- TPDU-SIZE 参数 `0x0A`=1024（COTP 层，与 S7 层 PDU Length 是两回事，UI 不暴露）
- `RemoteTSAP = (ConnType<<8) + rack*0x20 + slot`；`LocalTSAP = 0x0100`
- ConnType：0x01=PG（默认）/0x02=OP/0x03..0x10=Basic（高级选项暴露给用户，连接被拒时可切换）

### 2.2 型号默认参数（连接向导用）

| 型号 | rack | slot | 说明 |
|---|---|---|---|
| S7-200 SMART | 0 | 0（1 亦可）| 零配置；V 区=DB1 |
| S7-300 | 0 | 2 | |
| S7-400 | 0 | 3 | 多机架会变 |
| S7-1200 | 0 | 1 | 需 TIA 开 PUT/GET |
| S7-1500 | 0 | 1 | 需 TIA 开 PUT/GET |

### 2.3 TransportSize 两套编码（最易错点）

- **请求侧**（Length=元素数）：0x01 BIT / 0x02 BYTE / 0x04 WORD / 0x06 DWORD / 0x08 REAL / 0x1C COUNTER / 0x1D TIMER
- **响应/写数据侧**：0x03(BIT)/0x04(B/W/DW)/0x05(INT) Length 单位 **bit**；0x06(DINT)/0x07(REAL)/0x09(OCTET) 单位 byte。解析时除 0x03/0x07/0x09 外 `Size >>= 3`
- 字节流读写统一用 snap7 风格 `TS=0x02 + 元素数`
- Timer/Counter 区 Address 字段直接填编号（不乘 8）

### 2.4 PDU 与分片

- 协商：请求 480，采用 min(响应, 请求)；AMQ 1/1
- Read 单 Item ≤ `(PDU-31)/elemSize`（保守值；理论 PDU-18）；Write ≤ `(PDU-28)/elemSize`；多 Item ≤20
- 数据按偶数字节填充（padding）

### 2.5 错误码两层（文案系统用）

- **Item Return Code**：0xFF 成功 / 0x03 权限 / 0x05 地址越界（或优化块）/ 0x06 类型不支持 / 0x07 类型不一致 / 0x0A 对象不存在（DB 未下载或优化块）
- **头级 Error Code**：0x0005 地址越界 / 0x0007 写长度不匹配 / 0x8104 功能不支持 / 0x8500 超 PDU / 0xD241 需密码 / 0xD602 密码错 / 0xD209 资源不存在

## 3. 模块划分（对齐三菱 MC 家族模式）

| 模块 | 职责 | 对应 MC 家族的 |
|------|------|---------------|
| `s7_address.rs` | 地址语法：`M0.0` `MW10` `DB1.DBW20` `IB0` `QD4` `C5` `T3` `PIW256` + **SMART `VB/VW/VD/V.n`→DB1 映射** → `S7AnyAddr{area, db, byte, bit}`；AnyPointer 地址 `(byte<<3)\|bit`（T/C 区直接编号） | mc_address.rs |
| `s7_pdu.rs` | Read(0x04)/Write(0x05) 作业与 Ack_Data 解析（**响应 TS bit 换算**）、多 Item、Setup(0xF0) 协商、分片计算、两层错误码表人话文案 | mc_pdu.rs |
| `s7_cotp.rs` | TPKT+COTP：CR/CC（§2.1 模板）、DT、TPKT 流式读帧（`03 00 LL LL`） | mc_frame.rs |
| `s7_slave.rs` | 虚拟 S7 服务端：CR→CC→Setup→Read/Write 全序列；`S7SlaveMemory`+seed_demo；CC 回显 TSAP/TPDU-SIZE | mc_slave.rs |
| session.rs | `Connection::S7Tcp{stream, pdu_size, pdu_ref}`；`open_s7_connection`（CR→CC→Setup）；`s7_transact`（PDU Ref 配对+分片游标） | McTcp |
| protocol.rs | `s7_parse_address` / `open_s7_connection` / `s7_read` / `s7_write` / `start_s7_slave` / `stop_s7_slave` / `s7_slave_set` / `close_s7_connection` | mc_* 命令族 |

**字节序纪律**：S7 全大端，与 MC（小端）、Modbus（字大端位组装）并列第 3 套上下文，模块隔离+「同值异序」三协议专项测试。

## 4. 测试策略（三层，golden 向量全部来自外部证据）

1. **单元/向量**：期望字节来自 deep-dive 报告摘录的真实 pcap + snap7 源码，禁止自推：
   - CR 模板（§2.1）与 CC 验证
   - Setup 请求/响应（真实值：请求 `F0 00 00 01 00 01 01 E0`→响应 PDU=`00 F0`）
   - Read/Write：BOOL/BYTE/WORD/DWORD/REAL/STRING 各 1 对 + 多 Item（wincc_s300 8-Item 读响应为教科书向量）+ DB/M/I/Q/T/C 各 1
   - 错误路径：0x05/0x0A/0x8500/0xD241 + 对应人话文案断言
2. **E2E（JSONL）**：`tests/s7_jsonl_e2e.rs` 复用 Sidecar 辅助类，≥14 例（含 SMART V 区语法、分片读 400B、多 Item、从站 set/get）
3. **交叉验证（杀手锏）**：**python-snap7 3.0.0 已纯 Python 化（2026-08，无 DLL 依赖）**，`pip install python-snap7` 即用：
   - python-snap7 client → 我方 rust 从站：connect(0,1) 握手 → read_area(DB/M/I/Q) 值断言 → write_area → 我方 `s7_slave_set` 断言落库 → 读 T/C
   - 我方 client → snap7 server（若 Python 侧可起；至少单向必须有）
   - 目标 ≥15 项，对齐 Modbus 的 pymodbus 对拍标准

## 5. UI（VOC 直接转化）

- 侧边栏「西门子 S7」入口；协议变体（本轮 2 项，均标注线缆）：
  - `S7comm (网线直连/交换机, S7-300/400/1200/1500)`
  - `S7-200 SMART (网线直连, 本体网口)` —— P0.5，同链路不同默认值+V 语法
- **连接卡片**：型号下拉（自动填 rack/slot，见 §2.2 表）+ IP + 高级折叠（ConnType/自定义 TSAP/超时 3000ms——不学 s7netplus 固定 20s 被骂 11 楼）
- **前置检查清单**（连接按钮旁链接，按型号显示）：1200/1500 五步（PUT/GET 勾选位置+保护等级+DB 非优化+重编译下载+固件版本）；300/400 三步；SMART 一行「无需任何设置」
- **地址框**：双语法（`DB1.DBW100`/`M10.3` + SMART `VW100`）；占位提示按型号轮换；实时预览字节跨度
- **错误文案映射**（§2.5 两层码 → 人话+动作）：TCP 连不上→网段/网线/PLCSIM 提示；COTP 拒绝→rack/slot 表；读写被拒→PUT/GET 步骤；0x05/0x0A→优化块/DB 不存在提示
- **数据表格**：类型默认 REAL（现场第 1 需求）；有符号切换+工程量缩放；STRING 按 2 字节头截取+GBK/UTF-8 选项（中文现场刚需，74 条 issue）
- **报文面板**：TPKT/COTP/S7 分层着色解析（ROSCTR/Function/两层错误码/Item 明细），连接后显示协商 PDU
- 位写读-改-写；批量写二次确认；连接状态周期探测（不依赖 TCP 缓存状态）

## 6. 里程碑

| # | 交付 | 验收 |
|---|------|------|
| S1-M1 | s7_address + s7_cotp + s7_pdu + 向量测试 | 单测 ≥30（全部外部 golden 字节） |
| S1-M2 | session + protocol + E2E | `s7_jsonl_e2e` ≥14 例全绿 |
| S1-M3 | s7_slave + python-snap7 对拍 | 对拍 ≥15 项；`pip install python-snap7` 可复现脚本入库 |
| S1-M4 | UI 西门子页（含 SMART 变体、连接向导、错误文案、报文解析） | headless 截图审查+全流程手测 |
| S1-M5（可选） | CPU 启停/状态（Stop/HotStart/SZL 0x0424 帧已齐） | 二次确认交互+E2E |

## 7. 风险与对策

| 风险 | 对策 |
|------|------|
| python-snap7 3.0 API 变化（纯 Python 重写后） | 对拍脚本先跑 `pip install python-snap7==3.0.0` 冒烟；失败则降级为其报文文档静态对拍 |
| 响应 TS bit 换算错位（D2/D3 同类错误重犯） | 解析函数单测覆盖 8-Item 教科书向量逐字节 |
| 优化块用户误报 bug | 0x05/0x0A → 「可能是优化块」提示（对齐 snap7 FAQ + TIA 步骤） |
| 与三菱/Modbus 并存状态污染 | 独立 `s7_` 前缀 + Connection 新变体 + 三协议「同值异序」测试 |
| SMART 固件差异（slot 0/1） | 连接向导默认 0/0；失败自动重试 0/1 并提示 |
