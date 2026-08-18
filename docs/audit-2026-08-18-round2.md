# Nexus 2.0 第二轮全面审查报告(2026-08-18)

> 范围:`rust-core/src/` 全部 36 文件、`electron/` 全部服务、`src/main.js`(4011 行)、`index.html` 西门子/FINS/接口体检区。
> 方法:逐文件人工通读(重点新增:ppi_frame / ppi_slave / uss_frame / rk512 / pn_dcp / s7_fetchwrite / s7_pdu / s7_slave / s7_address / session / protocol / fins_* / brand_profiles;electron:main.cjs / preload.cjs / rust-core-client.cjs / serial-service.cjs / s7-webapi-service.cjs / realtime-push / serial-debug / slave-serial-bridge / fx-serial),三重白名单用脚本做程序化比对,`cargo check` 通过(测试二进制在本审查环境无法链接:LNK1104 msvcrt.lib,属环境缺 Windows SDK 库路径,非代码问题)。
> 上一轮(2026-08-17)17 P0/30 P1 的修复全部复核到位(见文末"上轮修复复核")。

---

## P0 —— 现场必坏(1 项)

### P0-1 西门子页 PPI / Fetch-Write / Web API 三个"可用"协议选项是死的:选了仍走 S7comm,100% 连接失败
- **位置**:`index.html:547,551,554` + `src/main.js:2618-2645(s7Connect)`、`2801-2843(initSiemensUi)`
- **证据**:下拉框把 `ppi`、`fw`、`webapi` 标注为"(可用)"并列在 optgroup「串口/透传(可用)」「经典兼容(可用)」「现代通道(可用)」;`s7-webapi-user/pass` 输入框也已埋好(`index.html:621-622`)。但 `s7Connect` 无任何 variant 分流——恒调用 `callBackend("open_s7_connection", …)`;`grep -n "ppi\|fw_\|uss\|rk512\|s7web\|webapi" src/main.js` 零命中。`open_ppi_tcp` / `open_fw_tcp` / `s7web_connect` 在 preload(172 条)、main.cjs handler、rust-core-client COMMANDS 三层白名单全部畅通,唯独前端没接线。
- **触发**:现场工程师对 S7-200 串口服务器选 "PPI" 点连接 → 实际发 COTP/S7comm 到 102 口 → 必失败;选 "Fetch/Write"(应走 2000 口裸 TCP)同理;选 "Web API" 密码框根本不显示。
- **修复**:`s7Connect` 按 `#s7-variant` 分流——ppi→`open_ppi_tcp`(读写走 `ppi_read/ppi_write`,双拍由 session 层已实现);fw→`open_fw_tcp` + `fw_read/fw_write`;webapi→`s7web_connect` + `s7web_read/write/ping`,并切换显示 webapi 凭据输入框。未接线前先把三个 option 置 `disabled` 并去掉"(可用)"。

---

## P1 —— 特定条件触发(11 项)

### P1-1 恶意/异常 S7 从机的响应 item 数与请求不符 → `chunk[i]` 越界 panic,整个 rust-core 退出
- **位置**:`rust-core/src/protocol.rs:3976-3984(handle_s7_read)`
- **证据**:
  ```rust
  let sub_items: Vec<S7Item> = chunk.iter().map(|(_, it)| it.clone()).collect();
  match session.s7_read(&p.connection_id, &sub_items) {
      Ok(parts) => {
          for (i, part) in parts.iter().enumerate() {
              let (origin, sub) = &chunk[i];   // ← parts.len() 来自响应的 param[1],与 chunk.len() 无关联
  ```
  `parse_read_response`(s7_pdu.rs:259-290)按**响应声明的** item_count 构造结果。若对端(被入侵的 PLC / MITM / 故障网关)在响应 param 里声明 5 项、数据区放足 5×4 字节头,而本端 chunk 只有 3 项,则 `chunk[3]` 越界 panic。`handle_line` 在 sidecar 主循环执行,panic = 主线程退出 = 进程退出,所有连接/轮询/从站全灭(Electron 会拉起新 sidecar,但全部会话状态丢失)。
- **修复**:`for (i, part) in parts.iter().enumerate() { let Some((origin, sub)) = chunk.get(i) else { break }; … }`,或校验 `parts.len() == chunk.len()` 不符即报 `S7_RESPONSE_MISMATCH`。`handle_s7_write`(protocol.rs:4091-4094 的 `codes.extend(rcs)`)同样应校验 rcs 长度。

