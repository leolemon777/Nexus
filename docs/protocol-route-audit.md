# Nexus 2.0 协议路由审计记录

日期：2026-08-23  
范围：M0 事实审计、M1 西门子变体路由收口、M2 PPI/USS/RK512 原生串口只读首轮、OMRON HostLink C-mode 只读首轮、M3 Allen-Bradley EtherNet/IP/CIP explicit TCP 只读软件边界、M4 Beckhoff ADS/AMS TCP 只读软件边界、M5 Keyence KV Host Link ASCII TCP 只读软件边界、M6 LS Electric XGT FEnet TCP 只读软件边界、M7 Panasonic MEWTOCOL-COM 离线编解码首轮、M8 Panasonic 共享 COM 只读会话软件边界、M9 Delta DVP/AS Modbus 地址 profile 首轮、M10 Delta profile 跨区拆分与共享 Modbus 只读读回、M11 汇川 H3U/H5U Modbus 地址 profile 与桌面路由首轮、M12 信捷 XC/XD D 区 Modbus profile 首轮、M13 FATEK FBs 原生 ASCII TCP 只读软件边界、M14 Fuji MICREX-SX SPH Loader Command TCP 只读软件边界、M15 GE Series 90/PACSystems SRTP TCP 只读软件边界、M16 MQTT 3.1.1 TCP 只读订阅/编解码与 Aedes 独立 Broker 互操作软件边界、M17 IEC 60870-5-104 TCP 只读 Client/Master 总召软件边界、M18 DNP3/TCP 只读 Master 完整性/Class 扫描软件边界、M19 DL/T 645-2007/1997 共享串口只读软件边界、M20 CJ/T 188-2004 离线只读编解码软件边界、M21 BACnet/IP Who-Is/I-Am 离线只读编解码软件边界、M22 BACnet/IP ReadProperty 离线请求/ComplexACK 编解码软件边界、M23 BACnet/IP 定向 Who-Is + ReadProperty UDP 独立脚本对端软件闭环、M24 KNXnet/IP Tunneling v1 离线只读编解码软件边界、M25 KNXnet/IP Tunneling v1 Connect/GroupValueRead/Disconnect UDP 独立脚本网关软件闭环、M26 KNXnet/IP Tunneling v1 Connection State 保活与显式重连软件闭环、M27 KNXnet/IP Tunneling v1 周期自动保活与无自动重连副作用回归、M28 KNXnet/IP Tunneling v1 knx 独立栈互通软件边界
目标仓库：Nexus-Rust

## 2026-08-23 测试优先协议补全（当前）

在既有 M1–M28 之上，本轮把马上要测的三族软件路由收口，并写出 `docs/test-first-protocol-handoff.md`：

- 西门子：`s7Connect` 按 `route.kind` 分流；未知/空变体 fail-closed，不得回落 `open_s7_connection`；USS/RK512/PPI COM 复用共享 COM 只读。
- Modbus：8×6 命令矩阵；UDP 走 `udp_*`；串口 FC01/02 走 `read_coils_once` / `read_discrete_inputs_once`。
- 三菱：十变体独立路由；C24 写禁用；`fx_serial_transact("c24")` 返回 `FX_BAD_PROTOCOL`。
- 复测：Electron 278/278、JSONL 89/89、完整 Rust 612/612、Vite build、Electron smoke、`cargo fmt --check`、`npm audit` 0 漏洞。软件通过不等于 L2。

下文 M1 条目保留当时快照（例如早期“USS/RK512 尚无在线事务”），与当前共享 COM 只读实现冲突时以本节和交接文档为准。

## 结论

西门子页面此前已经有 PPI、Fetch/Write、Web API 的独立连接入口和 Electron/Rust 命令，但变量读写仍统一调用 S7comm 命令；USS、RK512 选择项也会落入普通 S7comm 连接路径。该状态不能称为协议变体端到端完成。

本轮已完成首个可验证修复：

