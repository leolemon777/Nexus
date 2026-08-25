# Keyence KV Host Link ASCII Golden Vectors（TCP 只读软件边界）

本文件固定 Nexus 首轮 Keyence KV Host Link over TCP 的协议边界。向量和独立 TCP 对端只证明 ASCII 编解码、握手、只读收发与 Rust/Electron 路由，不证明具体 KV 型号、固件、Host Link 设置或真实 PLC L2。

## 固定连接上下文

| 字段 | 值 |
|---|---|
| TCP 端口 | `8501`（常用默认值） |
| 握手（带站号） | `CR 02\r` |
| 握手响应 | `CC\r\n` |
| 文本编码 | ASCII |
| 请求结束 | `CR` |
| 响应结束 | `CRLF` |
| 单次数量 | 1..256 |

## 请求向量

| 语义 | ASCII | HEX |
|---|---|---|
| 带站号握手 | `CR 02\r` | `43 52 20 30 32 0D` |
| DM 字读取 | `RDS DM0.U 2\r` | `52 44 53 20 44 4D 30 2E 55 20 32 0D` |
| MR 位读取 | `RDS MR1001 3\r` | `52 44 53 20 4D 52 31 30 30 31 20 33 0D` |
| DM 字写入 | `WRS DM0.U 2 1 65535\r` | `57 52 53 20 44 4D 30 2E 55 20 32 20 31 20 36 35 35 33 35 0D` |
| MR 位置 ON | `ST MR1001\r` | `53 54 20 4D 52 31 30 30 31 0D` |

地址归一化示例：`MR10.1` 转换为 `MR1001`；基础 `R0.1` 转换为无设备前缀的 `1`。`W/B/VB` 偏移使用十六进制；DM/EM/FM/ZF/TM/CM/VM/Z 等字设备使用十进制。

## 响应向量

```text
CC\r\n
1 65535\r\n
0 1 0\r\n
OK\r\n
E2\r\n
```

`E0/E1/E2/E4/E5/E6` 保留原始设备错误码，分别映射为未定义命令、命令格式错误、设备/地址错误、写保护、PLC 忙和不支持的命令。未知码仍保留原始值并显示为 PLC 错误。

## 必须拒绝的输入

1. 字地址带位后缀、位地址走字命令、W/B/VB 偏移含点或非十六进制。
2. 数量为 0 或超过 256、字值超出 `0..65535`、位值不是 `0/1`。
3. 响应缺少 CRLF、含多行/非 ASCII、返回字/位数量不匹配或写响应不是 `OK`。
4. `E*` 设备错误被误当作数据；地址不能含控制字符或超长文本。

## TCP 只读会话边界（S4a）

- `open_keyence_connection` 连接 TCP 8501，发送 `CR\r` 或 `CR NN\r`，只接受严格的 `CC\r\n` 握手响应；握手失败不会把 socket 放入会话表。
- `keyence_read_words` 发送 `RDS ...\r`，按 CRLF 收取完整响应并校验字数；`keyence_read_bits` 同样校验每个返回值只能是 `0/1`。
- 收帧采用有上限的逐字节 CRLF collector，可处理 TCP 分片；缺少 CRLF、超长、非 ASCII、设备错误或数量不匹配均 fail-closed。
- live path 只暴露连接、断开、RDS 字读和 RDS 位读；WRS、ST/RS、RUN/STOP、监视订阅、自动重连和真实设备控制不进入该边界。
- `rust-core/tests/keyence_jsonl_e2e.rs` 的独立 TCP 对端只模拟握手和只读响应；它不能替代 KV-7000/8000/KV-Nano 型号、固件、KV STUDIO 同地址回读或现场 L2。

## 证据边界

- `rust-core/src/keyence.rs` 保持 KV Host Link ASCII 编解码层；`rust-core/src/session.rs` 只增加握手后 RDS 只读 TCP 会话。MC Compatible、EtherNet/IP/CIP、串口 Host Link、PLC 运行控制、监视订阅和虚拟服务器仍是独立后续边界。
- `open_keyence_connection`、`keyence_read_words`、`keyence_read_bits` 与 `keyence_build_*` / `keyence_parse_*` 通过 Rust JSONL、Electron bridge 和 Keyence UI 暴露；页面明确标注 TCP 只读和证据等级。
- 软件向量/独立 TCP 对端通过只能标记 S2-S4a；KV-7000/8000、KV-Nano 或实际目标机型的设备侧配置、KV STUDIO 对值、抓包、重复读和长稳必须另行记录为 L2。