### P1-2 S7 虚拟从站:11 字节畸形 COTP CR(LI≤5)→ `cotp[7..end]` 切片 panic
- **位置**:`rust-core/src/s7_slave.rs:489-490(handle_s7_client)`;`s7_cotp.rs:207-224(unwrap_tpkt)` 不校验 LI
- **证据**:`let li = cotp[0] as usize; let params = &cotp[7..(li + 1).min(cotp.len())];`。前置检查只有 `cotp.len() >= 7` 与 `(cotp[1]&0xF0)∈{0xD0,0xE0}`。TPKT 帧 `03 00 00 0B 00 E0 00 00 00 00 00`(LI=0, CR,total=11=帧长,版本 0x03)通过全部校验后 `cotp[7..1]` 直接 panic("range start 7 > end 1")。端口扫描器/模糊测试一包即杀连接线程(默认 unwind 只杀该线程,但连接被咬死;若未来改 `panic=abort` 则整个 sidecar 死)。
- **修复**:`if li < 6 || li + 1 > cotp.len() { return; }`(CC/CR 可变参数区从第 7 字节起,LI 至少 6)。

### P1-3 PPI 虚拟从站:7 字节畸形帧 → `parse_sd2` 的 `body[1]/body[2]` panic
- **位置**:`rust-core/src/ppi_frame.rs:51-68(parse_sd2)`;经 `ppi_slave.rs:59` 可远程触达
- **证据**:`parse_sd2` 校验了 `frame.len() < 7`、`len < 4+le+2`、结束符与 FCS,但没校验 `le >= 3`。帧 `68 01 01 68 16 16 16`(LE=1,body=[0x16],FCS=0x16 ✓,尾 0x16 ✓)通过全部校验后执行 `Ok((body[0], body[1], …))` → `body[1]` 越界 panic。连接到 PPI 从站端口发 7 字节即触发。
- **修复**:`if le < 3 { return Err(…) }`(DA+SA+FC 最少 3 字节)。

### P1-4 PPI 虚拟从站:缓冲区永不重新同步 → 无界内存增长 + 线程滞留
- **位置**:`rust-core/src/ppi_slave.rs:44-101(ppi_serve)`、`104-113(find_sd2_end)`
- **证据**:`find_sd2_end` 首字节非 0x68 返回 None → `continue 'outer` → 只往 `pending` 里追加、永不丢弃。客户端先发 1 字节噪声(或 E5 残留)再持续发数据,`pending` 无上限增长(远程内存耗尽);且内层"等短帧确认"循环(71-88 行)只认 `0x10`/`0xE5` 开头,垃圾字节同样只积不丢,并且**不检查 `running`**——`stop_ppi_slave` 后,只要客户端保持连接,该线程永远卡在读循环里(200ms 超时自旋)。
- **修复**:① `find_sd2_end` 返回 None 且 `pending[0] != 0x68` 时丢弃首字节重新扫同步;② `pending` 设上限(如 8KB)超限断开;③ 内层循环每轮检查 `running`。

### P1-5 PPI 帧长 LE 为 u8,>234 字节的读写静默生成废帧(无预算检查)
- **位置**:`rust-core/src/ppi_frame.rs:30-42(build_sd2)`、`session.rs:1400-1426(ppi_read/ppi_write)`
- **证据**:`body_len as u8` 在 `body_len = 3 + s7_pdu.len() > 255` 时回绕。读响应 ≈ 21+N 字节,故 N>234 字节(S7-200 PPI 实际 PDU 上限恰 ~240)即产出 LE 错误的畸形帧。`ppi_read`/`ppi_write` 不做任何分片/预算(`s7_read` 路径有 `s7_chunk_items`,PPI 路径没有)。当前 UI 未接 PPI(P0-1),但 JSONL 三层白名单已可达,接线后立刻踩中。
- **修复**:`build_sd2` 对 `body_len > 255` 显式报错;`ppi_read/ppi_write` 按 200 字节预算分片(与 s7_read 同款 chunking)。