- 新增 src/siemens-route.js，集中描述 Siemens 变体到命令族的映射。
- S7comm 和 SMART 继续调用 s7_read/s7_write。
- PPI 调用 ppi_read/ppi_write。
- Fetch/Write 调用 fw_read/fw_write，并把默认端口从误用的 102 修正为 2000。
- Web API 调用 s7web_read/s7web_write，断开调用 s7web_disconnect。
- USS、RK512 在没有在线事务实现时显式阻止连接，不再静默回退到 S7comm。
- 已连接后锁定变体和型号选择，CPU 诊断/控制按钮只在 S7comm 会话启用。
- 主站 Modbus FC05/06/15/16 写入统一执行：写前读取旧值 → 显示协议/地址/旧值/新值并确认 → 写入 → 同地址回读比对 → JSONL 审计。广播写无法满足旧值/回读门禁，已阻止。S7/MC CPU 控制使用输入特定词的高风险确认并审计。
- 新增 `ppi-serial` 路由：只复用主站页已打开的 COM 口，不再把 TCP 网关路径代表原生串口。
- `ppi-serial` 当前只开放只读双拍（SD2 → E5 → SA → SD2）；写入按钮显式禁用。
- Protocol Registry MVP 已覆盖 52 个当前可选择变体，连接帮助目录与注册表集合一致。
- 欧姆龙新增 `hostlink-serial` 变体：首轮只实现 C-mode `RR` 读 DM，不把 FINS/TCP、FINS/UDP 和 HostLink FINS 混为同一路由。
- 欧姆龙新增 `hostlink-fins-serial` 变体：首轮只实现 HostLink FINS 0101 字读取，写入/位读取/完整服务集保持禁用。
- FINS TCP/UDP 网络读写增加软件安全前置：单次 1..512 点、三字节地址窗口、成功响应长度和位写入值严格校验；该上限不等同于具体 CPU 的能力，自动分段仍待 OMRON-008。
- Allen-Bradley 新增独立 `allen-bradley/cip` 页面和 `enip_*` 命令族；Rust 负责 EtherNet/IP 24 字节封装、Register/Unregister、SendRRData/CPF、CIP Read Tag 路径与响应状态解析。当前已接入 TCP 44818 RegisterSession/Read Tag 只读会话和独立 TCP 对端回归；不实现写入、ForwardOpen、PCCC 或 Implicit I/O。
- Beckhoff 新增独立 `beckhoff/ads` 页面和 `ads_*` 命令族；Rust 负责 AMS/TCP 6 字节头、32 字节 AMS 头、NetId/Port、InvokeId、ADS Read/ReadDeviceInfo/ReadState 会话和编解码。当前已接入 TCP 48898 只读会话和独立 TCP 对端回归；不创建 AMS Route、不缓存符号句柄或订阅通知。
- Keyence 新增独立 `keyence/kv-host-link` 页面和 `keyence_*` 命令族；Rust 负责 KV Host Link ASCII 的 CR/CR NN → CC 握手、RDS/WRS、ST/RS、地址归一化、CRLF 响应、数量/值边界和 E0/E1/E2/E4/E5/E6 错误映射。当前已接入用户显式 TCP 8501 只读会话和独立 TCP 对端回归；WRS/ST/RS、MC Compatible、EtherNet/IP、真实 KV 型号和现场 L2 仍未完成。
- LS Electric 新增独立 `ls-electric/xgt-fenet` 页面和 `ls_xgt_*` 命令族；Rust 负责 XGT FEnet 20 字节头、Company ID、CPU/Base/Slot、InvokeId、X/B/W/D/L 显式地址、单变量/连续读写和响应错误/数据边界。当前已接入用户显式 TCP 2004 只读会话和独立 TCP 对端回归；写入、PLC 控制、型号兼容宣称和真实 L2 仍未完成。
- Panasonic 新增独立 `panasonic/mewtocol-com` 页面和 `panasonic_*` 命令族；Rust 负责 MEWTOCOL-COM `%`/`<` ASCII 头、1..32 站号、RD/WD、RCS/WCS、XOR BCC、CR、数据/触点地址和正常/错误响应解析；Electron 新增 `panasonic_serial_read`，只复用主站页已打开的 COM 做 RD/RCS，支持 CR 收集、完整 TX 回显剥离、有限重试和设备记录。真实 FP 电气层、型号和 L2 仍未完成。
- Delta 新增 `delta/dvp-modbus` 与 `delta/as-modbus` 两个地址 profile 页面和 `delta_parse_address` 命令；Rust 固定 DVP/AS 地址、DVP X/Y 八进制、D/M 分段、AS X/Y 位/字空间和功能码；Electron `delta_modbus_plan`/`delta_modbus_read` 复用已打开的 RTU/ASCII Modbus 串口，跨 D4095/D4096、M1535/M1536 拆分后只读拼接并记录型号/固件。真实型号/固件/L2 仍未完成。
- 汇川新增 `inovance/h3u-modbus` 与 `inovance/h5u-modbus` 两个独立 Modbus 地址 profile 页面和 `inovance_parse_address` 命令；Rust 固定 H3U/H5U 的 M/SM/S/T/C/X/Y、D/SD/R 映射、八进制 X/Y、H3U M 空洞和 C200+ 双寄存器规则；AM/AC/Easy、EasyNet、Connected CIP、ComputerLink 和未锁定的 FX/MC 兼容模式明确拒绝。当前只完成 S2/S3 软件映射与桌面路由，真实 Modbus RTU/TCP 型号/L2 仍未完成。
- 信捷新增 `xinje/xc-modbus` 与 `xinje/xd-modbus` 两个独立 Modbus 地址 profile 页面和 `xinjie_parse_address` 命令；基于旧项目审计只确认 XC/XD/XL 的 D0..D65535 为 0-based holding register、FC03/FC06，HD/SD/SM/M/X/Y/C/T/S 等区域和未经型号确认的八进制规则明确拒绝。当前只完成 D 区 S2/S3 软件映射与桌面路由，真实 XC/XD/XL Modbus RTU/TCP 型号/L2 仍未完成。
- FATEK 新增 `fatek/ascii` 页面、`open_fatek_connection`/`fatek_read_words`/`fatek_read_discrete` 和 `fatek_*` 命令族；Rust 固定 FBs 原生 STX + 站号十六进制 + 命令/数据 + additive checksum + ETX 帧、40/44/45/46/47 命令、X/Y/M/S/T/C 位区和 R/D/RT/RC 字区。当前已接入用户显式 TCP 5000 只读会话和独立 TCP 对端回归，写入/RUN/STOP、型号差异、真实 L2 与 Ethernet Gateway 行为仍未完成。
- Fuji 新增 `fuji/sph` 页面、`open_fuji_sph_connection`/`fuji_sph_read` 和 `fuji_sph_*` 命令族；Rust 固定 MICREX-SX SPH Loader Command 20 字节二进制头、00H/01H、连接 ID、M1/M3/M10/I/Q 类型码、24 位小端字地址、16 位小端字数、CPU error byte 和响应长度/回显边界。当前已接入用户显式 TCP 18245 只读会话和独立 TCP 对端回归，不推断 CPU 状态、不开放位写入；SPH 型号/固件/Loader L2 仍未完成。
- GE 新增 `ge/srtp` 页面、`open_ge_srtp_connection`/`ge_srtp_read` 和 `ge_srtp_*` 命令族；Rust 固定 56 字节全零会话初始化、事务号、R/AI/AQ 16 位字区、I/Q/T/M/SA/SB/SC/S/G 字节/位数据码、短响应 `0xD4`、长响应 `0x94` 和 PLC 状态/长度边界。当前已接入用户显式 TCP 18245 只读会话和独立 TCP 对端回归，不推断 PLC 状态、不提供程序/时钟/订阅或虚拟服务器；Series 90/PACSystems 型号/固件/L2 仍未完成。
- MQTT 新增 `mqtt/mqtt-311` 页面和 MQTT 3.1.1 命令族；Rust 固定剩余长度、CONNECT/CONNACK、SUBSCRIBE/SUBACK、QoS 0 PUBLISH、PINGREQ/PINGRESP、DISCONNECT 编解码，并接入 TCP 1883 显式连接、只读订阅、单帧 PUBLISH 解码和 PING 保活。Aedes 1.1.1 Broker + MQTT.js 5.15.2 外部发布端互操作 1/1 已通过，覆盖匿名允许、Topic ACL 允许/拒绝和需认证连接拒绝；当前仍不开放发布写入、用户名密码/TLS、QoS 1/2 在线事务或 Sparkplug 状态机，生产 Broker/现场 L2 未完成。
- IEC104 新增 `iec/iec-60870-5-104` 页面和九个命令；Rust 固定 I/S/U、15 位 N(S)/N(R)、STARTDT/STOPDT/TESTFR、站/组总召、COT、Quality 和 M_SP/M_DP/M_ME_NA/M_ME_NC/M_IT 五类监视 ASDU。TCP 2404 会话要求 STARTDT_CON 和 ACT_CON→数据→ACT_TERM，对每个 I 帧立即 S 确认；独立脚本 Outstation 4/4 已通过。遥控、设点、校时、带时标类型、完整定时器/窗口和真实 RTU/IED L2 未完成。
- DNP3 新增 `dnp/dnp3-tcp` 页面和十个命令；Rust 固定 `05 64` 链路头、头/16-byte block CRC、little-endian Link Address、Transport/Application 分片序号、READ/CONFIRM、Class 0/1/2/3、IIN/Quality 和 g1/g2/g3/g4/g10/g11/g20-g23/g30/g32/g40/g42 静态/事件/时间对象。TCP 20000 会话只允许 Master 读取并自动确认 CON；独立实现 CRC/链路/Transport 的脚本 Outstation 4/4 已通过。Select/Operate/Direct Operate、校时、Restart/Freeze、Secure Authentication、串口和真实 RTU/IED L2 未完成。
- DL/T 645 新增 `dlt/dlt645-2007` 与 `dlt/dlt645-1997` 两个分版变体及五个只读编解码命令；Rust 严格处理 12 位 BCD 表地址低位在前、0..4 个 FE 前导字节、两次 68H、L 字段、33H 加减、校验和、16H 结束符和分版控制码/DI 长度。Electron 只复用用户已打开的共享 COM，按 L 收完整帧、剥离精确本地回显、有限重试并记录表型号/序列号；首批只读电量、日期、时间和状态字，写表、校时、拉合闸及广播读均不存在。软件/独立表端向量已通过，真实电表、电气层、厂家 DI 和 L2 未完成。
- CJ/T 188 新增 `cjt/cjt188-2004` 变体和六个离线命令；主流 2004 帧型锁定为单 68H、模 256 算术和、无 +33H、DI(2)+SER，旧 Nexus.Cjt 双 68H/XOR/+33H 判定为偏差。真实水/气/热表、唤醒时序、后续帧和厂家 DI 仍为 L2 pending。
- BACnet/IP 新增 `bacnet/bacnet-ip` 变体、六个离线命令和三个 UDP 只读会话命令；Who-Is/I-Am 离线部分使用 BVLC 81H+0AH/0BH、本地 NPDU、10H+08H/00H 并严格校验 `C4/22/91/21` 标签；在线 Who-Is 固定 0AH 定向单播，ReadProperty 使用 DER NPDU、Confirmed 0CH 和 ComplexACK 30H，并核对 context [0]/[1]/[2]、[3] 值包装与 Invoke/对象/属性回显。bacstack 0.0.1-beta.14 独立栈互操作 1/1 已通过。旧 Nexus.Bacnet 的 BVLC/标签/事务偏差不继承；RPM、写入、COV、BBMD/FDR 和真实楼宇设备 L2 未完成。
- KNXnet/IP 新增 `knx/tunneling-v1` 变体、八个离线命令和七个 UDP 只读会话命令；固定公共头 `06 10`、Connect Request/Response、三层组地址、GroupValueRead 的 L_Data.req、Tunneling ACK、GroupValueResponse 的 L_Data.ind + ACK、手动/周期 Connection State 和 Disconnect。knx 2.5.4 独立编解码器互操作 1/1 已通过。旧 Nexus.Knx 曾出现的 `10 00` 头、无握手伪隧道和 ACK/数据响应混淆不继承；写组值、Routing、Secure 和真实网关 L2 未完成。

## 审计矩阵

