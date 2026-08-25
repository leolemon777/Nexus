# DNP3 Golden Vectors（TCP 只读 Master）

本文件固定 Nexus 首轮 DNP3 边界：TCP 20000 Master、严格数据链路 CRC、Transport 分段、应用层 READ/CONFIRM、完整性轮询、Class 0/1/2/3、IIN、Quality，以及首批静态值和变化事件对象。首轮不提供 Select、Operate、Direct Operate、Analog Output、Time Write、Restart、Freeze、File、Secure Authentication、串口 DNP3 或 Outstation 模式。

`rust-core/tests/dnp3_jsonl_e2e.rs` 使用独立实现 CRC、链路帧和 Transport 分片的脚本 Outstation，在动态 loopback TCP 端口执行完整往返。它证明 Nexus 的软件编解码和 TCP 会话边界，不是 DNP Users Group 一致性认证，也不等于第三方生产栈、真实 RTU/IED、生产网络或 8/24/72 小时验证。真实站端仍为 **L2 pending**。

## 旧资产与栈选择边界

- 旧 `Nexus.Dnp3` 与主仓库均为 MIT，可作为已审计迁移参考；旧实现已有链路 CRC、Transport、静态对象、控制构造器和脚本 TCP Outstation，但没有 Class 扫描/事件/校时/第三方互操作/真实设备证据。
- 当前维护中的 Step Function Rust DNP3 栈支持 TCP/TLS/串口与较广对象模型，但其公开许可证明确限制为非商业、非生产使用；因此本 MIT 产品没有直接引入它。
- Apache-2.0 的 OpenDNP3 已归档，不能作为“当前维护中的生产栈”直接满足选型门禁。
- 本轮采用边界清晰的 MIT 自有只读实现并保持 S2-S4a；“维护中、许可兼容、经过互操作验证的生产栈”仍是后续选型/采购门禁。

## 数据链路与 CRC

DNP3 链路帧以 `05 64` 开始。地址采用 little-endian；10 字节头部末尾带 CRC，之后每 16 个 user-data 字节带一个 CRC。CRC 使用反射多项式 `0xA6BC`、初值 `0x0000`、最终按位取反，并在报文中 little-endian 存放。

无 user-data 的固定头部样本：

```text
05 64 05 C0 01 00 00 04 E9 21
```

- `length=05`：从 Link Control 到 Source Address 共 5 字节，无 user-data。
- `control=C0`，Destination=`0001H`，Source=`0400H`。
- Header CRC=`21E9H`，线上顺序为 `E9 21`。

Master Link Address=`1`、Outstation Link Address=`1024`、完整性请求的完整链路帧：

```text
05 64 14 C4 00 04 01 00 E9 B6 C0 C0 01 3C 01 06 3C 02 06 3C 03 06 3C 04 06 9C 09
```

- Master 发出的 Unconfirmed User Data control 固定为 `C4`；首轮只接受 Outstation control `44`。
- Destination=`0400H`、Source=`0001H`。
- Transport Header=`C0`，表示 FIR=1、FIN=1、sequence=0。
- 任何同步字、length、头 CRC、16-byte block CRC、地址、control 或尾随字节不匹配都必须 fail-closed。

## 完整性轮询与对象范围 READ

Application sequence=0 的 Class 0/1/2/3 完整性 READ：

```text
C0 01 3C 01 06 3C 02 06 3C 03 06 3C 04 06
```

- Application Control `C0`：FIR=1、FIN=1、sequence=0。
- Function `01`：READ。
- `3C 01/02/03/04 06`：Group 60 Variation 1/2/3/4，qualifier `06`（all objects）。

Application sequence=3，读取 Group 30 Variation 5，index `0x1234..0x1235`：

```text
C3 01 1E 05 01 34 12 35 12
```

- Qualifier `01` 固定 16-bit start/stop，index 使用 little-endian。
- `start > stop`、只提供一侧范围、保留地址、空 Class 请求以及超出软件对象/帧限制均拒绝。

