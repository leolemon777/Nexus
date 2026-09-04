# Nexus 2.0 — 工业通讯调试工作台

**一体化工业协议调试工具**：Modbus 全家 + 三菱 MC 12 变体 + 西门子多变体 + 欧姆龙 FINS/HostLink C-mode + Allen-Bradley EtherNet/IP/CIP explicit 首轮 + Beckhoff ADS/AMS 首轮 + Keyence KV Host Link 首轮 + LS Electric XGT FEnet 首轮 + Panasonic MEWTOCOL-COM 首轮 + Delta DVP/AS Modbus 地址 profile + 汇川 H3U/H5U Modbus 地址 profile + 信捷 XC/XD D 区 Modbus profile + FATEK FBs 原生 ASCII 首轮 + Fuji MICREX-SX SPH Loader Command TCP 只读首轮 + GE Series 90/PACSystems SRTP 首轮 + MQTT 3.1.1 TCP 只读订阅首轮 + IEC 60870-5-104 TCP 只读主站/总召首轮 + DNP3/TCP 只读 Master/Class 首轮 + DL/T 645-2007/1997 共享串口只读首轮 + 国产品牌映射 + 变频器 USS + S5 兼容 RK512。

![License](https://img.shields.io/badge/license-MIT-blue)
![Tests](https://img.shields.io/badge/tests-Electron%20278%20%7C%20JSONL%2089-brightgreen)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)

## 支持协议

| 厂商 | 协议 | 传输 | 状态 |
|------|------|------|------|
| **Modbus** | TCP / UDP / RTU / ASCII | 网口 / 232 / 485 | ✅ 主站+从站+轮询 |
| **三菱** | MC Binary 3E/4E / ASCII / UDP / A-1E / C24 串口 / FX Link / FX 编程口 | 网口 / 232 / 485 | ✅ 12 变体 + 虚拟从站 |
| **西门子** | S7comm (0x32) / Fetch-Write / PPI / Web API / Modbus TCP / USS / RK512 | 网口 / 232 / 485 | ✅ 10 通道 + CPU启停/SZL/密码 |
| **欧姆龙** | FINS TCP / UDP / HostLink C-mode RR / HostLink FINS 0101（只读首轮） | 网口 / 232 / 422 / 485 | ✅ FINS + HostLink 软件首轮 |
| **Allen-Bradley** | EtherNet/IP/CIP explicit（Register/Unregister、Read Tag 编解码） | TCP 44818（只读软件会话） | ✅ S2-S4a 软件证据；真实 PLC 会话/ListIdentity/L2 待验 |
| **Beckhoff** | ADS/AMS over TCP（Read/Write/ReadWrite、ReadDeviceInfo、ReadState 编解码） | TCP 48898（只读软件会话） | ✅ S2-S4a 软件证据；真实 AMS Route/PLC L2 待验 |
| **Keyence** | KV Host Link ASCII（CR/CR NN 握手、RDS 字/位 TCP 只读 + WRS/ST/RS 编解码） | TCP 8501（S4a 软件只读） | ✅ S2-S4a 软件证据；真实 KV 型号/L2 待验 |
| **LS Electric** | XGT FEnet（20 字节头、X/B/W/D/L 单变量/连续读写，TCP 只读） | TCP 2004（S4a 软件只读） | ✅ S2-S4a 软件证据；真实 XGT 型号/L2 待验 |
| **Panasonic** | MEWTOCOL-COM（RD/WD/RCS/WCS、XOR BCC） | 串口（共享 COM 只读） | ✅ S3/S4 软件会话；真实 FP 型号/电气层/L2 待验 |
| **Delta** | DVP/AS Modbus 地址 profile（地址、功能码、跨区分段只读） | 复用已打开的 Modbus RTU/ASCII COM（TCP/UDP 后续） | ✅ S4 软件读回边界；真实型号/固件/L2 待验 |
| **汇川** | H3U/H5U Modbus 地址 profile（位/字地址、八进制 X/Y、C200+ 双寄存器） | 复用 Modbus RTU/TCP | ✅ S2/S3 软件映射；AM/AC/Easy 和真实型号/L2 待验 |
| **信捷** | XC/XD/XL Modbus 地址 profile（首轮仅确认 D0..D65535） | 复用 Modbus RTU/TCP | ✅ S2/S3 D 区软件映射；其他区域、型号/固件/L2 待验 |
| **FATEK** | FBs 原生 ASCII（40/44/45/46/47、STX/ETX、加和校验） | TCP 5000（显式只读会话） | ✅ 软件 TCP 只读 + 编解码；真实 FBs/Gateway/L2 待验 |
| **Fuji** | MICREX-SX SPH Loader Command（20 字节头、00H/01H） | TCP 18245（显式只读会话） | ✅ 软件 TCP 只读 + 编解码；真实 SPH 型号/固件/Loader/L2 待验 |
| **GE** | Series 90 / PACSystems SRTP（56 字节会话、事务号、R/AI/AQ 与 I/Q/T/M/SA/SB/SC/S/G 数据码） | TCP 18245（显式只读会话） | ✅ 软件 TCP 只读 + 编解码；真实 GE 型号/固件/L2 待验 |
| **MQTT** | MQTT 3.1.1（CONNECT/CONNACK、SUBSCRIBE/SUBACK、QoS 0 PUBLISH 只读订阅、PING） | TCP 1883（显式只读订阅） | ✅ S2-S4b + Aedes 独立 Broker 互操作；生产 Broker/认证/TLS/Sparkplug/L2 待验 |
| **电力规约** | IEC 60870-5-104（I/S/U、STARTDT/STOPDT/TESTFR、站/组总召、五类监视 ASDU） | TCP 2404（只读 Client/Master） | ✅ S2-S4a + 独立脚本 Outstation；真实 RTU/IED、带时标、控制、L2 待验 |
| **电力规约** | DNP3（链路 CRC/Transport、完整性轮询、Class 0/1/2/3、静态/事件对象、IIN/Quality） | TCP 20000（只读 Master） | ✅ S2-S4a + 独立脚本 Outstation；生产栈互操作、真实 RTU/IED、控制/校时/认证/L2 待验 |
| **电能表** | DL/T 645-2007 / DL/T 645-1997（分版地址/控制码/DI、前导字节、地址反序、数据加减 33H、电量/日期/时间/状态首批解析） | 复用已打开的串口，默认提示 2400 8E1（只读） | ✅ S2-S4a 软件/独立表端向量；真实表计、电气层、厂家 DI、后续帧和 L2 待验 |
| **水气热表** | CJ/T 188-2004（单 68H 帧型、算术和、DI+SER、901F 流量/状态离线解析） | 离线构帧/解帧 | ✅ S1-S3 软件向量；真实表计、唤醒时序、后续帧和 L2 待验 |
| **楼宇自控** | BACnet/IP（定向 Who-Is/I-Am、ReadProperty/ComplexACK、Unsigned/Real） | UDP 47808（显式只读会话） | ✅ S1-S4b + bacstack 独立栈互操作；RPM、写入、COV、BBMD/FDR 和真实楼宇设备 L2 待验 |
| **楼宇自控** | KNXnet/IP Tunneling v1（Connect、GroupValueRead、Tunneling ACK/Response、Connection State、Disconnect） | UDP 3671（显式只读会话） | ✅ S1-S4b + knx 独立栈编解码互通；写组值、场景、Routing、Device Management、Secure 和真实 Interface/Router L2 待验 |
| **国产品牌** | 台达 DVP / 汇川 | 走 Modbus | ✅ 地址映射层 |
| **变频器** | USS (SINAMICS / MicroMaster) | 232 / 485 | ✅ 组帧/解帧 |
| **S5 兼容** | 3964R + RK512 | 232 / 485 | ✅ 组帧/解帧 |

Modbus 主/从站、串口调试、三菱、西门子、欧姆龙、Allen-Bradley、Beckhoff、Keyence、LS Electric、Delta、汇川、信捷、FATEK、Fuji、GE、MQTT、IEC104、DNP3、DL/T 645、CJ/T 188、BACnet/IP、KNXnet/IP 和 Panasonic 页面均内置“通讯设置帮助”：跟随当前协议变体显示 IP/端口或 COM/波特率/校验/站号、设备侧设置、电脑侧设置、连接检查和首次实机只读边界；当前注册表覆盖 53 个可选通讯变体。

## 快速开始

### 方式一：便携版(推荐)

下载 `Nexus-2.0-portable.zip`，解压后双击 `Nexus 2.0.exe`。免安装、免依赖、免管理员权限(改 IP 功能除外)。

### 方式二：开发模式

```bash
# 安装依赖
npm install

# 构建 Rust 核心
npm run build:rust-core

# 构建 Web 界面
npm run build

# 启动 Electron
npm run electron

# 打包便携版
npm run package:portable
```

## 架构

```
┌─────────────────────────────────────────────────┐
│                Electron (渲染层)                  │
│  3 列布局:导航 | 工作区 | 报文面板                  │
├─────────────────────────────────────────────────┤
│                Electron (主进程)                   │
│  IPC 白名单 · 串口服务 · 轮询调度 · Web API         │
├─────────────────────────────────────────────────┤
│              Rust Sidecar (JSONL stdio)           │
│  协议核心: Modbus · MC · S7 · FINS · ENIP/CIP · IEC104 · DNP3 · DLT645 │
│  帧层: RTU/ASCII/TPKT/COTP/FX/C24/PPI/USS/RK512/HostLink │
│  虚拟从站: Modbus · MC · S7 · FINS · PPI · FW     │
└─────────────────────────────────────────────────┘
```

## 演示与实机验证

```powershell
# 零硬件桌面闭环：MC 虚拟从站 → C# 对照/数据桥 → Nexus Modbus 回读
npm run rehearse:desktop

# 实机前置检查（必须明确 USB-485 串口）
node scripts/rehearse-hardware.cjs check --rs485-com COM4

# 默认实机全流程只读，不包含 PLC 写入
node scripts/rehearse-hardware.cjs all --rs485-com COM4
```

受控 PLC 写入不在默认流程中；只允许显式 D 区地址，并要求安全确认和原值恢复。完整边界见 `docs/spec-plan-desktop-demo-hardware-p0.md`。

## 测试

```bash
# Rust 全量(单元 + E2E + 交叉验证)
cd rust-core && cargo test

# Electron 单元
node --test electron/*.test.cjs

# R0 短时长稳工具验证（不是 1h/8h 验收）
npm run test:soak-harness

## 历史快照（早于 2026-08-23 测试基线修复）

# 交叉验证(需 Python)
pip install python-snap7 pymodbus
python tools/python_snap7_cross.py
```

> 以下为历史结果，不作为当前结论；当前基线见下方 2026-08-23 小节。

**当前回归结果**：Electron 全量 184/184 通过；Rust sidecar JSONL 当前实测 72/72 通过（IEC104 4/4、DNP3 4/4、DL/T 645 4/4）；MQTT Aedes/MQTT.js 独立 Broker 互操作 1/1 通过；Vite production build、`cargo fmt --check` 和 `npm audit`（0 漏洞）通过。DNP3 独立脚本 Outstation 覆盖链路 CRC、Transport/TCP 分片、完整性与 Class 扫描、静态/事件/时间/IIN、应用确认、自发响应和错序断开；DL/T 645 独立表端向量覆盖 2007/1997 分版请求、正常电量响应、表端异常、校验和与跨版本拒绝。完整 Rust 核心库测试当前为 423/426，三项失败仍来自既有 LS XGT 长度断言和 USS 应答位断言，不在 DL/T 645 改动范围；新增 DL/T 645 单元测试 9/9 通过。软件/独立向量证据不等于真实设备 L2。

## 当前回归结果（2026-08-23）

Electron 全量 278/278 通过（2026-08-23 复测；含三族路由收口、Modbus/MELSEC/Siemens Golden Vector、C24 只读状态，以及项目文件安全落盘、脱敏导出、v0→v1 黄金样本迁移、协议 ID/显示名分离、凭据 fail-closed、项目不可信输入、CSV/JSON 点表导入守卫、Electron 隔离/CSP、本地 SSE 回环绑定、Sidecar 命令/参数门禁、统一日志脱敏、双生态依赖与许可证清单、发布元数据、发布说明/回滚、构建证据与工具链预检回归）；固定 Rust sidecar JSONL 回归 89/89 通过，并额外执行 R0 soak harness 1/1；MQTT Aedes/MQTT.js、bacstack BACnet/IP、knx KNXnet/IP 三套独立互通各 1/1 通过；Vite production build、Electron 桌面冒烟、`cargo fmt --check`、`cargo check --all-targets`（0 warning）和 `npm audit`（0 漏洞）通过。完整 Rust 核心测试为 612/612 通过（单元 446/446 + integration 166/166；已修复既有 LS XGT 长度断言与 USS 应答位测试构造问题）。软件/独立栈/脚本对端证据不等于真实设备 L2，3 秒 soak 验证也不等于 1 小时/8 小时长稳。历史文档中的 Electron 216/253/257 为当时快照。

## 安全审查

两轮全面对抗性审查（代码正确性 + 并发/资源 + 安全面 + 前端状态），报告见 `docs/audit-*.md`。

## 技术文档

| 文档 | 内容 |
|------|------|
| `docs/西门子全协议设计文档.md` | 西门子 20 章字节级规范 |
| `docs/三菱全协议设计文档.md` | 三菱 MC 家族字节级规范 |
| `docs/协议路线图-v2.md` | 多品牌扩展路线 |
| `docs/research/` | 调研报告(snap7 交叉/开源对比/VOC/协议深挖) |
| `docs/audit-*.md` | 代码+安全审查报告 |
| `docs/PRODUCT_COMPLETION_ROADMAP.md` | 产品完成定义、发布门槛与后续批次 |
| `docs/spec-plan-serial-plot-parse-replay.md` | 串口可视化三件套专题批次：调试页曲线面板 / 自定义帧解析 / 会话录制回放 |
| `docs/r0-soak-runbook.md` | R0 软件长稳运行、判定与证据归档 |
| `docs/release-metadata-runbook.md` | Formal/Candidate 便携包 SBOM、SHA-256 清单与源提交元数据 |
| `docs/release-rollout-runbook.md` | 发布说明归档、回滚包配对校验和正式目录切换原则 |
| `docs/build-evidence-runbook.md` | 构建测试证据计划、脱敏日志、哈希与 candidate/formal 边界 |
| `docs/test-first-protocol-handoff.md` | 三大协议族测试交接、只读/受控写、禁止项 |
| `docs/modbus-golden-vectors.md` | Modbus 8×6 命令矩阵与 FC03 RTU 向量 |
| `docs/melsec-golden-vectors.md` | 三菱十变体、C24 只读与 3E D100 向量 |
| `docs/siemens-route-golden-vectors.md` | 西门子 fail-closed 路由边界 |
| `docs/dnp3-golden-vectors.md` | DNP3/TCP 只读 Master、CRC/Class/对象/确认向量与 L2 门禁 |
| `docs/dlt645-golden-vectors.md` | DL/T 645-2007/1997 只读串口、BCD/DI/33H/校验和向量与 L2 门禁 |

## License

MIT — 见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party dependencies.