| UI variant | 连接入口 | 读取命令 | 写入命令 | 断开 | 当前结论 |
|---|---|---|---|---|---|
| s7comm | open_s7_connection | s7_read | s7_write | close_connection | 软件路径已保持 |
| smart | open_s7_connection | s7_read | s7_write | close_connection | SMART 是 S7comm 地址 profile |
| ppi | open_ppi_tcp | ppi_read | ppi_write | close_connection | 当前是 TCP/串口服务器透传，不是原生 COM |
| ppi-serial | get_serial_status + ppi_serial_read | ppi_serial_read | —（只读） | UI 清理状态，不关闭共享 COM | 原生 COM 双拍已接入软件路径，真实 PLC L2 待验 |
| fw | open_fw_tcp | fw_read | fw_write | close_connection | S5 Fetch/Write 独立路径 |
| webapi | s7web_connect | s7web_read | s7web_write | s7web_disconnect | Electron HTTPS 服务独立路径 |
| uss | get_serial_status + uss_serial_read | uss_serial_read | —（只读） | UI 清理状态，不关闭共享 COM | 参数只读软件路径已接入，真实变频器 L2 待验 |
| rk512 | get_serial_status + rk512_serial_read | rk512_serial_read | —（只读） | UI 清理状态，不关闭共享 COM | 3964R/RK512 指定区只读软件路径已接入，真实 CP L2 待验 |
| hostlink-serial | get_serial_status + omron_hostlink_serial_read | omron_hostlink_serial_read | —（只读） | UI 清理状态，不关闭共享 COM | C-mode RR 读 DM 软件首轮已接入，真实 CPU/串口 L2 待验 |
| hostlink-fins-serial | get_serial_status + omron_hostlink_serial_read(mode=fins) | omron_hostlink_serial_read | —（只读） | UI 清理状态，不关闭共享 COM | HostLink FINS 0101 字读取软件首轮已接入，真实 CPU/串口 L2 待验 |
| allen-bradley/cip | `open_enip_connection`、`enip_read_tag` + enip_build_* / enip_parse_* | `enip_read_tag`（TCP 44818 只读） | —（Write/ForwardOpen 不进入 live path） | 显式 TCP RegisterSession + Read Tag 只读页 | EtherNet/IP/CIP 软件 S2-S4a 已接入；真实 CompactLogix/ControlLogix、ListIdentity 和 L2 待验 |
| beckhoff/ads | `open_ads_connection`、`ads_read`、`ads_read_device_info`、`ads_read_state` + ads_build_* / ads_parse_* | `ads_read`/`ads_read_device_info`/`ads_read_state`（TCP 48898 只读） | —（Write/WriteControl 不进入 live path） | 显式 TCP AMS endpoint 校验 + 只读页 | ADS/AMS 软件 S2-S4a 已接入；真实 AMS Route/TwinCAT Runtime/型号和 L2 待验 |
| keyence/kv-host-link | `open_keyence_connection`、`keyence_read_words`、`keyence_read_bits` + `keyence_build_*` / `keyence_parse_*` | `keyence_read_words`/`keyence_read_bits`（TCP 8501 只读） | —（WRS/ST/RS 只做离线构帧） | 显式 TCP 只读会话 + 编解码页 | Keyence 软件 S2-S4a 已接入；真实 KV 型号/Host Link/L2 待验 |
| ls-electric/xgt-fenet | `open_ls_xgt_connection`、`ls_xgt_read`、`ls_xgt_read_continuous` + ls_xgt_build_* / ls_xgt_parse_* | `ls_xgt_read`/`ls_xgt_read_continuous`（TCP 2004 只读） | —（Write 只做离线构帧） | 显式 TCP 只读会话 + 编解码页 | XGT FEnet 软件 S2-S4a 已接入；真实 XGK/XGI/XGR/L2 待验 |
| panasonic/mewtocol-com | panasonic_build_* / panasonic_parse_* + `panasonic_serial_read` | `panasonic_serial_read`（RD/RCS，只读） | —（WD/WCS 只离线构帧） | 复用共享 COM，不自动开关 | MEWTOCOL-COM S3/S4 软件会话已接入；真实 FP 型号/电气层/L2 待验 |
| delta/dvp-modbus | `delta_parse_address` + `delta_modbus_plan/read` | `delta_modbus_read`（共享 RTU/ASCII、只读） | —（本页不发写） | 复用主站已打开 COM | DVP/ES/EX/SS profile、跨区拆分和读回软件边界已接入；真实型号/L2 待验 |
| delta/as-modbus | `delta_parse_address` + `delta_modbus_plan/read` | `delta_modbus_read`（共享 RTU/ASCII、只读） | —（本页不发写） | 复用主站已打开 COM | AS300/DVP-ES3 profile、跨区拆分和读回软件边界已接入；真实型号/L2 待验 |
| inovance/h3u-modbus | `inovance_parse_address` | `inovance_parse_address`（只读地址规划） | —（本页不发写） | 当前为 Modbus profile 页 | H3U 地址 profile S2/S3 已接入；真实 Modbus RTU/TCP/L2 待验 |
| inovance/h5u-modbus | `inovance_parse_address` | `inovance_parse_address`（只读地址规划） | —（本页不发写） | 当前为 Modbus profile 页 | H5U 地址 profile S2/S3 已接入；AM/AC/Easy 与真实型号/L2 待验 |
| xinje/xc-modbus | `xinjie_parse_address` | `xinjie_parse_address`（D 区只读地址规划） | —（本页不发写） | 当前为 Modbus profile 页 | XC 仅 D 区 S2/S3 已接入；其他区域和真实型号/L2 待验 |
| xinje/xd-modbus | `xinjie_parse_address` | `xinjie_parse_address`（D 区只读地址规划） | —（本页不发写） | 当前为 Modbus profile 页 | XD/XL 仅 D 区 S2/S3 已接入；XC/XD 差异和真实 L2 待验 |
| fatek/ascii | `open_fatek_connection`、`fatek_read_words`、`fatek_read_discrete` + `fatek_*` 编解码 | `fatek_read_words`/`fatek_read_discrete`（TCP 5000 只读） | —（写入只做离线构帧） | 显式 TCP 只读会话 + 编解码页 | FATEK 软件 S2-S4a 已接入；真实 FBs/Gateway/型号/L2 待验 |
| fuji/sph | `open_fuji_sph_connection`、`fuji_sph_read` + `fuji_sph_*` 编解码 | `fuji_sph_read`（TCP 18245 只读） | —（写入只做离线构帧） | 显式 TCP 只读会话 + 编解码页 | SPH 软件 S2-S4a 已接入；真实 CPU/固件/Loader/L2 待验 |
| ge/srtp | `open_ge_srtp_connection`、`ge_srtp_read` + `ge_srtp_*` 编解码 | `ge_srtp_read`（TCP 18245 只读） | —（写入只做离线构帧） | 显式 TCP 只读会话 + 编解码页 | GE 软件 S2-S4a 已接入；真实 Series 90/PACSystems 型号、固件和 L2 待验 |
| mqtt/mqtt-311 | `open_mqtt_connection`、`mqtt_subscribe`、`mqtt_read_publish`、`mqtt_ping` + `mqtt_*` 编解码 | `mqtt_subscribe`/`mqtt_read_publish`/`mqtt_ping`（TCP 1883 只读） | —（PUBLISH 写入不进入 live path） | 显式 TCP CONNECT/CONNACK + SUBSCRIBE/SUBACK + PING | MQTT 3.1.1 软件 S2-S4b 已接入，Aedes/MQTT.js 互操作 1/1；生产 Broker/正向认证/TLS/Sparkplug/L2 待验 |
| iec/iec-60870-5-104 | `open_iec104_connection`、`iec104_general_interrogation`、`iec104_test_frame` + `iec104_*` 只读编解码 | `iec104_general_interrogation`（TCP 2404 只读总召） | —（遥控/设点/校时命令不存在） | STARTDT + TESTFR + best-effort STOPDT | IEC104 软件 S2-S4a 已接入，独立脚本 Outstation 4/4；真实 RTU/IED、带时标/控制/长稳/L2 待验 |
| dnp/dnp3-tcp | `open_dnp3_connection`、`dnp3_integrity_poll`、`dnp3_class_scan`、`dnp3_read` + `dnp3_*` 只读编解码 | Integrity、Class 0/1/2/3、对象 READ（TCP 20000 只读 Master） | —（Select/Operate/校时/Restart/Freeze 命令不存在） | 应用 CON 自动 CONFIRM + best-effort TCP close | DNP3 软件 S2-S4a 已接入，独立脚本 Outstation 4/4；生产栈/真实 RTU/IED/认证/串口/控制/长稳/L2 待验 |
| dlt/dlt645-2007、dlt/dlt645-1997 | `get_serial_status` + `dlt645_serial_read` + 五个 `dlt645_*` 编解码命令 | 指定 DI 的共享 COM 只读 | —（写表/校时/拉合闸命令不存在） | UI 清理状态，不关闭共享 COM | 2007/1997 分版 S2-S4a 软件/独立表端向量已接入；后续帧、厂家 DI、真实表计/电气层/L2 待验 |
| cjt/cjt188-2004 | `cjt188_parse_meter_type/address/data_id`、`cjt188_build_read_request`、`cjt188_parse_frame/read_response` | —（本轮无 COM/TCP live path） | —（写数据/写地址/阀门/后续帧命令不存在） | 离线构帧/解帧页 | CJ/T 188-2004 主流单 68H 帧型 S1-S3 审计+离线编解码已接入；旧 Nexus.Cjt 双 68H/XOR/+33H 判定为偏差；真实水/气/热表与唤醒时序 L2 待验 |
| bacnet/bacnet-ip | `open_bacnet_ip_connection` + 六个离线命令 | `bacnet_ip_whois`（0AH 定向 Who-Is/I-Am）、`bacnet_ip_read_property_live` | —（RPM/Write/COV/BBMD/FDR 命令不存在） | 显式 UDP 对端 + 离线构帧/解帧页 | Who-Is/I-Am 与 ReadProperty 离线 S1-S3、独立脚本 UDP 对端 S4a、bacstack 独立栈互操作 S4b 已接入；真实楼宇设备 L2 待验 |
| knx/tunneling-v1 | 八个 `knx_*` 离线命令 | `open_knx_connection`、`knx_group_read`、`knx_connection_state`、`knx_start_keepalive`、`knx_stop_keepalive`、`knx_keepalive_status`、`knx_disconnect` | —（写组值/场景/设备管理命令不存在） | 显式 UDP 网关 + 离线构帧/解帧页 | KNXnet/IP Tunneling v1 离线 S1-S3、独立脚本 UDP 网关 + 手动/周期状态检查和显式重连 S4a、knx 独立栈互通 S4b 已接入；真实 Interface/Router L2 待验 |

