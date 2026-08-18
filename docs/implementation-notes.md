# Implementation Notes — 阶段 1:Modbus 主站核心

> 对应 [phase-1-modbus-master-core.md](./phase-1-modbus-master-core.md) + [gap-analysis.md](./gap-analysis.md) 的 G15(广播写抑制)。
> 完成日期:2026-08-11。

## 交付概要

阶段 1 把 Modbus 主站从「FC03/FC04 一次性 RTU 串口读」推进到「全 FC(01–06、15、16)+ 6 传输(RTU/ASCII/TCP/UDP/RtuOverTcp/AsciiOverTcp 的编解码基础)+ 写操作 + 广播」。

**测试**:105 个测试全部通过(61 Rust lib + 10 JSONL 集成 + 34 Electron),0 失败。

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

2. **写操作无二次确认** —— 当前写操作直接执行,无 UI 确认弹窗。工业现场写操作有风险,阶段 2 应加确认对话框(对标 .NET Nexus 的 `WriteConfirmationService`)。

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
