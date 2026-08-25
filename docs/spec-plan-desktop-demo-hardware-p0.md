# spec-plan —— 四对象纯桌面演示与桌面实机验证 P0 修正

> 状态：四对象软件 P0 已完成并验证（2026-08-22）；FX3U、S7-200 SMART 与 485 真机验收待硬件。
> 目标：按四个独立对象管理证据：FX3U、S7-200 SMART、上位机（当前按 DemoDashboard 口径）、RS-485 温度模块。先交付可重复的纯桌面演示闭环，再在同一台电脑上完成两台 PLC 的只读首连和温度模块验证。
> 原则：桌面模拟证据与真机证据分开；真机首次连接默认不可写；脚本失败必须返回非零；硬件缺席不写入真机 PASS 记录。

## 1. 本轮范围

1. 修复 Modbus RTU 一键扫描：每一个“波特率 × 校验”档位都必须遍历用户选择的全部站号，`firstHit` 在任意站号首次应答后停止，`full` 收集全矩阵命中。
2. 重构硬件彩排脚本：
   - `check`：帧级确认 ADP，并核对指定 USB-485 串口；未就绪返回非零。
   - `adp-read`：只读 D100/D101 与 M0~M7，不写 PLC。
   - `adp-write`：仅允许 D 寄存器，要求显式安全确认；保存原值、写读核对、恢复原值并再次核对。
   - `preset`：保留 SC09 预置能力，但要求 `--sc09-com` 和显式写入确认，不进入默认 `all`。
   - `scan`：使用 `--rs485-com`；多串口时不得猜测。
   - `all`：只执行 `check → adp-read → scan`，任一步失败立即停止，不包含任何写操作。
   - `watch`：只有 ADP 帧级应答正常且指定 USB-485 串口同时存在时才触发安全 `all`。
3. 新增纯桌面闭环自检脚本：启动 Nexus 虚拟 MC 从站，运行 C# 黄金向量与 E2E，再启动 C# 数据桥并通过 Nexus Rust Core 读回 Modbus 镜像值。
4. 将演示文档拆成“纯桌面演示”和“桌面实机验证”两条执行线，修正测试计数和过期状态。
5. 重新构建并生成包含当前功能的正式便携版，验证启动与构建信息。
6. 纳入 S7-200 SMART：
   - 纯桌面自检启动 Nexus 虚拟 S7 CPU，通过真实 COTP + S7comm 握手读取 `MW0`，并验证 SMART `VW100` 语法。
   - 实机新增 `smart-read`，必须显式指定 `--smart-host`、`--smart-address` 和 Micro/WIN SMART 当前监视值 `--smart-expect-hex`；默认尝试 rack 0 / slot 0，失败再试 slot 1；只做协议握手与读取。
   - 新增 `all-field`：`check → adp-read → smart-read → scan`，任一步失败即停止，全程不写 PLC。
   - 修正 UI 中“SMART 无需 PLC 侧设置”的过期提示：V3 的 PUT/GET Server 默认关闭，须在 STEP 7-Micro/WIN SMART V3 通讯设置中启用并下载；V2.8 及更早型号按对应软件/手册确认。

## 2. 明确不做

- 不实现 E2 轮询统计、E3 配置预设。
- 不实现传统 MC 3E/4E 语义变体。
- 不修改温度模块寄存器含义；在手册到位前，只把 FC03 响应或 Modbus 异常响应作为“站号在线”证据。
- 不设置电脑网卡、不关闭 TUN、不连接或写入现场设备；这些由实机测试时按清单执行。
- 不自动猜测 SMART IP、CPU 版本或读取地址；CPU 型号/订货号、固件、实际 IP 和安全只读地址由 STEP 7-Micro/WIN SMART 现场确认。
- 本轮不增加 SMART 自动写入命令。首次真机只读 PASS 后，如确需写回环，另行确认 PLC 程序、写保护范围和保留 V 区后再立边界。

## 3. 安全边界

- `adp-read` 和默认 `all` 不调用任何 MC 写命令。
- `adp-write` 仅接受 `D0..D7999`，明确禁止 D8000+ 特殊寄存器，并要求 `--confirm-safe-write YES`。
- 写入测试必须恢复原值；恢复失败判定整步 FAIL。
- 未确认 PLC 程序和输出隔离前，不写 M/X/Y/S/T/C。
- 自动回填 `implementation-notes.md` 只记录 PASS；SKIP、未命中、异常和代理假连通只输出到终端。
- `smart-read` 的 PASS 必须同时满足 TCP 102、COTP、S7 PDU 协商、目标地址返回成功，并与 Micro/WIN SMART 当前监视值一致；单独 TCP connect 或 ping 不算 PLC 通讯成功。
- SMART V3 启用 PUT/GET 时，优先同时启用通信写限制，仅给演示保留 V 区；首轮脚本仍不发送写命令。