## 分层证据

### UI

- src/main.js 的 s7Connect 根据 route 分流。
- s7Read 根据 route.kind 选择 Web API、PPI、Fetch/Write 或 S7comm。
- s7Write 根据 route.kind 选择对应 payload 和安全确认。
- s7Disconnect 不再把 Web API 会话当作 Rust TCP 会话。
- `ppi-serial` 连接前检查共享 COM 句柄，读取失败时保留两拍 TX/RX 诊断。
- USS 已新增独立串口参数只读路径：`uss_serial_read` 复用共享 COM，Rust `uss_build_request/uss_parse_response` 负责帧和 AK 解析；USS 写参数/控制字仍禁用。
- RK512 已新增独立串口只读路径：`rk512_serial_read` 执行 3964R STX/DLE 握手、RK512 数据读和 DLE 释放；写入与现场优先级策略仍禁用/待验。
- Omron HostLink 已新增独立串口只读路径：`omron_hostlink_serial_read` 复用共享 COM，Rust `hostlink_build_cmode_read/hostlink_parse_cmode_read` 负责 RR/FCS/DM 字解析；HostLink FINS、写入和非 DM 区域仍禁用。
- Omron HostLink FINS 已沿用独立 `mode=fins` 路由，Rust `hostlink_build_fins/hostlink_parse_fins` 负责 FINS 0101/FCS/结束码/字数据解析；写入、位读取和高级服务仍禁用。
- Allen-Bradley 页面调用 `enip_build_register_session`、`enip_build_unregister_session`、`enip_build_read_tag`、`enip_parse_frame` 和 `enip_parse_cip_response`；页面只显示生成/解析结果，不会因为生成报文而声称已连 PLC。
- Beckhoff 页面调用 `ads_build_read`、`ads_build_write`、`ads_build_readwrite`、`ads_build_read_device_info`、`ads_build_read_state`、`ads_parse_frame` 和 `ads_parse_response`；页面只显示生成/解析结果，不会因为生成报文而声称已连 TwinCAT/PLC。
- Keyence 页面调用 `open_keyence_connection`、`keyence_read_words`、`keyence_read_bits`、`keyence_build_connect`、`keyence_build_read_words`、`keyence_build_read_bits`、`keyence_build_write_words`、`keyence_build_write_bit`、`keyence_parse_*`；连接和读取需用户显式点击，按 CR/CRLF 收发并标注软件 TCP 对端证据，不会把该证据声称为 KV PLC L2。
- LS Electric 页面调用 `ls_xgt_parse_address`、`ls_xgt_build_read`、`ls_xgt_build_continuous_read`、`ls_xgt_build_write`、`ls_xgt_build_continuous_write`、`ls_xgt_parse_response`；页面只显示 XGT HEX 生成/解析结果，不会因为生成报文而声称已连 PLC。
- Panasonic 页面调用 `panasonic_parse_data_address`、`panasonic_parse_contact_address`、`panasonic_build_read`、`panasonic_build_write`、`panasonic_build_read_contact`、`panasonic_build_write_contact` 和 `panasonic_parse_response`；RD/RCS 的 `panasonic_serial_read` 只复用主站页共享 COM，不自动打开/关闭 COM，WD/WCS 不进入现场事务。
- Delta 页面调用 `delta_parse_address`、`delta_modbus_plan` 和 `delta_modbus_read`；服务逐项复用 Rust profile 规划连续段，再调用现有 Modbus RTU/ASCII 只读 reader，保留跨 D/M 边界的分段 TX/RX、失败段和型号/固件记录，不执行任何写入。
- 汇川页面调用 `inovance_parse_address`；UI 明示底层为 Modbus RTU/TCP，按 H3U/H5U 和 Auto/Bit/Word 选择解析独立软元件 profile，AM/AC/Easy 和未确认兼容模式 fail-closed，不调用旧的通用 `brand_parse_address`。
- 信捷页面调用 `xinjie_parse_address`；UI 明示底层为 Modbus RTU/TCP，按 XC/XD-XL 和 Auto/Word 解析已确认的 D 寄存器，其他区域、Bit 访问和未确认兼容模式 fail-closed，不调用旧的通用 `brand_parse_address`。
- FATEK 页面调用 `open_fatek_connection`、`fatek_read_words`、`fatek_read_discrete`、`fatek_parse_address`、`fatek_build_read_discrete`、`fatek_build_write_discrete`、`fatek_build_read_words`、`fatek_build_write_words`、`fatek_pack_command` 和 `fatek_parse_response`；连接需用户显式点击，按 ETX 收只读响应并校验站号/命令/状态/加和校验/数据长度，不把软件 TCP 对端证据当成 FBs 现场通过。
- Fuji 页面调用 `open_fuji_sph_connection`、`fuji_sph_read`、`fuji_sph_parse_address`、`fuji_sph_build_read`、`fuji_sph_build_write` 和 `fuji_sph_parse_response`；连接需用户显式点击，按 20 字节响应头 payload length 收取只读数据并校验地址/类型/字数回显，不推断 CPU 状态、不把软件 TCP 对端证据当成 SPH 现场通过。
- GE 页面调用 `open_ge_srtp_connection`、`ge_srtp_read` 以及 `ge_srtp_parse_address`、`ge_srtp_build_handshake`、`ge_srtp_parse_handshake`、`ge_srtp_build_read`、`ge_srtp_build_write`、`ge_srtp_parse_response`；连接需用户显式点击，先完成 56B 会话初始化，只提供 TCP 只读，不推断 PLC 状态、不把软件/独立对端证据当成 GE 现场通过。
- MQTT 页面调用 `open_mqtt_connection`、`mqtt_subscribe`、`mqtt_read_publish`、`mqtt_ping` 以及 `mqtt_build_connect`、`mqtt_parse_connack`、`mqtt_build_subscribe`、`mqtt_parse_suback`、`mqtt_parse_publish`、`mqtt_build_pingreq`、`mqtt_parse_pingresp`、`mqtt_build_disconnect`；连接需用户显式点击，默认 clean session、QoS 0 订阅和无凭据 TCP 1883，只读取 Broker 返回的 PUBLISH，不提供 publish 写入或 Sparkplug 命令。
- IEC104 页面调用 `open_iec104_connection`、`iec104_general_interrogation`、`iec104_test_frame` 以及 I/S/U/APDU/ASDU 离线编解码；连接需用户显式点击，页面只允许 TESTFR 和总召，不提供遥控、设点、校时、文件传输或手工控制 ASDU 入口。
- DNP3 页面调用 `open_dnp3_connection`、`dnp3_integrity_poll`、`dnp3_class_scan`、`dnp3_read` 以及链路/应用层离线编解码；连接需用户显式点击，页面只允许 READ/CONFIRM，不提供 Select/Operate、Analog Output、Time Write、Restart、Freeze、File 或手工控制 PDU 入口。
- DL/T 645 页面按 2007/1997 显式分版，调用 `dlt645_serial_read` 和地址/DI/读请求/帧/读响应五个离线命令；只允许用户指定表地址与 DI 后复用当前共享 COM 读取，不提供写数据、校时、拉合闸、清零或广播读取入口。
- CJ/T 188 页面调用 `cjt188_parse_meter_type`、`cjt188_parse_address`、`cjt188_parse_data_id`、`cjt188_build_read_request`、`cjt188_parse_frame` 和 `cjt188_parse_read_response`；页面只做离线构帧/解帧，不打开 COM、不发送请求，不提供写数据、写地址、阀门控制或后续帧入口。
- BACnet/IP 页面调用 `open_bacnet_ip_connection`、`bacnet_ip_whois`、`bacnet_ip_read_property_live`、`close_connection` 和六个离线命令；在线路径只连接显式 UDP 对端并执行 0AH Who-Is / ReadProperty，不使用 0BH 广播，不提供 RPM、写入、COV、BBMD/FDR 或 MS/TP 入口。
- KNXnet/IP 页面调用 `open_knx_connection`、`knx_group_read`、`knx_connection_state`、`knx_start_keepalive`、`knx_stop_keepalive`、`knx_keepalive_status`、`knx_disconnect` 和八个离线命令；在线路径只面向显式 UDP 网关并执行 Connect、GroupValueRead、Connection State/周期保活、Disconnect，不提供写组值、场景、Routing、Device Management 或 Secure 入口。