### P1-6 `process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0"`:进程级 TLS 校验降级,且与注释自相矛盾
- **位置**:`electron/main.cjs:551-553`
- **证据**:注释写"per-request agent 隔离,不动进程级 TLS 设置",下一行却设置进程级环境变量;`s7-webapi-service.cjs` 的 `fetch` 没有传任何 per-request TLS 选项。两种后果二选一:(a) 该变量对 Node/undici fetch 生效 → 主进程**所有** HTTPS 证书校验被关闭(PLC 凭据可被中间人截获,Web API 写值通道可被劫持),且变量在注册 IPC 时无条件设置,即使用户从未用 Web API;(b) 不生效(undici 不读该变量)→ Web API 对自签证书的真实 S7-1500 仍然 `certificate` 错误,功能永远不可用。无论哪种都违背注释意图。
- **修复**:删除该行;改用 undici `Agent` + `connect: { rejectUnauthorized: false }` 按请求传入(`fetch(url, { dispatcher: agent })`),或 Electron `net.request`(支持 `session.setCertificateVerifyProc` 钉扎)。若保留全局开关,至少仅在用户显式确认后设置。

### P1-7 SSE 实时推送 `Access-Control-Allow-Origin: *`:用户浏览器里的任意网页可静默订阅工业报文流
- **位置**:`electron/realtime-push-service.cjs:34-38(/events)`、`49-58(/status)`;`main.cjs:59-67` 把轮询数据与 `debug_frame`(含完整 TX/RX 原始字节)推入 SSE
- **证据**:服务绑 127.0.0.1 但响应头 ACAO `*`。EventSource 不受同源策略默认限制,配合 `*` 意味着用户打开的**任何**恶意网页都能 `new EventSource("http://127.0.0.1:8080/events")` 持续读取寄存器值与原始协议帧(数据外泄通道);`/status` 同理(可探测工控机是否在调试 PLC)。这不是 DNS rebinding,是更简单的直接跨源读。
- **修复**:校验 `Host` 头必须是 `127.0.0.1:port`;去掉 ACAO `*`(EventSource 同源使用无需 CORS,外部工具本就跑在本机/Node-RED);或要求一次性 token。

### P1-8 S7 虚拟从站不执行协商 PDU 上限:大请求导致响应长度字段与 TPKT 长度双回绕,回废帧
- **位置**:`rust-core/src/s7_slave.rs:293-366(handle_read)`、`343-347`
- **证据**:`handle_setup` 协商出 `SLAVE_PDU_LIMIT=480`,但 `handle_read`/`handle_write` 从不据此限制。第三方客户端(snap7/自写脚本)单 Item 读 20000 字节:内存 64KB 内成功 → `let (ts, len) = … (0x04u8, (data.len() as u16) * 8)` 在 data.len() ≥ 8192 时回绕(如 20000*8 mod 65536 = 33280 ≠ 160000),响应头声明长度错误;更大时 `wrap_dt` 的 `(4+3+len) as u16`(s7_cotp.rs:175)也回绕,帧界错位。python-snap7 走协商路径不受影响,故 e2e 未覆盖。
- **修复**:在 `handle_read/handle_write` 入口按 `item.data_bytes()` 总量与 480 预算比较,超限回 `0x8500`(与真机一致)。

### P1-9 渲染层点表轮询无重入保护:间隔 < 事务耗时 → 请求堆积风暴
- **位置**:`src/main.js:419-499(pollPointTableTick)`、`502-507(startPointPoll)`
- **证据**:`setInterval(pollPointTableTick, intervalMs)` 内是 `await` 批次循环,无 in-flight 标志。上轮修复的"轮询重入保护"只覆盖了 Electron `poll-scheduler` 与 Rust 流式轮询;这条渲染层定时器路径漏网。50ms 间隔 + 1s 超时的慢设备 → 每秒 20 个并发 `*_once` 调用排队(rust 单线程顺序处理,队列在 `_sendRequest` 的 pending Map 里无界增长),UI 卡顿、IPC 风暴、内存增长。
- **修复**:`let ticking=false; if (ticking) return; ticking=true; try{…}finally{ticking=false}`。

