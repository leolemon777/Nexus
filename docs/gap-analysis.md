# Modbus 全覆盖缺口分析

> 对照 **Modbus Poll/Slave 官方功能**(竞品基准)、**用户之声(VOC)痛点**(论坛/社区反馈)、**.NET Nexus 成熟实现**(内部参考),逐项审查现有 spec-plan 的覆盖情况,找出遗漏的功能。
>
> 本文档是 [spec-plan.md](./spec-plan.md) 的补全清单,识别出的缺口会回填到对应阶段文档。

---

## 数据来源

### 竞品基准:Modbus Poll / Modbus Slave(官方功能清单)
- 来源:[modbustools.com](https://www.modbustools.com/modbus_poll.html) / [modbustools.com/modbus_slave.html](https://www.modbustools.com/modbus_slave.html) / [poll_display_formats.html](https://www.modbustools.com/poll_display_formats.html)

### VOC:用户痛点(论坛/社区)
- [control.com — Modbus 不可浏览的寄存器映射](https://control.com/forums/threads/modbus-protocol-and-solving-issues-using-it.44833/)
- [CSDN — Modbus 通讯故障排查指南](https://bbs.csdn.net/weixin_29189363/article/details/100252585)
- [知乎 — Modbus 用户常见问题](https://zhuanlan.zhihu.com/p/1908102545206907025)
- [Industrial Monitor Direct — 32位浮点字节序静默损坏](https://industrialmonitordirect.com/zh-hans/blogs/knowledgebase/modbus-32-bit-float-byte-order-resolving-word-swap-variations)
- [modbus.cn — 8合1协议工具评测](https://www.modbus.cn/42572.html)
- [Siemens Forum — RTU 慢响应/总线负载](https://support.industry.siemens.com/forum/ma/en/post/777981/)
- [Ignition Forum — 超时配置不匹配](https://forum.inductiveautomation.com/t/modbus-tcp-communication-issues-in-ignition/85218)
- [OpenEMS — 硬件一致性导致通信失败](https://community.openems.io/t/modbus-rtu-communication-failed/2392)
- [Universal Robots — Wireshark 抓包调试](https://forum.universal-robots.com/t/modbus-debugging/654)

### 内部参考:.NET Nexus
- `E:\Desktop\Nexus2.0\Nexus\src\Nexus.Modbus\`(6 传输,11+2 FC,4 字节序)

---

## 🔴 高优先级缺口(必须补)

### G1. 28 种显示格式(当前仅 7 种数据类型)

**竞品**:Modbus Poll/Slave 支持 **28 种显示格式**,含:
- 有符号/无符号整数:8/16/32/64 位
- 浮点:32 位(4 种字节序)、64 位(4 种字节序)
- 二进制、十六进制、八进制
- ENRON/DANIEL 32 位浮点(石油天然气行业变体)

**当前计划**:[phase-2](./phase-2-modbus-master-advanced.md) 的 `value_codec.rs` 只列了 7 种类型(UInt16/Int16/UInt32/Int32/Float32/Float64/String)+ 4 字节序。

**缺口**:
- ❌ 8 位整数(UInt8/Int8)— 需从 16 位寄存器中拆高低字节
- ❌ 64 位整数(UInt64/Int64)的字节序变体(只列了 ABCD/DCBA/BADC/CDAB,但 64 位有 8 种排列)
- ❌ 二进制视图(寄存器的 16 个位单独显示)
- ❌ 十六进制/八进制原始显示
- ❌ ENRON/DANIEL 模式(见 G2)
- ❌ 位域视图(对标 Modbus Poll 的位级编辑)

**补全方案**:扩展 `value_codec.rs` 的 `DataType` 枚举到完整 28 种,`display-type` 下拉分组显示。

#### Modbus Poll 官方 28 种显示格式(权威清单,来源:[poll_display_formats.html](https://www.modbustools.com/poll_display_formats.html))

规律:**4 种原生 16 位 + 6 类 × 4 字节序 = 28**。

| # | 格式名称(原文) | 位宽 | 字节序 | 占用寄存器 |
|---|---|---|---|---|
| 1 | Signed | 16-bit | — | 1 |
| 2 | Unsigned | 16-bit | — | 1 |
| 3 | Hex | 16-bit | — | 1 |
| 4 | Binary | 16-bit | — | 1 |
| 5 | 32 Bit signed Big-endian | 32-bit | ABCD | 2 |
| 6 | 32 Bit signed Little-endian | 32-bit | DCBA | 2 |
| 7 | 32 Bit signed Big-endian byte swap | 32-bit | BADC | 2 |
| 8 | 32 Bit signed Little-endian byte swap | 32-bit | CDAB | 2 |
| 9 | 32 Bit unsigned Big-endian | 32-bit | ABCD | 2 |
| 10 | 32 Bit unsigned Little-endian | 32-bit | DCBA | 2 |
| 11 | 32 Bit unsigned Big-endian byte swap | 32-bit | BADC | 2 |
| 12 | 32 Bit unsigned Little-endian byte swap | 32-bit | CDAB | 2 |
| 13 | 64 Bit signed Big-endian | 64-bit | ABCD EFGH | 4 |
| 14 | 64 Bit signed Little-endian | 64-bit | HGFEDCBA | 4 |
| 15 | 64 Bit signed Big-endian byte swap | 64-bit | BADC FEHG | 4 |
| 16 | 64 Bit signed Little-endian byte swap | 64-bit | DCBA HGFE | 4 |
| 17 | 64 Bit unsigned Big-endian | 64-bit | ABCD EFGH | 4 |
| 18 | 64 Bit unsigned Little-endian | 64-bit | HGFEDCBA | 4 |
| 19 | 64 Bit unsigned Big-endian byte swap | 64-bit | BADC FEHG | 4 |
| 20 | 64 Bit unsigned Little-endian byte swap | 64-bit | DCBA HGFE | 4 |
| 21 | Float Big-endian | 32-bit | ABCD | 2 |
| 22 | Float Little-endian | 32-bit | DCBA | 2 |
| 23 | Float Big-endian byte swap | 32-bit | BADC | 2 |
| 24 | Float Little-endian byte swap | 32-bit | CDAB | 2 |
| 25 | Double Big-endian | 64-bit | ABCD EFGH | 4 |
| 26 | Double Little-endian | 64-bit | HGFEDCBA | 4 |
| 27 | Double Big-endian byte swap | 64-bit | BADC FEHG | 4 |
| 28 | Double Little-endian byte swap | 64-bit | DCBA HGFE | 4 |

**字节序语义**(关键):
- `Big-endian` = ABCD(字序大端 + 字内大端)— 最常见,PLC 默认
- `Little-endian` = DCBA(字序小端 + 字内小端)
- `Big-endian byte swap` = BADC(字序大端 + 字内交换)— 即"Word Swap"
- `Little-endian byte swap` = CDAB(字序小端 + 字内交换)— 常见于施耐德/西门子部分设备

> ⚠️ **VOC 第一大隐性痛点**(来源:[Industrial Monitor Direct](https://industrialmonitordirect.com/zh-hans/blogs/knowledgebase/modbus-32-bit-float-byte-order-resolving-word-swap-variations)):"最常见的集成缺陷是主站与从站字节序不匹配导致 32 位浮点值**静默损坏** —— 不报错,只是数值完全不对(如温度显示为荒谬值)。"

**Nexus 额外加分项**(超越 Modbus Poll):
- **字符串** ASCII/UTF8/UTF16(Modbus Poll 28 种里没有,但工业现场需要)
- **8 位整数** Int8/UInt8(从 16 位寄存器拆高低字节)
- **位域视图**(16 位逐位显示/编辑,线圈级操作)
- **ENRON/DANIEL 浮点**(见 G2)

所以 Nexus 的 `DataType` 枚举应是 **28(对标)+ 6(加分)= 34 种**。

---

### G2. ENRON/DANIEL 模式

**竞品**:Modbus Poll 明确支持 "ENRON/DANIEL Mode"。

**是什么**:石油天然气行业(Daniel 流量计)的 Modbus 变体:
- 使用 **32 位寄存器**(一个寄存器存一个 float,而非标准的两个 16 位寄存器拼)
- 使用 **6 位地址**(000000–999999,统一地址空间,不按功能区区分)

**当前计划**:完全未提及。

**补全方案**:阶段 1 的地址解析器加 Enron 模式开关;`value_codec.rs` 加 Enron float 解码。**优先级中等**(垂直行业,但 Modbus Poll 有就说明有需求)。

---

### G3. 地址基(0-based vs 1-based)可切换

**竞品**:Modbus Poll/Slave 有 "Adjustable Address Base (0 or 1)"。

**VOC 痛点**:[CSDN] 和 [知乎] 反复提到 **"PLC 地址 40001 与协议地址(0-based)的偏移关系,现场工程师极易搞混"**。这是排名第一的地址映射痛点。

**当前计划**:[phase-1](./phase-1-modbus-master-core.md) 提到支持 5 位前缀(`40001` → 内部 -1),但没有全局的 0/1 基切换。

**缺口**:
- ❌ 全局地址基切换开关(UI 一键切 0-based / 1-based)
- ❌ UI 显示时明确标注当前地址基
- ❌ 5 位前缀(`0xxxx`/`1xxxx`/`3xxxx`/`4xxxx`)与 6 位前缀(`000000`–`999999`,Enron)的区分

**补全方案**:阶段 1 的 UI 加地址基单选;地址解析器同时支持两种基。

---

### G4. 缩放(Scaling)— 工程值转换

**竞品**:Modbus Poll 有 "Scaling" 功能 + "Conditional color"(条件着色)。

**VOC 痛点**:寄存器原始值(如 `0x012C = 300`)需要缩放到工程值(如 `30.0°C`,比例 0.1,偏移 0)。这是工业现场的**标配需求**。

**当前计划**:完全未提及缩放。

**缺口**:
- ❌ 比例(scale factor)+ 偏移(offset)配置:`工程值 = 原始值 × 比例 + 偏移`
- ❌ 单位标注(°C / bar / rpm / kW)
- ❌ 条件着色(值超阈值变红/绿,对标 Modbus Poll)
- ❌ 实时趋势图(对标 Modbus Poll 的 "Real time Charting")

**补全方案**:
- 阶段 2 的 `value_codec.rs` 加 `scale` + `offset` 参数
- 阶段 6 加实时趋势图(Canvas 绘制)
- UI 点位表格加比例/偏移/单位/颜色列

---

### G5. 寄存器映射表 / 点表管理(对标"不可浏览"痛点)

**VOC 痛点(最大痛点)**:[control.com] **"Modbus data is not browsable — you cannot auto-discover a device's register map. Users must manually consult datasheets for register addresses, data types, and scaling factors."**

这是 Modbus 协议的本质缺陷:设备不告诉你它有哪些寄存器。用户只能手动查手册、手动建点表。

**当前计划**:UI 有 `添加点位`/`批量导入`/`保存点表` 按钮但都是 disabled,阶段文档未详细规划。

**缺口**:
- ❌ 点表编辑器(地址 / 名称 / 数据类型 / 字节序 / 比例 / 偏移 / 单位)
- ❌ 批量导入(CSV / JSON / 从设备手册粘贴)
- ❌ 点表保存/加载(JSON 配置文件,对标 HSL 的 XML 持久化)
- ❌ **寄存器自动发现**(扫描一段地址范围,用不同数据类型试探,识别"活跃"寄存器)— 这是**差异化功能**,Modbus Poll 没有

**补全方案**:
- 阶段 2 加点表编辑器
- 阶段 2 加批量导入/导出(CSV)
- 阶段 6 加"智能寄存器发现"模式(扫描 + 变化检测,识别哪些寄存器在变)

---

### G6. OLE/Excel 自动化 → 换成现代方案

**竞品**:Modbus Poll 有 "OLE Automation for easy interface to Excel using VBA" + "Data logging direct to Excel"。

**当前计划**:[phase-6](./phase-6-polish-extras.md) 有 CSV/JSON/SQLite 导出,但没有实时 Excel 联动。

**缺口**:VBA/OLE 是 Windows 90 年代技术。现代替代:
- ❌ **CSV 实时流**(其他工具可 tail -f 监控)
- ❌ **WebSocket 实时推送**(浏览器/Node-RED 可订阅)
- ❌ **OPC UA 网关**(把 Modbus 寄存器暴露为 OPC UA 节点)— 这是工业 4.0 的集成需求
- ✅ SQLite 日志(阶段 6 已有)

**补全方案**:阶段 6 加 WebSocket 实时推送;OPC UA 网关标注为"后续协议包"。

---

## 🟡 中优先级缺口(应该补)

### G7. RTS 切换控制(RS-485 转换器)

**竞品**:Modbus Poll 有 "Easy control of RS-485 converters with RTS toggle"。

**为什么重要**:RS-485 半双工模式下,发送时拉高 RTS、接收时拉低 RTS 是关键时序。很多 USB-RS485 转换器依赖软件控制 RTS。

**当前计划**:[phase-1](./phase-1-modbus-master-core.md) 有 `rts_mode: preserve/high/low`,但没有"发送时自动切换"模式。

**缺口**:
- ❌ `rts_mode: "auto-toggle"`(发送前拉高,发送后拉低)
- ❌ RTS 切换延迟配置(有些转换器需要 ms 级延迟)

**补全方案**:阶段 1 的 `serial_config.rs` 加 `auto-toggle` 模式;`serial-service.cjs` 的 `transact()` 在 write 前后切换 RTS。

---

### G8. 多窗口/多会话(同时监控多个从站或数据区)

**竞品**:Modbus Poll 用 MDI(多文档界面),"monitor several Modbus slaves and/or data areas at the same time"。Modbus Slave 可"模拟最多 100 个从站,每个一个窗口"。

**当前计划**:单窗口单会话。

**缺口**:
- ❌ 同时连接多个从站(TCP 多连接 / 串口多站号轮询)
- ❌ 多个数据区标签页(同时看线圈 + 保持寄存器 + 输入寄存器)
- ❌ 从站模拟多实例(一个端口区段内多个虚拟从站)

**补全方案**:
- 阶段 2 支持多轮询流(多个 `start_poll_stream` 并行)
- 阶段 3 的 `SlaveServer` 支持多实例(不同端口)
- UI 阶段 6 加"标签页"模式(多个数据区并排)

---

### G9. Test Center(自定义报文发送)

**竞品**:Modbus Poll 有 "Test Center" 允许"compose and send your own test strings"并查看 hex 结果。

**当前计划**:[phase-4](./phase-4-serial-debug.md) 的串口调试覆盖了原始字节发送,但缺少**结构化**的自定义报文构建。

**缺口**:
- ❌ 可视化 PDU 构建器(选 FC → 填地址/数量/值 → 自动生成 hex)→ 对标 HSL 的"报文生成器"
- ❌ 报文模板保存(常用报文存为快捷按钮)
- ❌ 批量报文序列(按顺序发送多条,记录每条响应)

**补全方案**:阶段 4 的串口调试加"报文构建器"模式(结构化输入 → hex 输出)。

---

### G10. FC07 / FC11 / FC12 / FC17(诊断类功能码)

**竞品**:Modbus Poll/Slave 支持这些。
- **FC07** Read Exception Status — 读取 8 位异常状态(紧凑型遗留诊断)
- **FC11** Get Comm Event Counter — 返回状态字 + 事件计数
- **FC12** Get Comm Event Log — 返回总线事件日志
- **FC17** Report Slave ID — 返回设备类型和状态

**当前计划**:[phase-6](./phase-6-polish-extras.md) 列了 FC08/22/23/43,但**遗漏了 FC07/11/12/17**。

**缺口**:这 4 个都是串行线专用(serial-line only)的诊断功能码,.NET Nexus 也没实现。但 Modbus Poll 支持,说明现场有需求(尤其是 FC17 Report Slave ID 用于设备识别)。

**补全方案**:阶段 6 补齐 FC07/11/12/17 的 build/parse。

---

### G11. 6 位地址支持

**竞品**:Modbus Poll/Slave 支持 "6 digit addresses"(Enron 模式用)。

**当前计划**:未提及。

**补全方案**:与 G2(Enron 模式)一起实现。

---

### G12. 超时配置助手(VOC 高频痛点)

**VOC 痛点**:[Ignition Forum] **"bridge timeout must be less than the device timeout, or communication becomes very slow"** — 超时配置不匹配是高频问题。[Siemens Forum] 也反映慢响应由总线负载和轮询间隔不当导致。

**当前计划**:有超时参数,但没有配置助手。

**缺口**:
- ❌ 超时推荐计算器(根据波特率 + 帧长度,自动推荐最小超时)
- ❌ 轮询周期 vs 超时冲突检测(UI 警告"轮询间隔 100ms 小于单次事务耗时 150ms")
- ❌ RS-485 总线负载率计算器(设备数 × 单次事务时间 / 轮询周期)

**补全方案**:阶段 2 加超时/负载率计算器(纯 UI,提示性)。

---

## 🟢 低优先级缺口(可以补)

### G13. 打印 / 打印预览

**竞品**:Modbus Poll 有 "Print and print preview"。

**评估**:桌面打印功能在 2026 年价值低。替代:PDF 导出(阶段 6 数据导出已覆盖)。

**决策**:不补。

---

### G14. 上下文敏感帮助

**竞品**:Modbus Poll 有 "Context sensitive help"。

**评估**:有价值但工作量大。阶段 6 可加简单的 tooltip + 错误码速查表。

---

### G15. 广播写确认抑制

**竞品**:Modbus Poll 支持广播(slave ID 0)。

**VOC 痛点**:广播写不返回响应,主站不应等待响应。当前 [phase-1](./phase-1-modbus-master-core.md) 提到"写操作支持广播"但未明确响应处理。

**补全方案**:阶段 1 明确 — 广播写(unit 0)时,Rust `build_write_*` 返回 `expectResponse: false`,Electron 发送后不等待响应,直接返回成功。

---

### G16. 寄存器变化高亮(VOC 隐性需求)

**VOC 痛点**:轮询时值变化如果不明显,用户难以察觉。Modbus Poll 用"Conditional color"解决。

**当前计划**:[phase-2](./phase-2-modbus-master-advanced.md) 提到"高频轮询时开启闪烁高亮提示值变化"。

**补全方案**:阶段 2 加变化高亮(值变化时单元格闪绿/红,持续 500ms)+ 变化日志(记录每次变化的时间戳/旧值/新值)。

---

## 缺口汇总表

| ID | 缺口 | 优先级 | 目标阶段 | 竞品/VOC 来源 |
|---|---|---|---|---|
| G1 | 28 种显示格式(当前仅 7 种) | 🔴 高 | 阶段 2 | Modbus Poll 官方 |
| G2 | ENRON/DANIEL 模式 | 🔴 高 | 阶段 1/2 | Modbus Poll 官方 |
| G3 | 地址基 0/1 切换 | 🔴 高 | 阶段 1 | VOC 第一大痛点 |
| G4 | 缩放 + 单位 + 条件着色 + 趋势图 | 🔴 高 | 阶段 2/6 | Modbus Poll + VOC |
| G5 | 点表编辑器 + 批量导入 + 寄存器发现 | 🔴 高 | 阶段 2/6 | VOC 最大痛点 |
| G6 | 实时数据推送(WebSocket) | 🔴 高 | 阶段 6 | Modbus Poll Excel 联动的现代替代 |
| G7 | RTS 自动切换(RS-485) | 🟡 中 | 阶段 1 | Modbus Poll 官方 |
| G8 | 多窗口/多会话/多数据区 | 🟡 中 | 阶段 2/3/6 | Modbus Poll MDI |
| G9 | Test Center(报文构建器) | 🟡 中 | 阶段 4 | Modbus Poll Test Center |
| G10 | FC07/11/12/17 诊断类功能码 | 🟡 中 | 阶段 6 | Modbus Poll 官方 |
| G11 | 6 位地址支持 | 🟡 中 | 阶段 1/2 | Modbus Poll(Enron) |
| G12 | 超时/负载率配置助手 | 🟡 中 | 阶段 2 | VOC 高频痛点 |
| G13 | 打印/PDF 导出 | 🟢 低 | 阶段 6 | Modbus Poll(PDF 替代) |
| G14 | 上下文敏感帮助 | 🟢 低 | 阶段 6 | Modbus Poll |
| G15 | 广播写确认抑制 | 🟢 低 | 阶段 1 | 协议规范 |
| G16 | 寄存器变化高亮 + 变化日志 | 🟢 低 | 阶段 2 | VOC 隐性需求 |

---

## 修订后的完整功能码覆盖表

| FC | 名称 | .NET Nexus | Modbus Poll | 现计划 | 修订后 |
|---|---|---|---|---|---|
| 01 | 读线圈 | ✅ | ✅ | 阶段1 | 阶段1 |
| 02 | 读离散输入 | ✅ | ✅ | 阶段1 | 阶段1 |
| 03 | 读保持寄存器 | ✅ | ✅ | ✅ | ✅ |
| 04 | 读输入寄存器 | ✅ | ✅ | ✅ | ✅ |
| 05 | 写单线圈 | ✅ | ✅ | 阶段1 | 阶段1 |
| 06 | 写单寄存器 | ✅ | ✅ | 阶段1 | 阶段1 |
| **07** | **读异常状态** | ❌ | ✅ | ❌ | **阶段6(G10)** |
| 08 | 诊断 | ✅(TCP) | ✅ | 阶段6 | 阶段6 |
| **11** | **获取通信事件计数** | ❌ | ✅ | ❌ | **阶段6(G10)** |
| **12** | **获取通信事件日志** | ❌ | ✅ | ❌ | **阶段6(G10)** |
| 15 | 写多线圈 | ✅ | ✅ | 阶段1 | 阶段1 |
| 16 | 写多寄存器 | ✅ | ✅ | 阶段1 | 阶段1 |
| **17** | **报告从站 ID** | ❌ | ✅ | ❌ | **阶段6(G10)** |
| 22 | 屏蔽写寄存器 | ✅ | ✅ | 阶段6 | 阶段6 |
| 23 | 读写多寄存器(原子) | ✅ | ✅ | 阶段6 | 阶段6 |
| 43/14 | 读设备标识 | ✅(TCP) | ✅ | 阶段6 | 阶段6 |

**修订后**:从 11+2 个 FC 扩展到 **15+2 个 FC**(补 FC07/11/12/17)。

---

## 修订后的完整传输方式表

| 传输 | .NET Nexus | Modbus Poll | 现计划 | 修订后 |
|---|---|---|---|---|
| RTU 串口 | ✅ | ✅ | ✅ | ✅ |
| ASCII 串口 | ✅ | ✅ | 阶段1 | 阶段1 |
| TCP | ✅ | ✅ | 阶段1 | 阶段1 |
| UDP | ✅ | ✅ | 阶段1 | 阶段1 |
| RtuOverTcp | ✅ | ✅ | 阶段1 | 阶段1 |
| AsciiOverTcp | ✅ | ✅ | ❌ | **阶段1(G-new)** |
| **RtuOverUdp** | ❌ | ✅ | ❌ | **阶段1(G-new)** |
| **AsciiOverUdp** | ❌ | ✅ | ❌ | **阶段1(G-new)** |

**新发现**:Modbus Poll 支持 **8 种传输**(含 RtuOverUdp / AsciiOverUdp),.NET Nexus 只 6 种。补齐到 8 种。

---

## 修订后的数据显示格式表(对标 Modbus Poll 28 种)

| 类别 | 格式 | 现计划 | 修订后 |
|---|---|---|---|
| **16 位有符号** | Int16 | ✅ | ✅ |
| **16 位无符号** | UInt16 | ✅ | ✅ |
| **32 位有符号** | Int32(4 字节序) | ✅ | ✅ |
| **32 位无符号** | UInt32(4 字节序) | ✅ | ✅ |
| **32 位浮点** | Float32(4 字节序) | ✅ | ✅ |
| **64 位有符号** | Int64 | ✅ | ✅(补 8 种字节序) |
| **64 位无符号** | UInt64 | ✅ | ✅(补 8 种字节序) |
| **64 位浮点** | Float64 | ✅ | ✅(补 8 种字节序) |
| **8 位有符号** | Int8(高低字节拆) | ❌ | **G1 补** |
| **8 位无符号** | UInt8(高低字节拆) | ❌ | **G1 补** |
| **二进制** | Binary(16 位展开) | ❌ | **G1 补** |
| **十六进制** | Hex(原始) | ❌ | **G1 补** |
| **ENRON 浮点** | Enron Float32 | ❌ | **G2 补** |
| **字符串** | String(ASCII/UTF8/UTF16) | ✅ | ✅ |
| **位域** | Bit field(逐位显示/编辑) | ❌ | **G1 补** |

---

## 下一步

以上缺口已识别。执行时需要:
1. 把 G1-G6(高优先级)回填到阶段 2/6 文档的具体章节
2. 把 G7-G12(中优先级)回填到阶段 1/2/4/6
3. 把 G15(广播写)补到阶段 1
4. 更新 spec-plan.md 主索引的差距矩阵

**建议**:先执行阶段 1(主站核心),在阶段 1 中顺带补 G2/G3/G7/G11/G15(都是阶段 1 的地址/传输/配置相关)。阶段 2 集中补 G1/G4/G5/G12(数据显示和点表)。阶段 6 补 G6/G8/G9/G10/G13/G14。