### Preload/Electron

- electron/preload.cjs 已暴露 PPI、Fetch/Write、Web API、原生串口只读服务、品牌 profile、ENIP、ADS、Keyence、LS XGT、Panasonic、FATEK、Fuji、GE、十二个 MQTT 命令、九个 IEC104 命令、十个 DNP3 命令、五个 DL/T 645、六个 CJ/T 188、九个 BACnet/IP 和十五个 KNXnet/IP 只读命令；IEC104、DNP3、DL/T 645、CJ/T 188、BACnet/IP 与 KNXnet/IP 都没有控制或校时白名单。
- electron/main.cjs 已分别注册 Rust Core 透传、各原生串口服务、品牌 profile、Allen-Bradley、Beckhoff、Keyence、LS XGT、Panasonic、FATEK、Fuji、GE、MQTT、IEC104、DNP3、DL/T 645 共享 COM 只读、CJ/T 188、BACnet/IP 离线/UDP 只读和 KNXnet/IP 离线/UDP 只读 IPC。
- PPI 服务保持串口句柄独占，复用 serial-service 的 PPI 帧收集器；未增加写入入口。
- collector 已加入 PPI 本地回显剥离、E5/NAK 终止；服务对站号/功能码不匹配执行最多 3 次以内的有限重试。

### Rust Core

- rust-core/src/protocol.rs 已存在 PPI、Fetch/Write、S7comm 命令处理器，并新增 `ppi_build_read`、`ppi_build_sa_confirm`、`ppi_parse_read_response` 离线命令。
- rust-core/src/fins_frame.rs 修复 FINS 响应短帧边界：至少 14 字节后才读取结束码，避免畸形 HostLink/FINS 响应触发索引 panic。
- FINS TCP/UDP 与 HostLink FINS 解析现在保留并核对 SID；响应串号不匹配时 fail-closed，不把重复响应当作当前请求结果。
- `rust-core/src/fins_frame.rs` 暴露 `FINS_MAX_POINTS=512`、`validate_access_window` 和 `expected_data_bytes`；`session.rs` 在网络读写前后校验点数、地址窗口、写入数据字节数和成功响应长度。
- `rust-core/src/protocol.rs` 拒绝超出 `u16`/512 点的写入数组，并拒绝位写入值大于 1；`src/main.js`/`index.html` 同步显示并预检 512 点网络上限、100 点 HostLink 首轮上限。
- `rust-core/src/fins_slave.rs` 的 TCP/UDP 虚拟 PLC 复用同一访问窗口门禁，拒绝非法 word/bit 标志和多余写入尾字节；短请求错误也生成带 0101 服务码的可解析响应。该项只提高 L1 仿真证据，不代表真实 CPU 的区长度或设备批量能力。
- HostLink FINS 0101 读取也在 Electron 串口服务和 Rust 构帧层拒绝 `0xFFFFFF + count` 的地址窗口回绕，避免三字节地址截断。
- HostLink C-mode `RR` 响应解析现在严格校验站号 0..31、`RR` 头、结束码、FCS、`*`/CRLF 和 4 字符字数据；C-mode 构帧与 Electron 服务同步拒绝 DM 地址窗口回绕。
- RK512 读/写构帧现在限制 1..512 字和 0..65535 字节窗口，Rust/Electron 对成功响应统一检查 `count×2` 数据长度；这仍不代表 CP341/441 的型号能力或 3964R 优先级策略已通过 L2。
- Fetch/Write Rust 层已增加固定头、OPC、精确长度和写请求数据长度校验；该项只证明软件黄金帧/虚拟服务边界，真实 S5/CP 返回码仍待 L2。
- rust-core/src/session.rs 已按连接类型区分 PpiTcp、FwTcp、S7Tcp。
- 原生 COM 的物理 I/O 仍由 Electron 持有；Rust 只负责地址/PDU/FCS/响应项解析，TCP 透传与 COM 两条路径没有混用。
- `rust-core/src/ads.rs` 固定 ADS/TCP reserved、AMS 头字段偏移、little-endian IndexGroup/Offset/length、Read/Write/ReadWrite payload、ReadDeviceInfo/ReadState 长度、InvokeId 和可选源/目标端点匹配及 AMS/ADS 错误解释；`Session` 已接入 TCP 48898 只读 Read/ReadDeviceInfo/ReadState，独立 TCP 对端已验证，AMS Route/符号句柄/通知仍禁用。
- `rust-core/src/keyence.rs` 固定 KV Host Link ASCII 命令、地址矩阵/位归一化、CR/CRLF 边界、1..256 数量、字/位响应和设备错误码；`rust-core/src/session.rs` 增加握手后 RDS 字/位 TCP 只读收发并 fail-closed。
- `rust-core/src/ls_xgt.rs` 固定 XGT FEnet 官方 20 字节头、请求/响应 command、Company ID、checksum、InvokeId、显式变量和连续 byte count；`rust-core/src/session.rs` 增加按 application length 收帧的单变量/连续 TCP 只读收发并 fail-closed。
- `rust-core/src/panasonic.rs` 固定 MEWTOCOL-COM 站号、标准/扩展头、XOR BCC、CR、DT/D/LD/FL/F 和 X/Y/R/L/T/C 地址以及 RD/WD/RCS/WCS 响应边界；`electron/panasonic-serial-service.cjs` 在共享 COM 层只允许 RD/RCS。
- `rust-core/src/delta.rs` 固定 Delta DVP/AS Modbus 地址 profile、八进制 X/Y、不连续 D/M 段、AS X/Y 位/字区、功能码和寄存器取位拒绝边界；不包含独立 Delta 私有传输。
- `rust-core/src/inovance.rs` 固定 H3U/H5U Modbus 地址 profile、八进制 X/Y、H3U M 空洞、H3U C200+ 双寄存器和 H5U R/B 边界；AM/AC/Easy 与未知兼容模式返回结构化错误，不把旧 `brand_profiles.rs` 的通用映射当作新 UI 路由。
- `rust-core/src/xinjie.rs` 固定 XC/XD/XL 首轮已确认的 D 数据寄存器映射（0-based、FC03/FC06）；HD/SD/SM/M/X/Y/C/T/S 等未确认区域返回结构化错误，不把旧 `XinjeAddress` 的猜测偏移当作新 UI 路由。
- `rust-core/src/fatek.rs` 固定 FBs 原生 ASCII 的 STX/ETX、站号、40/44/45/46/47 命令、加和校验、离散/字地址与响应错误边界；`session.rs` 已接入 TCP 5000 只读会话、ETX 收帧和 44/46 数据长度/0-1 校验，不包含写入、RUN/STOP 或 Ethernet Gateway。
- `rust-core/src/fuji_sph.rs` 固定 SPH Loader Command 的 20 字节头、连接 ID、00H/01H、M1/M3/M10/I/Q 类型码、24 位小端地址、1..230 字和 CPU error/响应长度边界；`session.rs` 已接入 TCP 18245 只读会话、payload length 收帧和请求/响应地址回显校验，不包含 CPU 状态控制、写入或自动重连。
- `rust-core/src/ge_srtp.rs` 固定 SRTP 56 字节会话初始化、事务号、R/AI/AQ 字区、I/Q/T/M/SA/SB/SC/S/G 字节/位数据码、读/写报头、短/长响应和 PLC 状态/长度边界；`session.rs` 已接入 TCP 18245 会话初始化和只读事务，仍不包含 PLC 状态推断或程序服务。
- `rust-core/src/mqtt.rs` 固定 MQTT 3.1.1 剩余长度、UTF-8/协议名、CONNECT/CONNACK、SUBSCRIBE/SUBACK、QoS 0 PUBLISH、PINGREQ/PINGRESP、DISCONNECT 和 1 MiB 帧上限；`session.rs` 已接入 TCP 1883 CONNECT/CONNACK、SUBSCRIBE/SUBACK、PUBLISH 单帧读取、PING 保活和 best-effort DISCONNECT，默认不携带用户名/密码、不发送 PUBLISH。
- `rust-core/src/iec104.rs` 固定 IEC 60870-5-104 APDU 长度、I/S/U 帧、15 位 N(S)/N(R)、STARTDT/STOPDT/TESTFR、C_IC_NA_1 站/组总召、顺序/非顺序 IOA、COT、Quality 和 M_SP/M_DP/M_ME_NA/M_ME_NC/M_IT 五类监视 ASDU；`session.rs` 已接入 TCP 2404 只读 Client/Master，会话要求 STARTDT_CON，总召要求 ACT_CON→监视数据→ACT_TERM，并逐 I 帧发送 S 确认。遥控、设点、校时、带时标类型和 Outstation 模式均不存在。
- `rust-core/src/dnp3.rs` 固定 DNP3 CRC16、链路 length/地址/control、每 16 user bytes CRC、Transport 分段、READ/CONFIRM、Class 0/1/2/3、IIN/Quality 和首批静态/事件/时间对象；`session.rs` 已接入 TCP 20000 只读 Master、solicited/unsolicited 响应、应用确认和错序断开。Select/Operate、输出、校时、Restart/Freeze、File、Secure Authentication、串口和 Outstation 模式均不存在。
- `rust-core/src/dlt645.rs` 分别固定 2007 四字节 DI/11H-91H-B1H-D1H 与 1997 两字节 DI/01H-81H-A1H-C1H，严格校验 BCD 地址、前导 FE、双 68H、长度、33H 变换、校验和、16H、响应方向/异常位、地址与 DI 回显；首批解释总/费率电量、日期/星期、时间和状态字，未知 DI 保留原始解码字节。广播读、后续帧拼接和所有写/控制命令均 fail-closed 或不存在。
- `rust-core/src/cjt188.rs` 固定 CJ/T 188-2004 单 68H、模 256 算术和、无 +33H、14 位 BCD 地址低位对先传、DI 高位在前、SER 回显、81H/C1H 响应和 901F 水/气表流量/状态；热量表 901F、C1H 异常载荷和未知 DI 保留原始字节。
- `rust-core/src/bacnet.rs` 固定 BACnet/IPv4 81H+0AH/0BH、本地 NPDU、Unconfirmed 10H、Who-Is 08H/I-Am 00H、上下文 [0]/[1] 范围和 `C4/22/91/21` 应用标签；ReadProperty 固定 DER NPDU、00H+03H+Invoke+0CH 请求、30H ComplexACK、context [0]/[1]/[2] 和 [3] 3EH/3FH 值包装，当前只解释 Unsigned/Real，未知应用标签保留原始字节。`session.rs` 新增显式 UDP 对端、0AH 定向 Who-Is 收包和 ReadProperty Invoke/回显校验；旧 Nexus.Bacnet 的错误 BVLC/标签/事务语义不进入实现。
- `rust-core/src/knx.rs` 固定 KNXnet/IP 公共头 06 10、Connect Request/Response、HPAI/CRI/CRD、三层组地址、Tunneling 连接头、GroupValueRead 的 L_Data.req、Tunneling ACK、GroupValueResponse 的 L_Data.ind、Connection State、Disconnect 和 APDU/APCI 长度边界；短值只归一化低 6 位，扩展值保留原始字节，不自动换算 DPT。`session.rs` 已接入显式 UDP 网关 Connect、双向 Sequence、读响应 ACK 回送、Connection State 状态错误释放会话、周期自动保活、显式重连、Disconnect；写组值与场景无命令。