### P1-10 serial-service PPI framing 用"第一个 0x16"判帧尾:数据字节 0x16 即截断(当前为死代码,接线即咬人)
- **位置**:`electron/serial-service.cjs:148-150`
- **证据**:`const etx = frame.indexOf(0x16, 5)` — SD2 帧 FCS 前的 PDU 数据里出现 0x16(概率 1/256/字节,100 字节读≈33%)即提前成帧 → 解析失败;FCS 恰为 0x16 时同样误判。正确做法是按 `LE` 定长:`total = 4 + frame[1] + 2`。目前全仓无调用方(P0-1 的连带结果),属"埋雷"。
- **修复**:按 LE 计算总长;顺带核实 `frame.length === 1 && frame[0] === 0xE5` 应放宽为 `frame[0] === 0xE5`(容忍 E5 与后续数据同包到达)。

### P1-11 FINS 虚拟从站 TCP 线程泄漏:UDP 绑定失败时已 spawn 的 accept 线程无法停止
- **位置**:`rust-core/src/session.rs:1741-1762(start_fins_slave)`
- **证据**:先 bind TCP 并 `thread::spawn(fins_tcp_accept_loop)`,再 bind UDP;UDP 失败(端口被其他 UDP 服务占用)返回 Err,但 TCP 线程已带 listener 启动,`fins_slaves` 未登记 → 永远没有 `stop` 句柄,端口与线程泄漏至进程退出,且用户重试永远撞 TCP 端口占用。
- **修复**:UDP 先绑(或失败时置 `running=false` 并回滚 TCP 注册)。

---

## P2 —— 质量/防御性(17 项)

| # | 位置 | 问题与建议 |
|---|------|-----------|
| P2-1 | `src/main.js:342` + `3404-3410` | 扫描结果"选用"按钮以 HTML 字符串传给 `appendCells`(textContent)→ 渲染为字面文本 `<button…>`,功能失效。改为 createElement+textContent 构建。 |
| P2-2 | `src/main.js:2542-2548` + `index.html:566` | 型号选项 "S7-200 经 CP243-1 (TSAP 1000/1001)" 无实现:`S7_MODEL_DEFAULTS` 无 `s7200` 键(回退 1200 默认),`s7Connect` 也从不传 `localTsap/remoteTsap`(后端 protocol.rs:3832-3851 明明支持)。按型号填 TSAP 覆盖或先禁用该选项。 |
| P2-3 | `rust-core/src/fins_slave.rs:64` | seed 把 C0 放在 `tim_cnt[5000]` 并注释"C 编号偏移 5000",但 `parse_fins_address("C5")` 解析为地址 5 → 读到的是 T5 槽位;`C0` 读到 T0 的 100。演示数据自相矛盾(T/C 实为同一 bank 前后段)。改为按 FINS 规范 TIM 区 0x80 分段或 UI 说明修正。 |
| P2-4 | `rust-core/src/rk512.rs:1-14,101-139` | 注释称请求头 10B/应答头 10B,实际请求编码 9B(1+2+2+2+2)、应答 10B。代码自洽、注释误导;另 `build_rk512_write` 奇数长度数据不补偶字节(3964R 传输惯例补齐);`rk512_area_func` 无调用方且映射语义混乱(Q→0x06 写)。建议:注释改 9/10、写路径补偶、删死函数。 |
| P2-5 | `rust-core/src/s7_fetchwrite.rs:132-136,148-152` | 错误响应复用 `fw_error_response` 但 byte5 保留请求 OPC(0x05),Fetch 错误响应规范应为 0x06 形态;`bank()` 读 DB 时懒创建 64KB(读探测产生写副作用)。按手册核对错误帧 OPC;读路径不落库。 |
| P2-6 | `rust-core/src/uss_frame.rs:20,37-49` | `MIN_FRAME_LEN` 常量(10)无人使用且与 parse 的 ≥8 不一致;`build_uss_request` 对 `pzd.len()+5 > 255` 无检查(LGE u8 回绕)。加 LGE 上限校验。 |
| P2-7 | `rust-core/src/pn_dcp.rs`(整文件) | 模块在 lib.rs 声明但无任何 JSONL 命令接线(protocol.rs 无 dcp 命令),纯死代码;`build_identify_all` 的 ResponseDelay=0(规范建议 2-5 避免响应碰撞);`build_blink_led` 的 Control 块结构未按规范加 BlockInfo。接线前不算功能,建议补 `dcp_*` 命令或移除。 |
| P2-8 | `electron/serial-service.cjs:84-226,359-362` | framing `"uss"/"rk512"/"ppi"` 为无调用方的死代码;且 `transact` 的断言(359 行)对这些 framing 仍强制要求 `expectedResponseLength`(5..256),将来接线若忘传会直接抛"正常响应长度必须是…"。建议:白名单枚举 framing,非 RTU 分支跳过该断言。 |
| P2-9 | `electron/serial-debug-service.cjs:50-68` | `detach()` 用 `removeAllListeners("data")` 会同时杀掉 SlaveSerialBridge/响应收集器的监听;重复 `attach` 叠加监听导致重复帧。改为保存自身引用 `port.off("data", this._boundAccumulate)`。 |
| P2-10 | `electron/main.cjs:706-710` | `start_serial_slave` 不检查串口是否已打开(对比 fx_serial_transact:574 有检查):串口未开时返回 `{running:true}` 但桥接从未绑定,UI 假成功。 |
| P2-11 | `electron/main.cjs:374` | 轮询 streamId `poll-${Date.now()}` 毫秒级碰撞:同毫秒启动两个轮询互相覆盖(rust `poll_streams.insert` 同 key 覆盖,订阅表错乱)。加自增序列。 |
| P2-12 | `electron/rust-core-client.cjs:637-647` | `startPollStream` 先 `subscriptions.set` 再 `request`,请求失败时订阅表条目泄漏(直到 stop 被调用,而调用方拿不到 streamId 语义时永不清理)。失败分支应 `subscriptions.delete`。 |
| P2-13 | `rust-core/src/fins_address.rs:136` | `word * 16 + bit`:`parse_dec` 接受至 u32::MAX,`D4294967295.15` 在 debug 构建乘法溢出 panic、release 静默回绕成错地址。对 word 上限(≤ 0xFFFFF)钳制。 |
| P2-14 | `rust-core/src/session.rs:1414-1426(ppi_write)` | 无预算检查:大 `values` → `build_write_request` 的 `(data.len() as u16) * 8`(s7_pdu.rs:323-327)在 >8191B 时回绕 + P1-5 的 LE 回绕,双废。与 P1-5 一并修。 |
| P2-15 | `electron/s7-webapi-service.cjs:48-64` | `connect` 对 host 无任何格式校验(IP/主机名正则),渲染层被攻破即可让主进程向任意 URL POST 凭据(工具性质决定低危,建议校验 + 连接后锁定);`token.slice(0,8)` 回显前 8 字符属轻度泄露,建议改为长度或哈希指纹。 |
| P2-16 | `rust-core/src/s7_slave.rs:220-229,368-376` | `error_ack` 把中文错误消息塞进 S7 数据区(非协议字节,真机不会这样);`push_read_item` 解码失败用非标准 RC 0xD2(规范应为 0x03/0x0A)。对拍客户端可能困惑。 |
| P2-17 | `electron/main.cjs:737-796(scanBaudRate)` | 扫描期间反复 close/open 串口,与用户在主站页的操作存在竞态(无互斥);`serialService.current` 可能被扫描换成用户未预期的配置。加 `SERIAL_BUSY` 类互斥或 UI 锁。 |

