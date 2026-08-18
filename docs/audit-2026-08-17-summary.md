# Nexus 2.0 全面代码审查汇总报告（2026-08-17）

> 四路对抗性审查：① Rust Modbus 协议核心 ② Rust 会话/并发 ③ Electron 中间层 ④ 前端状态/资源。
> 每条问题均有 file:line 证据（详见各审查组报告全文，存于本次会话记录）。
> 结论：**当前构建不可上现场**；修复 P0+关键 P1 后需 8 小时烤机验证。

## P0 汇总（17 项，现场必坏）

### A. 已发包必坏（上一轮 zip 里就有的）
| # | 位置 | 问题 |
|---|------|------|
| A1 | `electron/main.cjs:410` | S7 命令循环引用了 MC 循环的 `mcCmd` 变量（作用域错误）→ **打包版 S7 全部 8 个命令 100% 报错**（我的 UI 检查脚本自建了 handler 环境，绕过了这个 bug——教训：检查脚本必须走真实 main.cjs） |
| A2 | `electron/rust-core-client.cjs` COMMANDS | 白名单缺 13 个命令（tcp_mask_write_register 等 8 个高级 FC + start_serial_slave 等 5 个串口从站）→ **高级功能码与串口从站页全部 UNKNOWN_COMMAND** |
| A3 | `electron/fx-serial-service.cjs:53-129` | 响应结构假设错误（`build.ok` 恒 undefined）+ `[...rx]` 对非可迭代对象 spread → **FX 串口/MC-C24 在线事务必炸** |
| A4 | `src/main.js:2819` | 轮询推送字段名不匹配（后端发 `address`，前端读 `startAddress`）→ **连续轮询数据显示全部错乱为 NaN** |

### B. 协议正确性（数据错误）
| # | 位置 | 问题 |
|---|------|------|
| B1 | `modbus_slave.rs:337-356` | FC15 畸形 quantity 越界 panic + 锁中毒级联 → 14 字节报文远程瘫痪整个虚拟从站 |
| B2 | `modbus_slave.rs:438-441` | FC23 读越界返回钳制到 65535 的伪造数据（报成功）——「静默给错数据」 |
| B3 | `protocol.rs:938` | build_read_coils/discrete_inputs 产出双 FC 帧（读错地址 256 倍偏移） |

### C. 长时运行/现场工况
| # | 位置 | 问题 |
|---|------|------|
| C1 | `session.rs:19` + 主循环单线程 | 慢/半开设备一次事务最长阻塞 50s（5s 超时 × 10 重试），期间整个 sidecar 冻结 |
| C2 | `protocol.rs:4410` | 扫描站号的 timeout_ms 参数从未使用 → 247 空站最坏假死 20 分钟 |
| C3 | `lib.rs:64` + `close_connection` | 轮询流错误静默吞掉 + 断开连接不清理轮询流 → 死流空转、UI 无感 |
| C4 | `session.rs:2022/2099` | Modbus/MC 从站 handle 线程未 `set_nonblocking(false)`（S7 已修，同类遗漏）→ Windows 上每客户端 100% 自旋吃满一核 |
| C5 | `electron/modbus-master-service.cjs:386` | RTU 广播写（站号 0）假成功：帧根本没发出去却报成功 |
| C6 | `electron/realtime-push-service.cjs:31` | SSE 8080 端口被占用时无 error 监听 → 主进程直接崩溃闪退 |
| C7 | `src/main.js:3105` | 异常面板 DOM 无上限 + 断开不停轮询 → 掉线时每 500ms 一行，8 小时数万 DOM 节点 + IPC 风暴 |

## P1 关键项（部分）
- 串口拔出（现场最高频故障）：`serial-service.cjs` 不监听 `close` 事件 → 锁死到超时；RTS auto-toggle 异常不复位
- S7 从站握手无超时 → 端口扫描器造成线程泄漏
- MC 从站：1E 帧 10/11 字节 panic；MC-ASCII 切片 panic（UDP 路径一包永久杀死从站 UDP 服务）
- 轮询调度无重入保护（间隔 < 事务耗时 → 错误风暴）
- FC08 诊断 sub-function 1 字节编码违反规范（对真实设备不可用）
- RTU-over-TCP 高级 FC 帧长推断错误 → 流永久错位
- 全部 `.lock().unwrap()` 无 poisoned 防御；扫描计数 u8 溢出等

## 核对通过项
CRC16/LRC 全路径正确；字节序 8 种排列手推正确；MBAP 正确；sidecar 生命周期管理（超时/崩溃恢复/优雅关停）质量最高；安全配置（sandbox/contextIsolation/导航白名单）良好；趋势图/调试日志有界。

## 行动
本轮修复：A1-A4、B1-B3、C2、C4、C5、C6、C7（断开停轮询+告警上限）、S7 握手超时、MC 从站 panic、轮询重入保护、锁防御等。C1（架构级：事务移线程）单独立项。修完全量回归 + 重新打包。