## 本轮验证

- 目标协议串行回归：PPI/USS/RK512/HostLink C-mode/HostLink FINS/MEWTOCOL 共享 COM/串口 collector/Siemens route/Delta 共享 Modbus service/FATEK ASCII/Fuji SPH/GE SRTP 路由共 50/50 通过。
- npm run build：通过。
- npm run test:electron：当时该审计轮次快照 216/216；2026-08-23 测试优先协议补全复测为 278/278。当时覆盖 Allen-Bradley、Beckhoff、Keyence、LS XGT、Panasonic、Delta、Inovance、Xinje、FATEK、Fuji、GE SRTP、MQTT、IEC104、DNP3、DL/T 645、CJ/T 188、BACnet/IP、KNXnet/IP route/Golden Vector/共享串口服务，start/stop poll stream 双字段响应路由、脱敏诊断包、项目趋势/任务清单持久化、安全写入审计、只读会话恢复、发布元数据、发布说明/回滚，以及构建证据回归。
- `cargo check --tests --manifest-path rust-core/Cargo.toml`：通过（含新 JSONL 命令编译检查）。
- `npm run test:rust-jsonl`：通过，Rust sidecar JSONL E2E 89/89（既有 S7/FINS/USS/RK512/HostLink 28/28 + Allen-Bradley ENIP/CIP 3/3 + Beckhoff ADS/AMS 3/3 + Keyence KV Host Link 3/3 + LS XGT 3/3 + Panasonic MEWTOCOL 2/2 + Delta profile 2/2 + Inovance profile 2/2 + Xinje profile 2/2 + FATEK ASCII 3/3 + Fuji SPH 3/3 + GE SRTP 3/3 + MQTT 3/3 + IEC104 4/4 + DNP3 4/4 + DL/T 645 4/4 + CJ/T 188 3/3 + BACnet/IP 7/7 + KNXnet/IP 7/7），另有 MQTT Aedes/MQTT.js 独立 Broker 互操作 1/1、bacstack 0.0.1-beta.14 BACnet/IP 独立栈互操作 1/1 和 knx 2.5.4 KNXnet/IP 独立栈互通 1/1；脚本只在当前 PowerShell 进程补齐 `E:\VS Studio` 的 x64 MSVC 与 Windows SDK `LIB` 路径、构建普通 sidecar 并启动动态 loopback 对端，不修改系统环境变量或遗留后台服务。
- 完整 Rust 核心测试在相同临时 MSVC/Windows SDK `LIB` 环境下为 612/612（单元 446/446 + integration 166/166）。已修正两个过期 LS XGT 长度断言和 USS round-trip 测试直接复用请求帧、未置响应 ADR bit7/未重算 BCC 的问题；生产 fail-closed 语义未放宽。`cargo check --all-targets` 现为 0 warning。DL/T 645 单元测试 9/9；`cargo fmt --check`、Vite build 和 `npm audit`（0 漏洞）通过。
- `cargo test --lib fins_frame`：9/9 通过；`cargo test --lib fins_network_commands_fail_closed_on_batch_window_and_bit_values`：1/1 通过。
- `cargo test --lib fins_slave`：7/7 通过，覆盖虚拟 PLC 的超限批量、非法标志、多余尾字节和短请求错误响应。
- HostLink FINS 地址窗口回归：Electron 服务 6/6，Rust `hostlink_fins_command_keeps_area_codes_distinct_and_rejects_unsafe_input` 1/1。
- HostLink C-mode 回归：`cargo test --lib hostlink` 7/7、Electron HostLink 服务 6/6，覆盖 FCS/站号/头/结束码/数据边界与 DM 窗口。
- RK512 回归：`cargo test --lib rk512` 9/9、Electron RK512 服务 3/3，覆盖数量/偏移窗口、写数据长度、响应长度和 BCC。
- `electron/protocol-registry.test.cjs` 额外校验 `index.html` 的 Modbus 主站单选项、从站、三菱、西门子、欧姆龙选择器与注册表集合一致，并校验 Allen-Bradley/Beckhoff/Keyence/LS Electric/Delta/Panasonic/Inovance/Xinje/FATEK/Fuji/GE/MQTT/IEC104/DNP3/DL/T 645/CJ/T 188/BACnet/KNX 页面与帮助入口；当前 53 个可选变体均未被禁用。
- `rust-core/tests/enip_jsonl_e2e.rs`：Allen-Bradley EtherNet/IP/CIP JSONL 3/3 通过，覆盖 RegisterSession、TCP Read Tag、UnregisterSession、SendRRData CPF、ENIP 头解析、CIP 回复和畸形长度拒绝。
- `electron/enip-route.test.cjs`：Allen-Bradley 七个命令在 Rust client、preload 白名单和 main IPC forwarding 三处 1/1 对齐，并检查页面 TCP 只读入口。
- `rust-core/tests/ads_jsonl_e2e.rs`：Beckhoff ADS/AMS JSONL 3/3，覆盖 Read/ReadDeviceInfo/ReadState TCP 事务、Read/Write/ReadWrite 构帧、AMS/ADS 响应长度、InvokeId/Command 校验和非法 NetId 拒绝。
- `electron/ads-route.test.cjs` 与 `electron/ads-golden-vectors.test.cjs`：十一个 ADS 命令三层路由、页面 TCP 只读入口和 ADS/AMS 黄金向量文档通过后纳入 Electron 回归。
- `rust-core/tests/keyence_jsonl_e2e.rs`：Keyence KV Host Link JSONL 3/3，覆盖地址归一化、CR/CR NN、RDS/WRS/ST/RS 构帧、CRLF 响应和非法地址拒绝，并通过独立 TCP 对端验证 CC 握手、RDS 字/位读取和分片收帧。
- `electron/keyence-route.test.cjs` 与 `electron/keyence-golden-vectors.test.cjs`：`open_keyence_connection`、`keyence_read_words`、`keyence_read_bits` 和十个编解码命令三层路由、Keyence 页面和 ASCII 黄金向量文档纳入 Electron 回归。
- `rust-core/tests/ls_xgt_jsonl_e2e.rs`：LS XGT JSONL 3/3，覆盖显式地址、单变量/连续读写、20 字节头、响应数据边界和非法无类型地址拒绝，并通过独立 TCP 对端验证 application length 收帧和两次只读事务。
- `electron/ls-xgt-route.test.cjs` 与 `electron/ls-xgt-golden-vectors.test.cjs`：`open_ls_xgt_connection`、`ls_xgt_read`、`ls_xgt_read_continuous` 和六个编解码命令三层路由、XGT 页面和黄金向量文档纳入 Electron 回归。
- `rust-core/tests/panasonic_jsonl_e2e.rs`：Panasonic MEWTOCOL JSONL 2/2，覆盖地址、RD/WD/RCS 构帧、BCC/CR 响应和非法站号/地址拒绝。
- `electron/panasonic-route.test.cjs` 与 `electron/panasonic-golden-vectors.test.cjs`：七个 `panasonic_*` 编解码命令三层路由、`panasonic_serial_read` preload/main 路由和 MEWTOCOL ASCII 黄金向量文档纳入 Electron 回归。
- `electron/panasonic-serial-service.test.cjs` 与 `electron/serial-service.test.cjs`：RD/RCS 共享 COM 只读事务、设备记录、超时重试、MEWTOCOL CR 分包和完整 TX 回显剥离回归。
- `electron/inovance-route.test.cjs` 与 `electron/inovance-golden-vectors.test.cjs`：`inovance_parse_address` Rust client/preload/main/UI 路由和 H3U/H5U/AM 黄金边界纳入 Electron 回归；AM/AC/Easy 明确保持不支持。
- `rust-core/tests/inovance_jsonl_e2e.rs`：Inovance JSONL 2/2 通过，覆盖 H3U/H5U 地址、八进制点寻址、H3U 空洞/C200+ 宽度、H5U 上限、类型不匹配和 AM fail-closed。
- `electron/xinjie-route.test.cjs` 与 `electron/xinjie-golden-vectors.test.cjs`：`xinjie_parse_address` 三层路由、XC/XD 页面和 D 区确认/其他区域拒绝黄金文档纳入 Electron 回归。
- `rust-core/tests/xinjie_jsonl_e2e.rs`：Xinje JSONL 2/2 通过，覆盖 XC/XD D0/D100/D65535、底层 Modbus 标记和 X/M/HD 未确认区 fail-closed。
- `rust-core/tests/fatek_jsonl_e2e.rs`：FATEK JSONL 3/3 通过，覆盖 D/R 字区、X/M 离散区、站号/命令/数量边界、加和校验、响应解析和非法地址拒绝，并通过独立 TCP 对端验证 46 读、ETX 分帧和只读数据长度。
- `electron/fatek-route.test.cjs` 与 `electron/fatek-golden-vectors.test.cjs`：`open_fatek_connection`、`fatek_read_words`、`fatek_read_discrete` 和七个编解码命令三层路由、FATEK 页面和原生 ASCII 黄金向量文档纳入 Electron 回归。
- `rust-core/tests/fuji_sph_jsonl_e2e.rs`：Fuji SPH JSONL 3/3 通过，覆盖 M10/M1 地址、20 字节头、00H/01H、24 位小端地址、响应数据长度/回显、位写入拒绝，并通过独立 TCP 对端验证显式连接、分段响应和只读事务。
- `electron/fuji-sph-route.test.cjs` 与 `electron/fuji-sph-golden-vectors.test.cjs`：`open_fuji_sph_connection`、`fuji_sph_read` 和四个编解码命令三层路由、Fuji 页面和 SPH 二进制黄金向量文档纳入 Electron 回归。
- `rust-core/tests/ge_srtp_jsonl_e2e.rs`：GE SRTP JSONL 3/3 通过，覆盖 56 字节会话初始化、事务号、1-based 地址/数据码、读写报头、短/长响应、PLC 状态和坏事务/短帧拒绝，并通过独立 TCP 对端验证流式初始化、R100 只读事务和显式断开。
- `electron/ge-srtp-route.test.cjs` 与 `electron/ge-srtp-golden-vectors.test.cjs`：`open_ge_srtp_connection`、`ge_srtp_read` 和六个 `ge_srtp_*` 命令三层路由、GE 页面和 SRTP 56 字节黄金向量文档纳入 Electron 回归。
- `rust-core/tests/mqtt_jsonl_e2e.rs`：MQTT 3.1.1 JSONL 3/3 通过，覆盖 CONNECT/CONNACK、SUBSCRIBE/SUBACK、QoS 0 PUBLISH、PINGREQ/PINGRESP、DISCONNECT、畸形剩余长度/订阅包标识拒绝，并通过独立 TCP Broker 对端验证分片收帧和只读订阅事务。
- `scripts/mqtt-aedes-integration.test.cjs`：Aedes 1.1.1 Broker、MQTT.js 5.15.2 发布端与 Nexus Rust sidecar 三方互操作 1/1，通过动态 loopback TCP 端口覆盖 CONNECT、SUBSCRIBE、外部 PUBLISH、PUBLISH read、PING、DISCONNECT、匿名允许、Topic ACL 允许/拒绝和需认证连接拒绝。
- `electron/mqtt-route.test.cjs` 与 `electron/mqtt-golden-vectors.test.cjs`：十二个 MQTT 命令三层路由、MQTT 页面只读订阅入口和 MQTT 3.1.1 黄金向量文档纳入 Electron 回归。
- `rust-core/src/iec104.rs` 单元测试：7/7 通过，覆盖 I/S/U、序号、总召 ASDU、五类监视量、COT/Quality、畸形长度和非有限浮点拒绝。
- `rust-core/tests/iec104_jsonl_e2e.rs`：IEC104 JSONL 4/4 通过；独立脚本 Outstation 采用动态 loopback TCP 端口并分片发送 STARTDT_CON、ACT_CON、五类监视数据、ACT_TERM 和 TESTFR_CON，验证 Nexus 发出 7 个 S 确认和 best-effort STOPDT_ACT，并拒绝错误 N(S)。
- `electron/iec104-route.test.cjs` 与 `electron/iec104-golden-vectors.test.cjs`：九个 IEC104 命令三层路由、TCP 2404 只读总召页面、控制命令缺席和 APDU/ASDU 黄金向量文档纳入 Electron 回归。
- `rust-core/src/dnp3.rs` 单元测试：7/7 通过，覆盖 CRC、链路多块 CRC、Transport 错序、Class/对象 READ、静态/事件/时间对象和畸形响应拒绝。
- `rust-core/tests/dnp3_jsonl_e2e.rs`：DNP3 JSONL 4/4 通过；独立脚本 Outstation 自行实现 CRC、链路帧和 Transport，在动态 loopback TCP 端口验证完整性/Class 0/1/2/3、静态/事件/时间/IIN、TCP/Transport 分片、solicited/unsolicited CON 确认，并拒绝错误 Transport sequence 后清理会话。
- `electron/dnp3-route.test.cjs` 与 `electron/dnp3-golden-vectors.test.cjs`：十个 DNP3 命令三层路由、TCP 20000 只读 Master 页面、控制/校时/管理命令缺席和链路/Class/CONFIRM 黄金向量文档纳入 Electron 回归。
- `rust-core/src/dlt645.rs` 单元测试 9/9、`rust-core/tests/dlt645_jsonl_e2e.rs` 4/4：覆盖 2007/1997 精确读请求、BCD 地址、分版 DI/控制码、33H、完整校验和、电量/日期/时间/状态解释、表端异常、广播读/坏校验和/跨版本 DI 拒绝。
- `electron/dlt645-serial-service.test.cjs`、`electron/serial-service.test.cjs`、`electron/dlt645-route.test.cjs` 与 `electron/dlt645-golden-vectors.test.cjs`：覆盖共享 COM 2007/1997 读事务、精确回显剥离、按 L 收帧、有限重试、关闭串口/广播/后续帧 fail-closed、五命令三层路由及写/控制命令缺席。
- `rust-core/tests/ls_xgt_jsonl_e2e.rs`：LS XGT JSONL 3/3，覆盖显式地址、单变量/连续读写、20 字节头、响应数据边界和非法无类型地址拒绝，并通过独立 TCP 对端验证 application length 收帧和两次只读事务。
- `electron/ls-xgt-route.test.cjs` 与 `electron/ls-xgt-golden-vectors.test.cjs`：`open_ls_xgt_connection`、`ls_xgt_read`、`ls_xgt_read_continuous` 和六个编解码命令三层路由、XGT 页面和黄金向量文档纳入 Electron 回归。
- `docs/serial-golden-vectors.md` 固定 PPI/USS/RK512/HostLink 的软件 TX/RX 向量、校验和、串口格式待核对字段，并由 `electron/serial-golden-vectors.test.cjs` 检查关键边界未从文档消失。
- `electron/s7-webapi-service.cjs` 现在限制变量名控制字符/长度、JSON 响应体大小，厂商 RPC 错误保留结构化码；token 失效会清空会话。证书指纹、固件权限矩阵和真实 S7-1200/1500 仍未完成。
- `docs/fetchwrite-golden-vectors.md` 固定 Fetch/Write 16 字节头、读/写 OPC、成功响应精确长度和错误体边界；Rust 虚拟服务错误响应现在也回传正确的响应 OPC。
- HostLink 串口服务的响应站号现在要求严格两位十六进制且范围 0..31，C-mode/FINS 畸形站号在进入 Rust 解析前 fail-closed。

