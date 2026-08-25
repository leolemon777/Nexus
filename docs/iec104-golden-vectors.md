# IEC 60870-5-104 Golden Vectors（TCP 只读 Client/Master）

本文件固定 Nexus 首轮 IEC 60870-5-104 边界：TCP Client/Master、严格 I/S/U APDU、STARTDT/STOPDT/TESTFR、站/组总召、发送/接收序号、Cause of Transmission、Quality Descriptor，以及五类无时标监视方向 ASDU。首轮不提供遥控、双点遥控、设点、校时、文件传输、站端模式、TLS 或 IEC 62351。

`rust-core/tests/iec104_jsonl_e2e.rs` 使用独立脚本 Outstation 在动态 loopback TCP 端口执行完整往返；它证明软件帧和会话边界，不是 IEC 一致性认证，也不等于真实 RTU/IED、生产网络或 24/72 小时 L2。真实站端仍为 **L2 pending**。

## U 帧链路功能

```text
68 04 07 00 00 00    # STARTDT_ACT
68 04 0B 00 00 00    # STARTDT_CON
68 04 13 00 00 00    # STOPDT_ACT
68 04 23 00 00 00    # STOPDT_CON
68 04 43 00 00 00    # TESTFR_ACT
68 04 83 00 00 00    # TESTFR_CON
```

U 帧必须恰好 6 字节，长度字段为 `04`，后三个控制字节为零，功能控制字节只能是上述六个值。

## 站总召请求

公共地址 CA=`1`、发起方 OA=`0`、组=`0`、QOI=`20`、初始 N(S)=0/N(R)=0：

```text
68 0E 00 00 00 00 64 01 06 00 01 00 00 00 00 14
```

ASDU 部分：

```text
64 01 06 00 01 00 00 00 00 14
```

- Type ID `100` = `C_IC_NA_1`。
- VSQ `01` = 单对象、非顺序地址。
- COT `06` = Activation。
- CA 使用 2 字节 little-endian；IOA 固定为 0。
- QOI `20` 为站总召；组 1..16 映射 QOI 21..36。

## ACT_CON、S 确认和 ACT_TERM

站端以 N(S)=0、N(R)=1 返回肯定 ACT_CON：

```text
68 0E 00 00 02 00 64 01 07 00 01 00 00 00 00 14
```

主站收到后立即把本地 N(R) 推进到 1 并发送：

```text
68 04 01 00 02 00
```

全部监视数据结束后，站端以 N(S)=6、N(R)=1 返回 ACT_TERM：

```text
68 0E 0C 00 02 00 64 01 0A 00 01 00 00 00 00 14
```

总召只有在 `ACT_CON → 监视 ASDUs → ACT_TERM` 完整结束后才返回成功。否定确认、CA/QOI 不一致、N(S) 跳号、N(R) 未确认、零对象、截断、尾随或未知 Type ID 都必须失败。

## 首批监视 ASDU

固定单点样本，CA=1、IOA=42、COT=20、值=true、Quality.Invalid=true：

```text
01 01 14 00 01 00 2A 00 00 81
```

首批解码表：

| Type ID | 名称 | 对象数据 | 值解释 | 质量 |
|---:|---|---:|---|---|
| 1 | M_SP_NA_1 | 1 byte | SIQ bit 0 | BL/SB/NT/IV |
| 3 | M_DP_NA_1 | 1 byte | DIQ bits 0..1：中间/关/开/不确定 | BL/SB/NT/IV |
| 9 | M_ME_NA_1 | 3 bytes | little-endian i16 / 32768 | OV/BL/SB/NT/IV |
| 13 | M_ME_NC_1 | 5 bytes | little-endian IEEE-754 f32；NaN/Inf 拒绝 | OV/BL/SB/NT/IV |
| 15 | M_IT_NA_1 | 5 bytes | little-endian i32 + BCR | SQ/CY/CA/IV |

Quality 位：`OV=0x01`、`BL=0x10`、`SB=0x20`、`NT=0x40`、`IV=0x80`。SIQ/DIQ 的低位是值/状态，不得误判为 Overflow；BCR 的低 5 位是序号，`0x20/0x40/0x80` 分别为 Carry/Adjusted/Invalid。

## 软件交叉验证与未完成门禁

- `rust-core/src/iec104.rs`：严格 APCI/ASDU 编解码、总召构造、质量与五类监视数据解码。
- `rust-core/src/session.rs`：TCP 2404、STARTDT、TESTFR、总召状态顺序、逐帧 S 确认和 best-effort STOPDT。
- `rust-core/tests/iec104_jsonl_e2e.rs`：4/4，含分片 TCP、五类点、错序拒绝和完整关闭。
- `electron/iec104-route.test.cjs`：验证 Rust client、preload/main allow-list、页面只读入口和控制命令缺席。
- 语义交叉核对参考 MZ Automation 的 lib60870 官方 Client/Master 文档与示例；其文档确认默认 TCP 2404、STARTDT 以及 QOI 20 总召的 ACT_CON/数据/ACT_TERM 流程。
- 未完成：真实 RTU/IED 或生产模拟器、冗余连接、k/w 窗口与完整 t0/t1/t2/t3 定时器、CP24/CP56Time2a 类型、TLS/IEC 62351、长稳与一致性实验室测试。