## 应用 CONFIRM 与响应序号

Solicited response sequence=0 的应用 CONFIRM：

```text
C0 00
```

Unsolicited response sequence=7 的应用 CONFIRM：

```text
D7 00
```

Nexus 只在响应设置 CON 时自动确认；CONFIRM 的 UNS 位必须与被确认响应一致。Transport 使用 6 位 sequence，Application 使用 4 位 sequence，FIR/FIN、序号、solicited/unsolicited 分片必须连续。错序、重复 FIR、自发/请求响应混线或在软件帧数上限内没有 FIN 都失败并按错误类型关闭会话。

## 首批响应对象、Quality 和时间

首批只读解析范围：

| Group | 含义 | 首批 variation/值 | Quality / 时间 |
|---:|---|---|---|
| 1 / 2 | Binary Input 静态/事件 | packed 或带 Flags 的 bool | ONLINE/RESTART/COMM_LOST/REMOTE_FORCED/LOCAL_FORCED/CHATTER_FILTER；事件可带绝对时间 |
| 3 / 4 | Double-bit Binary 静态/事件 | 2-bit 状态 | 同上；事件可带绝对时间 |
| 10 / 11 | Binary Output Status 静态/事件 | bool | 同上；事件可带绝对时间 |
| 20 / 22 | Counter 静态/事件 | u16/u32 | DISCONTINUITY/ROLLOVER 等 Flags；事件可带绝对时间 |
| 21 / 23 | Frozen Counter 静态/事件 | u16/u32 | Flags；支持首批绝对时间 variation |
| 30 / 32 | Analog Input 静态/事件 | i16/i32/f32/f64 | OVER_RANGE/REFERENCE_ERR；拒绝 NaN/Inf；事件可带绝对/相对时间 |
| 40 / 42 | Analog Output Status 静态/事件 | i16/i32/f32/f64 只读状态 | 只解析状态，不提供输出命令；拒绝 NaN/Inf |

绝对时间按 48-bit little-endian Unix epoch 毫秒解析；相对时间只在同一响应已有有效 common time 时合成，否则拒绝。当前只覆盖明确列出的 variation，不把未知 group/variation 猜成数据。

IIN 会逐位保留和解释。`FUNC_NOT_SUPPORTED`、`OBJECT_UNKNOWN`、`PARAM_ERROR` 等请求错误位使当前事务失败；`DEVICE_RESTART`、Class event available、need time 等状态仍返回给上层，不会被悄悄清除。Quality 不良点会保留 flags，不能当成普通有效值。

## 软件交叉验证与未完成门禁

- `rust-core/src/dnp3.rs`：CRC、严格链路帧、Transport、READ/CONFIRM、IIN/Quality、静态/事件对象和时间解析。
- `rust-core/src/session.rs`：TCP 20000 只读 Master、完整性/Class/对象读取、自动应用确认、自发响应收集、序号和地址校验。
- `rust-core/tests/dnp3_jsonl_e2e.rs`：4/4，含独立 CRC/链路实现、TCP 分片、Transport 分段、Class 0/1/2/3、静态/事件/时间/IIN、应用确认、自发响应和错序断开。
- `electron/dnp3-route.test.cjs`：验证十个命令穿过 Rust client、preload/main 最小白名单，页面只提供读取和解析，控制/校时/重启/冻结命令缺席。
- `electron/dnp3-golden-vectors.test.cjs`：把上述固定帧、只读范围和 L2 证据边界纳入桌面回归。
- 未完成：许可兼容且维护中的生产栈或商业授权、第三方 Outstation 互操作矩阵、一致性测试、默认 variation/Class assignment 全矩阵、完整 time sync、Secure Authentication、TLS、串口、Outstation、控制安全门、真实 RTU/IED、断线恢复和 8/24/72 小时长稳。