测试只证明软件路由和虚拟/回环行为，不证明真实 PPI、Fetch/Write 或 Web API PLC 已通过 L2。

## 未完成事项

- [ ] Protocol Registry 目前覆盖当前 53 个可选择变体；Allen-Bradley CIP、Beckhoff ADS/AMS、Keyence KV Host Link、LS XGT、Panasonic MEWTOCOL-COM、Delta DVP/AS profile、Inovance H3U/H5U profile、Xinje XC/XD D profile、FATEK FBs ASCII、Fuji SPH、GE SRTP、MQTT 3.1.1、IEC104、DNP3、DL/T 645-2007/1997、CJ/T 188-2004、BACnet/IP 和 KNXnet/IP 已登记，其他计划新增品牌协议仍未接入。
- [ ] DL/T 645 当前仅完成 2007/1997 单帧读数据 S2-S4a；真实表计/USB-RS485 电气层、厂家 DI、异常/后续帧组合、唤醒静默间隔、8/24/72 小时和 L2 仍未完成，写表、校时、拉合闸、清零继续禁用。
- [ ] CJ/T 188 当前仅完成离线只读编解码 S1-S3；真实水/气/热表、FE 唤醒时序、后续帧、厂家 DI、异常码表和 L2 仍未完成，写数据/写地址/阀门控制继续禁用。
- [ ] BACnet/IP 当前完成离线编解码 S1-S3、独立脚本 UDP 对端 S4a 和 bacstack 0.0.1-beta.14 独立栈互操作 S4b；ReadPropertyMultiple、错误/拒绝/中止、分段、COV、BBMD/FDR 和真实楼宇设备 L2 仍未完成，写入/订阅/设备管理继续禁用。
- [ ] KNXnet/IP 当前完成 Tunneling v1 离线编解码、独立脚本 UDP 网关、Connection State 状态错误释放、周期自动保活、显式重连和 knx 2.5.4 独立栈互通 S4b；长稳、Routing、Device Management、Secure 和真实 Interface/Router L2 仍未完成，写组值/场景/设备管理继续禁用。
- [ ] Allen-Bradley CIP 下一轮仍缺 TCP 44818 会话、ListIdentity、Unconnected Send 路由、真实设备 L2；写入、Connected CIP、PCCC、DF1、CIP Safety、Implicit I/O 仍未开放。
- [ ] PPI 原生 COM 软件双拍已实现；仍缺真实 S7-200/SMART、拔插、回显/NAK/令牌和 L2。
- [ ] PPI TCP 透传、Fetch/Write 和 Web API 尚未完成真实设备 L2。
- [ ] Web API 当前只完成服务层输入/响应安全门禁；证书指纹、权限矩阵、变量类型矩阵和真实设备 L2 仍未完成。
- [ ] USS 参数只读和 RK512 指定区只读在线串口已接入；USS 写/控制、RK512 写/冲突重试与真实 L2 仍未完成。
- [ ] HostLink C-mode RR 读 DM 软件首轮已接入；HostLink FINS、写入、CIO/WR/HR/AR、型号矩阵和真实串口 L2 仍未完成。
- [ ] HostLink FINS 0101 字读取软件首轮已接入；位读取、写入、完整服务集、型号矩阵和真实串口 L2 仍未完成。
- [ ] FINS OMRON-008 尚未完成：当前只有 512 点传输安全上限和地址窗口 fail-closed，尚无按 CPU/区类型的设备限制自动分段。
- [ ] 现有 route 表尚未成为所有协议页面的统一注册表；Beckhoff ADS/AMS 仍缺真实 TCP/AMS Route/符号句柄生命周期，Keyence 软件 TCP 只读会话已接入但仍缺真实 KV 型号/固件、Host Link 设置/抓包/L2 和 MC Compatible 独立路径，LS XGT 仍缺真实 TCP/型号矩阵和 XG5000 L2，Panasonic 共享 COM 软件只读已接入但仍缺真实 FP 型号、电气层和串口 L2，Delta 共享 Modbus 只读软件边界已接入但仍缺真实型号/固件和 L2，Inovance H3U/H5U profile 仍缺真实 Modbus RTU/TCP 型号/固件比对和 L2；Xinje XC/XD 目前仅 D 区确认，仍缺真实型号/固件、其他区域手册和 Modbus L2；FATEK 软件 TCP 只读会话已接入，但仍缺 FBs 型号/固件矩阵、串口/Gateway 行为差异和真实 L2；Fuji 软件 TCP 只读会话已接入，但仍缺 SPH CPU/固件/以太网模块、Loader 抓包、230 字分帧和真实 L2；GE 已接入软件 TCP 18245 只读会话，但仍缺 Series 90/PACSystems 型号/固件、抓包互验、最大引用表、非字节对齐位写入和真实 PLC L2；MQTT 已完成 Aedes/MQTT.js 独立 Broker 互操作，但仍缺生产 Mosquitto/EMQX、用户名密码/证书正向认证、TLS、复杂 ACL、QoS 1/2 在线语义、Sparkplug B 状态机和现场 L2；IEC104 已完成 TCP 2404 只读总召脚本闭环，但仍缺 CP24/CP56Time2a、完整 t0/t1/t2/t3、k/w 窗口、异步自发上送、自动重连、生产模拟器、真实 RTU/IED 和长稳 L2，遥控/设点/校时继续禁用；DNP3 已完成 TCP 20000 只读完整性/Class/对象读取脚本闭环，但维护且许可兼容的生产栈、第三方 Outstation、一致性、time sync、Secure Authentication、串口、控制门禁、真实 RTU/IED、自动重连和长稳 L2 仍未完成；AM/AC/Easy 仍无可确认的统一地址表。
- [ ] 默认 shell 直接执行 `cargo test` 仍可能因未设置 `LIB` 报 `msvcrt.lib`；使用 `npm run test:rust-jsonl` 可复现当前 E2E。
- [ ] 黄金帧仍是软件/虚拟证据；真实设备的串口格式、型号、帧间隔、回显和 L2 记录尚未填入。
- [ ] 实机、现场和 8/24/72 小时验证尚未执行。

## 下一条工作边界

测试优先三族（Modbus / MELSEC / Siemens）的软件路由、Golden Vector 和联合门禁已按 `docs/test-first-protocol-handoff.md` 收口。下一轮这三族只推进真实设备 L2（型号/固件/接线/厂家工具同地址比对/抓包），不得把虚拟从站或脚本对端写成现场 PASS，也不得默认开放 S7 写入、MC RUN/STOP/RESET 或 C24 写。楼宇协议不再扩展软件功能边界。Rust JSONL 仍用 `scripts/test-rust-jsonl.ps1` 固定 `LIB`。