## 4. 验收标准

1. 扫描单测覆盖：真实站号为 5/16 时，前置站号离线仍可命中；`full` 跨多个档位收集；取消恢复串口状态。
2. `npm run test:electron` 全绿；Rust Core 测试全绿；Node 语法检查通过。
3. 无硬件环境：`rehearse-hardware.cjs check` 明确返回非零且不修改实机记录。
4. 纯桌面脚本完成：黄金向量、MC 3E/A-1E E2E、S7-200 SMART 虚拟 CPU 读取、C# 数据桥、Modbus 回读全部 PASS。
5. 最新便携版包含 SMART V3 提示/slot 回退、“一键扫描”和网络 Ping，`smoke:portable` 通过，`BUILD-INFO.txt` 记录本次时间与 Rust Core 哈希。

## 5. 最终命令口径

```powershell
# 纯桌面演示前自检（零硬件）
node scripts/rehearse-desktop.cjs all

# 实机：只读安全全流程
node scripts/rehearse-hardware.cjs all --rs485-com COM4

# S7-200 SMART：先在 Micro/WIN SMART 确认实际 IP、版本和已知只读地址
node scripts/rehearse-hardware.cjs smart-read --smart-host 192.168.1.30 --smart-address VW100 --smart-expect-hex 1234

# 三个硬件通讯对象一次执行（两台 PLC + 485 温度模块）；上位机证据仍由 rehearse:desktop 单独给出
node scripts/rehearse-hardware.cjs all-field --rs485-com COM4 --smart-host 192.168.1.30 --smart-address VW100 --smart-expect-hex 1234

# 实机：用户确认 D300 为安全测试寄存器后，单独受控写入
node scripts/rehearse-hardware.cjs adp-write --address D300 --value 0x5A5A --confirm-safe-write YES

# 可选：不用 GX Works2 预置 D100 时，明确指定 SC09
node scripts/rehearse-hardware.cjs preset --sc09-com COM3 --value 0x1234 --confirm-safe-write YES
```

## 6. 软件侧验证结果

- Modbus 扫描专项：10/10；覆盖站号 5、16 前置站离线仍可命中。
- Electron：68/68；Vite production build 通过。
- Rust Core：459/459；Release build 通过（现有 20 个编译 warning，未在本 P0 扩展清理范围）。
- C#：Release build 0 warning / 0 error；7 项黄金向量与 MC E2E 通过。
- 纯桌面闭环：虚拟 SMART COTP + S7 PDU 480B，MW0=0x1234、VW100=0x5678；C# bridge 10 轮/0 失败，Nexus Modbus 回读 0x1234/0xABCD/0x0555 通过。
- 便携版：`output/portable/Nexus 2.0/`，355.76 MiB，`smoke:portable` 通过；`PackagedAtUtc=2026-08-22T04:10:47.9372386Z`，打包 Core SHA256=`FFFDD7AA6DAA0AAFC1B5DCD43E76435B0BDB412940E52E2D9B83AD22891A37EA`。
- 无硬件负路径：`all --rs485-com COM999`、未确认 `adp-write`、未指定串口的 `watch`、缺少 SMART 参数的 `smart-read` 均 exit 2；SMART 关闭端口会依次尝试 slot 0/1 后 exit 1；未生成真机 PASS。

## 7. 四对象验收矩阵（2026-08-22 增补）

| 对象 | 配置/裁判软件 | Nexus 通道 | 首次真机门槛 | 写入边界 |
|---|---|---|---|---|
| 三菱 FX3U + ENET-ADP | GX Works2 + SC09 | A-1E，TCP 5000 | D/M 只读与已知 D100 对值 | 单独 `adp-write`，仅安全 D 区，恢复原值 |
| 西门子 S7-200 SMART | STEP 7-Micro/WIN SMART（版本匹配 CPU） | S7comm，TCP 102 | COTP + PDU 协商 + 显式地址只读 + 监视值对账；slot 0/1 兼容尝试 | 本轮无自动写入；V3 建议限制到保留 V 区 |
| 上位机 | 当前按 `DemoDashboard` | MC 客户端 + Modbus TCP 502 服务端 | `rehearse:desktop` 的 C# bridge 和 Nexus 回读 | 仅桌面模拟/虚拟数据，不冒充真机 |
| 485 温度模块 | 厂家手册/模块参数 | Modbus RTU | 全矩阵命中 + 明确 FC/寄存器/倍率读取 | 首轮扫描与读取，不改站号/波特率 |

> 若“上位机”指的不是 `DemoDashboard`，而是另一台物理上位机或另一套软件，只替换本表第三行；两台 PLC 与温度模块的测试证据不受影响。