---

## 核对通过项(明确"通过")

1. **三重白名单一致性:通过**。程序化比对:preload `allowedCommands` 172 条 = main.cjs 注册 handler 172 条(含 3 个 for-of 循环数组),零差异;preload 中所有 rust 透传命令(含新增 brand/fins/s7/fw/ppi/uss/rk512 全家)均在 rust-core-client `COMMANDS`(164 条)中;rust `dispatch` 多出的 `close_s7_connection` 为冗余别名无害。上一轮 A2 类问题未复发。
2. **命令注入:通过**。PowerShell(`main.cjs:176`)命令串为常量、`execFile` 数组参数;netsh(211-244)`execFile` 数组 + 网卡名黑名单 `/[&|<>^"]/` + IP/掩码/网关/DNS 全部走 `ipv4()` 正则后拼接。无 shell 串联面。
3. **前端 XSS 面:通过(附建议)**。动态数据全面走 `textContent`(S7/FINS/MC 结果表、趋势图例、接口体检表);17 处 `innerHTML` 逐一核对仅静态文案或不含用户输入的后端固定消息(`parse_hex_string` 错误只回显索引,frame_parser.rs:44-53)。`renderParseResult`(main.js:3175-3179)把后端 summary/error 直插 innerHTML 属危险模式,当前字符串均受控——建议仍统一 escape。
4. **DOM/内存上限:通过**。告警面板 500 行裁剪(3511-3513,上轮 C7 修复保持)、trace 5000 条(3436)、趋势每序列 300 点/60s 窗口(530-534)、调试日志环形 1000 条。
5. **协议字节级编码:通过**(golden 交叉验证充分)。S7comm:COTP CR 与 snap7 逐字节一致、TSAP 公式、AnyPointer 两套 TransportSize、读写奇偶填充、Stop/Start/SZL/密码 XOR 0x55 链式——测试向量含 WinCC 8-Item 真实抓包;PPI SD2 算术和 FCS 与 `68 1B 1B 68…8D 16` golden 一致;Fetch/Write 16B 头与 `53 35 10 01 03 05 …FF 02` golden 一致且纠正了写 OPC=0x03;USS LGE/BCC、PKE AK 编码正确;RK512 DLE 填充/BCC 正确;FINS 区码表/位线性地址正确;LLDP TLV 头解析正确。字节序无混用(S7/FINS 大端、MC 3E/4E 小端长度字段各处一致)。
6. **锁与并发:通过**。全部 `.lock().unwrap_or_else(|e| e.into_inner())`(grep 全仓仅测试内裸 unwrap);`close_connection` 级联清理轮询流(session.rs:759-779);lib.rs 主循环轮询流出错推送 `stream_error` 且 `streamEnd=true` 自动停流 + JS 侧订阅清理(rust-core-client.cjs:798-811);stdin 独立线程 + 50ms 超时轮询(上轮 C1 的轮询部分已缓解,事务级阻塞仍属已立项的架构问题)。
7. **从站生命周期:基本通过**。所有 `stop_*` 正确清 running 标志;accept 循环非阻塞+10ms 轮询;客户端线程 200ms 读超时检查 running;例外即本报告 P1-4/P1-11。
8. **sidecar 生命周期(上轮最佳模块):保持**。超时/崩溃恢复/优雅关停/行上限/协议校验均未回退;`s7_transact` 新增 PDU-Ref 回显配对校验(session.rs:1947-1958)是加分项。
9. **brand_profiles:通过**。表驱动 + 无把握段返回 MANUAL 不猜,边界(八进制 Y177、M1535、D1311)测试齐全。

