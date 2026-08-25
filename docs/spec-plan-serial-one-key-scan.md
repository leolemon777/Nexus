# spec-plan —— Modbus RTU 一键扫描(站号 × 波特率 × 奇偶校验)与自动连接

> 状态:**P0 正确性修正已完成(2026-08-21，专项 10/10；全套 Electron 68/68)**。首次实现的“首站离线即跳档”会漏掉站号 2..247，
> 现已由《spec-plan-desktop-demo-hardware-p0.md》收口为每档完整遍历站号。实现相对本 spec 的架构调整:扫描编排不内联 main.cjs,
> 落在独立服务模块 `electron/modbus-scan-service.cjs`(依赖注入,可被 node --test require),见 implementation-notes.md 该批次条目。
> 动机:演示测试规划第 5(c) 幕 —— RS-485 温度模块现场"一键扫描",扫出参数后系统自动连接并读取实时温度,作为串口易用性的压轴演示。
> 现状盘点(2026-08-21 审计):
> - 已有 **站号扫描**:`scan_serial_stations`(electron/main.cjs:825,串口保持打开,逐站 FC03 探测 300ms 超时);
> - 已有 **波特率扫描**:`scanBaudRate`(main.cjs:759,close→按档 open→FC03 探测→命中恢复原配置);
> - 已有 **数值缩放**(倍率+单位,Modbus 点表 main.js:467),温度 ×0.1℃ 可直接用;
> - **缺**:奇偶校验档扫描、三维组合一键编排、命中后自动连接+自动读温度的动线。

## 1. 做什么

1. **`scan_all` IPC**(Electron 主进程,electron/main.cjs):
   - 参数:`{ portPath, stations:[1..16 可配], bauds:[勾选档位], parities:["8N1","8E1","8O1" 勾选], timeoutMs(默认 200), mode:"firstHit"|"full", onProgress }`
   - 编排:**外层 波特率×校验 档位,内层完整遍历站号**；不得用站号 1 无响应推断整档离线。
   - `firstHit`:任一站号应答即停,返回 `{baudRate, parity, dataBits, stopBits, station}`;`full`:完整矩阵,返回命中列表。
   - 复用 `SerialService.open(config)`(serial-service.cjs:257,config 已支持 baudRate/parity/dataBits/stopBits)与 `transact`(framing "rtu");探测帧复用 `build_read_holding_registers`(FC03 读 1 点)。串口服务**零改动**。
   - 进度推送:每档/每站回调渲染层(实时显示当前档位与耗时);全程可取消(`scan_all_cancel`)。
2. **UI**(Modbus 页扫描区扩展,src/main.js + index.html):
   - 现有"扫描站号/扫描波特率"旁新增 **"一键扫描"** 按钮 + 高级面板(站号范围、波特率勾选组、校验勾选组、超时)。
   - 结果沿用 `#scan-rows` 面板:新增"**选用并连接**"按钮 → 回填协议参数(Modbus RTU / 波特率 / 校验 / 站号)→ 自动打开连接 → 自动读保持寄存器 0..N(N=通道数,默认 8,高级面板可配)→ 结果表按倍率 0.1 + 单位 ℃ 显示 → 提示可一键开轮询(沿用现有轮询)。
3. **不做**:寄存器地址扫描(模块手册给出即可,列 P2+);非 Modbus 协议扫描;校验位/停止位任意组合(固定 1 停止位三档 8N1/8E1/8O1,覆盖 99% 现场模块)。

## 2. 时长预算(演示节奏硬约束)

- 每探测超时 200ms;单档位站号全扫(16 站)≈ 3.2s + 开关串口开销 ≈ 4s。
- 最坏全矩阵 6 波特率 × 3 校验 = 18 档 ≈ 72s(实际模块常在 9600 8N1 前 1~2 档内命中,< 10s)。
- UI 必须显示"当前档位 x/18 · 已用 t s · 预计剩余 t' s",演示时口播有数字可讲。

## 3. 验收

1. **无硬件自测**(开发机):com0com 虚拟串口对(若驱动签名受阻,用两台真机或 USB 串口对)→ Nexus 串口从站(Modbus RTU,slave-serial-bridge 已有)故意配成 19200 8E1 → 一键扫描应 firstHit 命中并自动连接读回从站 seed 数据。
2. 真机:温度模块(参数未知)→ 一键扫描 → 自动连接 → 实时温度跳动(倍率 0.1)。
3. 取消:扫描中途点停止,串口恢复扫描前配置(照抄 scanBaudRate 的保存/恢复逻辑)。
4. 回归:现有 scanStations/scanBaudRate 按钮行为不变;`npm run test:electron` 全绿;新增 scanAll 单测(档位编排顺序、firstHit 短路、取消恢复,用 stub SerialService)。

## 4. 接口与文件清单

| 文件 | 改动 |
|---|---|
| electron/main.cjs | `scanAll`(复用 scanBaudRate 模式)+ IPC `nexus:scan_all` / `nexus:scan_all_cancel` |
| electron/preload.cjs | 白名单 +2 |
| src/main.js | 一键扫描 UI 逻辑 + 命中后自动连接/读温动线 |
| index.html | 按钮 + 高级面板(默认折叠) |
| electron/*.test.cjs | scanAll 编排单测(stub) |

## 5. 风险

- R1 com0com 在 Win11 需测试签名(开发期),不影响演示(演示用真模块);
- R2 快速 close→open 循环个别 CH340 固件偶发打不开 → 档间加 50ms 间隔 + 单档重试 1 次;
- R3 扫描期间用户操作串口 → scanAll 持有 `transactionActive` 语义锁(同现有 transact busy 机制),冲突报 SERIAL_BUSY。
