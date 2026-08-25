# R0 软件长稳运行手册

## 目的与边界

`scripts/soak-r0.cjs` 用当前 Rust Core 启动内置 Modbus TCP 虚拟从站，再通过同一 sidecar 的 `start_poll_stream` 建立真实 TCP 轮询流。它验证的是 R0 软件稳定性：轮询数据、请求延迟、进程资源、句柄增长和清理路径。

这个工具不代表真实设备 L2，也不包含拔线、断电、丢包、延迟或乱序故障注入。1 小时和 8 小时证据必须分别完整执行，不能用短时 harness 或 3 秒验证替代。

## 运行前

先构建并验证普通 sidecar：

```powershell
npm run test:rust-jsonl
```

该命令会构建 `rust-core/target/debug/nexus-rust-core.exe`，并执行 2 秒 soak harness。若只改了 Rust Core，也可以先运行：

```powershell
npm run build:rust-core
```

## 快速验证

```powershell
$env:NEXUS_RUST_CORE_PATH = (Resolve-Path rust-core\target\debug\nexus-rust-core.exe).Path
node scripts\soak-r0.cjs --duration 3s --interval-ms 50ms --sample-interval-ms 500ms --progress-interval-ms 1s --output evidence\r0\soak-validation-3s.json
```

当前 3 秒验证证据：`evidence/r0/soak-validation-3s.json`。它只证明工具和产品轮询路径可运行，不满足 Soak-A/Soak-B。

## Soak-A / Soak-B

```powershell
# 1 小时快速泄漏观察
npm run soak:r0 -- --duration 1h --interval-ms 100ms --sample-interval-ms 5s --progress-interval-ms 30s

# 8 小时单班连续轮询
npm run soak:r0 -- --duration 8h --interval-ms 100ms --sample-interval-ms 30s --progress-interval-ms 5m
```

默认输出到 `evidence/r0/soak-<UTC-start-time>.json`。如需指定文件：

```powershell
npm run soak:r0 -- --duration 1h --output evidence\r0\soak-20260823-1h.json
```

## 主要参数

| 参数 | 默认 | 说明 |
|---|---:|---|
| `--duration` | `1h` | 总时长，支持 `ms/s/m/h` |
| `--interval-ms` | `100ms` | Modbus 轮询间隔；工具下限 20ms |
| `--sample-interval-ms` | `5s` | PowerShell 采样 sidecar RSS/CPU/句柄/线程的间隔 |
| `--progress-interval-ms` | `30s` | 控制台进度输出间隔 |
| `--max-rss-growth-mb` | `128` | 首末采样 RSS 增长上限 |
| `--max-handle-growth` | `100` | 首末采样句柄增长上限 |
| `--max-transport-latency-ms` | `2000` | 单次推送 transport latency 上限 |
| `--min-success-ratio` | `0.75` | 按配置周期折算的最小成功次数比例 |
| `--output` | `evidence/r0/soak-....json` | JSON 证据文件；`-` 表示不写文件 |

默认成功比例是 75% 而不是 100%：Rust sidecar 的轮询调度与同步事务耗时会计入周期。例如 50ms 配置在当前本机上呈现约 60ms 实际周期，这是调度/事务开销，不等同于未处理请求堆积。静默检测、数据错误、流错误、清理失败和进程退出仍是独立 fail-closed 条件。

## 判定项

JSON 证据中的 `checks` 必须全部为 true：

- 无运行失败；
- 成功次数达到配置时长和比例折算下限；
- 无寄存器值错误；
- 无 poll stream 错误；
- 无超过 3 个轮询间隔（至少 1 秒）的数据静默；
- 最大 transport latency 不超过阈值；
- RSS 增长和句柄增长不超过阈值；
- 流、连接、虚拟从站、sidecar 均清理成功；
- sidecar 退出码为 0。

## 记录字段

每次运行记录 UTC 起止时间、包版本、源码提交、工作区是否 dirty、Rust Core SHA-256、负载配置、成功/错误计数、延迟分位数、进程资源采样和清理状态。证据文件不得包含密码、令牌或生产数据。
