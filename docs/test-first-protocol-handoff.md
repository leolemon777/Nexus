# 测试优先协议交接（Modbus / MELSEC / Siemens）

日期：2026-08-23  
范围：Nexus-Rust 桌面产品马上要做的软件/现场只读测试。  
证据口径：软件、虚拟从站、脚本对端 **不等于** 真机 L2。

本轮软件联合门禁已实测（2026-08-23 夜）：Electron 278/278、Vite build、Electron smoke、JSONL 89/89、MQTT/BACnet/KNX 各 1/1、R0 soak harness 1/1、`cargo fmt --check`、完整 `cargo test` 612/612、`cargo check --all-targets`、`npm audit` 0 漏洞。下列步骤仍用于现场只读，不把上述结果写成 L2。

## 1. 当前支持矩阵

六态：`implemented` / `offline-codec` / `online-readonly` / `online-readwrite` / `hardware-pending` / `Blocked`。

注册表 `state` 描述软件能力；所有三族条目的 `evidence` 仍是 `software-and-virtual; L2 pending`，即硬件列一律 `hardware-pending`，除非另有真机记录。

| 族 | 变体 | 软件状态 | 硬件 | 备注 |
|---|---|---|---|---|
| Modbus | rtu / ascii / tcp / udp / rtu-over-tcp / ascii-over-tcp | online-readwrite | hardware-pending | UI 已按 8×6 命令矩阵分流；写需确认+回读+审计 |
| 三菱 | 3e / 4e / ascii-3e / ascii-4e / mc-udp-3e / mc-udp-4e / mc-1e | online-readwrite | hardware-pending | 未知变体不得回落 3E |
| 三菱 | mc-c24 | online-readonly | hardware-pending | 写按钮禁用；禁止 `fx_serial_transact("c24")` |
| 三菱 | fx-links / fx-prog | online-readwrite | hardware-pending | 共享 COM |
| 西门子 | s7comm / smart | online-readwrite | hardware-pending | SMART 是 S7comm 地址 profile |
| 西门子 | ppi | online-readwrite | hardware-pending | TCP 网关，不是原生 PPI 电缆 |
| 西门子 | ppi-serial / uss / rk512 | online-readonly | hardware-pending | 共享 COM；不得变 S7comm |
| 西门子 | fw | online-readwrite | hardware-pending | 默认端口 2000 |
| 西门子 | webapi | online-readwrite | hardware-pending | HTTPS；证书未钉扎 |
| 西门子 | S7comm-Plus / PROFINET / MPI | Blocked | — | UI 禁用，无路由 |

## 2. 测试步骤

### 2.1 软件回归（每次改三族代码后）

```text
npm run test:electron
npm run build
npm run smoke:electron
npm run test:rust-jsonl
cargo test --manifest-path rust-core/Cargo.toml
cargo fmt --manifest-path rust-core/Cargo.toml --all -- --check
npm audit --audit-level=moderate
```

专项：`electron/modbus-route.test.cjs`、`electron/melsec-route.test.cjs`、`electron/siemens-route.test.cjs` 及对应 golden-vector 测试。

### 2.2 Modbus 只读

1. 选传输并连接（串口先开 COM）。
2. FC01/02/03/04 各读一次；串口 FC01 不得发成 FC03；UDP 不得走 `tcp_*`。
3. 看异常码、CRC/LRC、超时。参考 `docs/modbus-golden-vectors.md`。

### 2.3 Modbus 受控写（非默认）

1. 非广播站号。
2. 确认框出现传输/地址/旧值/新值后才写。
3. 回读一致；`logs/write-audit.jsonl` 有记录。
4. 站号 0 必须被拒绝。

### 2.4 三菱只读

1. 3E/4E Binary、ASCII、UDP、A-1E 分别连接虚拟从站或实验室 PLC。
2. 地址：D/M 十进制，X/Y 八进制，B/W/ZR 十六进制。
3. C24：主站页开 COM → 选 `mc-c24` → 读；写按钮禁用。
4. 参考 `docs/melsec-golden-vectors.md`。CPU RUN/STOP/RESET 默认不要做。

### 2.5 西门子只读

1. S7comm/SMART 走 `open_s7_connection`；PPI TCP、PPI COM、FW、Web API、USS、RK512 走各自命令。
2. 切到 USS 再点连接，状态应是串口就绪，不能出现 S7 PDU 协商。
3. Fetch/Write 端口 2000。参考 `docs/siemens-route-golden-vectors.md`、`docs/fetchwrite-golden-vectors.md`、`docs/serial-golden-vectors.md`。

## 3. 只读命令 vs 受控写命令

| 族 | 只读 | 受控写 | 禁止默认 |
|---|---|---|---|
| Modbus | FC01–04 对应 `*_read_*` / `*_once` | FC05/06/15/16 + 确认 + 回读 | 广播写、无旧值盲写 |
| 三菱 | `mc_*_read`、`mc_c24_serial_read`、FX read | `mc_*_write`、FX write（需确认） | C24 写、CPU RUN/STOP/RESET |
| 西门子 | `s7_read` / `ppi_read` / `ppi_serial_read` / `fw_read` / `s7web_read` / `uss_serial_read` / `rk512_serial_read` | `s7_write` / `ppi_write` / `fw_write` / `s7web_write`（确认） | CPU 控制、USS/RK512/PPI COM 写 |

## 4. 预期结果与常见错误

| 现象 | 含义 |
|---|---|
| `CONNECTION_TYPE_MISMATCH` | UDP 会话误调了 `tcp_*` |
| `FX_BAD_PROTOCOL` | C24 被当成 FX 协议 |
| 未知西门子变体「不能走 S7comm」 | fail-closed 生效 |
| Modbus 异常 02 | 非法数据地址 |
| MC 结束码非 0 | 设备拒绝该软元件/权限 |
| S7 返回码不是 `0xFF` | 该项读取失败，不是整帧成功 |
| Web API 登录失败 | 用户/密码/证书/固件 Web 服务未开 |

## 5. 软件验证 vs 必须真机

软件已覆盖：命令存在性、UI 路由、编解码黄金帧、虚拟从站、写安全门禁、C24 写禁用。

必须真机才可升 L2：具体 CPU/模块型号、固件、接线、厂家工具同地址比对、抓包、断线/重连。未完成前不得写「现场 PASS」。

## 6. 备份 / 恢复

- 项目文件 `.nexus.json` 可保存连接参数和点表；打开只恢复配置，不自动连接或写入。
- 异常退出后 `workspace-recovery.json` 一次性只读恢复。
- 密码、令牌、私钥不得写入项目。脱敏导出可带走协议结构以便复现。

## 7. 禁止项

- 默认打开 S7/MC CPU 控制或未确认写入。
- 把模拟器、虚拟从站、脚本对端写成 L2。
- 项目或仓库内存放 PLC 密码、Web API 口令、证书私钥。
- `git reset --hard` 清掉未提交工作。
- 修改旧 C# 线 `E:\Desktop\Nexus2.0\Nexus`。
- 为本轮测试去改 OPC UA / IEC 61850 / CANopen / EtherCAT / PROFINET / 网关 / 插件 / formal 发布。