## 上轮(2026-08-17)修复复核
A1(S7 命令循环)、A2(白名单 13 命令)、A3(fx-serial 响应结构)、A4(轮询字段名,main.cjs:386-392 现发 `address`/`fc`)、B1/B2(modbus_slave 越界)、B3(双 FC 帧)、C2(扫描超时,protocol.rs:5097+ 使用 timeout_ms)、C5(RTU 广播,serial-service awaitResponse)、C6(SSE error 监听,realtime-push:67)、C7(断开停轮询+告警上限)、S7 握手超时(fins_serve_tcp:222 2s;S7 走 read_tpkt_frame 5s 超时)、MC 从站 panic(body 长度预检,session.rs:2841-2852)、UDP MC-ASCII 一包杀伤(同上)、锁防御——**全部确认在位**。C1(事务移线程)如上轮结论仍为架构级遗留。

## 总体结论
- 上一轮修复质量高,未发现回退;新增代码的**协议编码层**(s7_pdu/s7_cotp/fins/ppi 帧构造)延续 golden 对照纪律,质量好。
- 本轮 1 个 P0:西门子页三个"可用"选项(PPI/Fetch-Write/Web API)前端未接线,后端三层白名单全部就绪、只差最后一步分流——现场选了必失败,属"功能上架未通电"。
- 值得优先处理的 P1 集中在两类:**远程可触发的 panic ×3**(protocol.rs:3977 / s7_slave.rs:490 / ppi_frame.rs:67,均为畸形报文一击)与 **TLS/SSE 两个安全面**(进程级关证书校验 + SSE 任意网页可订阅)。修复量都不大(合计约半天)。
- **建议**:修完 P0-1 与 P1-1/2/3/6/7 后,对 S7/PPI/FW 路径做一轮真机或仿真器联测(PPI 需补 >234 字节分片),再走 8 小时烤机。测试在本审查环境因 LNK1104 无法链接执行,上机前请在开发机完整跑一遍 `cargo test`(5 个集成测试文件覆盖 S7/MC/FINS e2e)。
