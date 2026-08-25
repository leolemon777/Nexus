# S5 Fetch/Write 软件黄金帧

日期：2026-08-22  
证据等级：S2/S3（Rust 编解码、虚拟 Fetch/Write 服务和 JSONL E2E）；尚未等同真实 CP343/CP443 或 S5 L2。

Fetch/Write 使用 CP/NetPro 配置的裸 TCP 用户端口（常见示例 2000），不能把 S7comm 的 102 端口当作 Fetch/Write。以下帧固定的是软件协议边界，现场仍需按实际 CP 工程核对端口、ORG 区码、地址和返回码。

## 1. 16 字节头布局

```text
53 35 10 | 01 03 OPC | 03 08 | ORG DBN | ADDRESS(2B BE) | LENGTH(2B BE) | FF 02
```

| 字段 | 说明 |
|---|---|
| `53 35 10` | S5 固定头 |
| `01 03` | 作业/版本固定字段 |
| `OPC` | `05` Fetch 请求、`06` Fetch 成功响应、`03` Write 请求、`04` Write 成功响应 |
| `03 08` | 传输类型固定字段 |
| `ORG` | `01` DB、`02` M、`03` I、`04` Q、`06` C、`07` T |
| `DBN` | DB 号；非 DB 区通常为 0 |
| `ADDRESS` | 大端字节地址 |
| `LENGTH` | 大端字节数量 |
| `FF 02` | 固定尾字段 |

## 2. Fetch 读 DB1 地址 0、2 字节

```text
TX Fetch request
53 35 10 01 03 05 03 08 01 01 00 00 00 02 FF 02

RX Fetch success（数据 AA BB）
53 35 10 01 03 06 03 08 00 01 00 00 00 02 FF 02 AA BB
```

成功 Fetch 响应必须满足：OPC=`06`、错误号（响应字节 8）=`00`、总长度正好 `16 + LENGTH`，并返回恰好 2 个数据字节。短帧、尾随数据、固定字段或 OPC 不匹配都必须拒绝。

## 3. Write 写 M50 两字节

```text
TX Write request（数据 CA FE）
53 35 10 01 03 03 03 08 02 00 00 32 00 02 FF 02 CA FE

RX Write success（无数据体）
53 35 10 01 03 04 03 08 00 00 00 32 00 02 FF 02
```

成功 Write 响应必须满足：OPC=`04`、错误号=`00`、长度固定为 16 字节且没有数据体。写请求的实际数据长度必须等于头部 `LENGTH`，不允许缺失或尾随数据。

## 4. 错误响应与安全边界

- 错误号非零时保留原始错误号；错误响应仍不得包含额外数据。
- `Fetch` 与 `Write` 的 OPC 不可互换；不能因为地址或数据看起来合理就接受错误作业类型。
- ORG、DB、地址和长度在 UI/Electron/Rust 三层分别校验；未批准的控制区或批量范围保持禁用。
- 端口由 NetPro/CP 工程决定；软件文档中的 2000 只是常见示例，不是设备默认值。
- 软件黄金帧和虚拟服务通过只证明 S2/S3；真实 CP 返回码、连接资源、断线恢复和 8/24/72 小时长稳仍需 L2 记录。

## 5. 关联测试

- `rust-core/src/s7_fetchwrite.rs`：固定头、请求 OPC、成功长度、错误体长度和读写内存回环。
- `rust-core/src/session.rs`：Fetch 响应 OPC/数据长度、Write 响应 OPC/无数据体和错误号处理。
- `rust-core/tests/s7_jsonl_e2e.rs`：`s7_e2e_fetch_write_flow` 通过 sidecar/虚拟服务验证读写闭环。

