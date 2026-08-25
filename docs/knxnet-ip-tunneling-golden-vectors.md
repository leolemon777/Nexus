# KNXnet/IP Tunneling v1 黄金帧与审计边界

## 1. 证据链与旧实现判定

| 来源 | 结论 | 本轮使用方式 |
|---|---|---|
| 旧 `Nexus.Knx` 审计 | 公开头曾写成 `10 00`，缺 Header Length 06H；无 Connect/Channel/Sequence/Tunneling ACK；把 ACK 当数据响应；cEMI/APCI 错误 | 作为反面清单，不移植旧公开接口 |
| 重写后的 C# `Nexus.Knx` 审计与测试 | 非安全 UDP/IPv4 Tunneling v1：Connect、Channel、双向 Sequence、ACK、Connection State、Disconnect、cEMI GroupValue 读写 | 本轮只移植其离线只读帧语义，不移植写和在线会话 |
| KNX Association Connection Manager | KNXnet/IP Tunneling 默认 UDP 3671；区分 Tunneling、Routing、Secure 和 NAT 模式 | 锁定端口和产品边界，不宣称认证或 Secure 支持 |

旧实现问题不可通过补几个字节修复；本轮重新从帧语义实现 Rust 编解码。

## 2. 首批黄金帧

### 2.1 Connect Request

本机 `127.0.0.1:50000`：

```text
06 10 02 05 00 1A
08 01 7F 00 00 01 C3 50
08 01 7F 00 00 01 C3 50
04 04 02 00
```

字段：`06 10` 公共头；`02 05` CONNECT_REQUEST；`00 1A` 总长 26；两段 HPAI 均为 UDP/IPv4 `08 01 + IPv4 + port`；CRI 为 `04 04 02 00`，表示 Tunneling link-layer。

### 2.2 Connect Response

```text
06 10 02 06 00 14 15 00
08 01 7F 00 00 01 C3 50
04 04 11 01
```

字段：Channel=15H、Status=00；数据端点 127.0.0.1:50000；CRD 中个体地址为 11 01。Channel 0 或非 0 Status 在后续在线边界必须拒绝。

### 2.3 GroupValueRead（1/2/3，Channel 15H，Sequence 0）

```text
06 10 04 20 00 15
04 15 00 00
11 00 BC E0 00 00 0A 03 01 00 00
```

字段：`04 20` TUNNELING_REQUEST；连接头 structure-length=04、channel=15H、sequence=00、reserved=00；cEMI `11H` L_Data.req、无附加信息、`BC E0` 控制字段、组地址 `0A03`，APDU 长度 1 + `00 00` GroupValueRead。

### 2.4 Tunneling ACK

```text
06 10 04 21 00 0A 04 15 00 00
```

字段：`04 21` TUNNELING_ACK；总长 10；structure-length=04；channel=15H；sequence=00；status=00。

### 2.5 GroupValueResponse 与建议 ACK

扩展值 `12 34`：

```text
06 10 04 20 00 17
04 15 00 00
29 00 BC E0 11 01 0A 03 03 00 40 12 34
```

字段：`29H` L_Data.ind；source individual address `11 01`；destination group `1/2/3`；APDU 长度字段 03 表示 APDU 实际 4 字节；`00 40` APCI=GroupValueResponse；payload 为 `12 34`。解析后建议回上面的 Tunneling ACK，status=00。

若 APDU 实际 2 字节，低 6 位是内嵌小值，输出 normalized value；其他 DPT 不自动换算。

### 2.6 独立栈互通向量

`scripts/knx-independent-stack-integration.test.cjs` 使用 MIT 许可的 `knx@2.5.4` 作为第二实现，不运行其完整客户端状态机，而是直接使用其 `KnxProtocol` 解析 Rust 发出的 UDP 载荷并编码对端响应：

- Rust Connect Request 26 字节；对端用 knx 编码 20 字节 Connect Response，Channel=15H，数据端点为动态 loopback HPAI。
- Rust 1/2/3 GroupValueRead 21 字节；对端用 knx 解码 `L_Data.req + GroupValue_Read`，编码 10 字节 Tunneling ACK 和 23 字节 `L_Data.ind + GroupValue_Response + 12 34`。
- Rust 收到响应后回送 10 字节 ACK；对端用 knx 解码 Channel/Sequence/status。
- Rust Disconnect Request 16 字节；对端用 knx 解码后返回 8 字节 Disconnect Response。因 knx 2.5.4 高层 writer 未枚举 Disconnect Response，该 8 字节响应由公共头加同一 `ConnState` 编码器生成，仍不使用 Rust 生产编解码。

测试固定校验 Connect 两段 HPAI/CRI、Tunneling Channel/Sequence/cEMI/APCI、响应 ACK 和 Disconnect HPAI；当前 1/1 通过，纳入 `scripts/test-rust-jsonl.ps1`。

## 3. 拒绝边界

- 公共头不是 `06 10`、服务类型不符、总长度不等于 UDP 载荷长度。
- Connect Response 不是 20 字节、HPAI 不是 `08 01`、CRD 不是 `04 04`。
- Tunneling Request 连接头不是 04、reserved 非 00、cEMI 附加信息长度导致越界、APDU 声明长度不符。
- 组地址不是三层、main>31、middle>7 或 sub>255。
- GroupValueResponse 不是 `29H + APCI Response`；Tunneling ACK 不是 10 字节。
- 旧 `10 00` 头立即返回 `KNX_HEADER_INVALID`。

## 4. 安全与成熟度边界

- 当前命令包括八个离线编解码命令和七个 UDP 只读会话命令：`open_knx_connection`、`knx_group_read`、`knx_connection_state`、`knx_start_keepalive`、`knx_stop_keepalive`、`knx_keepalive_status`、`knx_disconnect`。
- 在线 Connect 固定与显式网关完成 Request/Response；读取固定为 GroupValueRead → 网关 ACK → GroupValueResponse → 客户端 ACK，并核对 Channel、双向 Sequence 和组地址。Connection State 使用 Request/Response 校验通道和状态，连续超时或非零 status 会释放本地会话，随后可通过新的 Connect 显式重连。Disconnect 发送 Request 并核对 Response。
- 独立脚本网关已覆盖：状态响应 `status=29H` → `KNX_CONNECTION_STATE_STATUS` → 会话释放 → 重新 Connect 得到新 Channel → Connection State `status=00`。
- 独立脚本网关已覆盖周期保活：启动定时 Connection State → 网关无响应 → 本地会话释放 → 后续读取返回 `CONNECTION_NOT_FOUND` → 显式重新 Connect 得到新 Channel。没有 `knx_auto_reconnect` 或隐式恢复副作用。
- 独立栈互操作已覆盖 knx 2.5.4 编解码器的 Connect、GroupValueRead、Tunneling ACK/Response ACK 和 Disconnect；其完整客户端状态机、Secure、Routing、NAT mode 和真实 Interface/Router 未纳入。
- 不实现 KNXnet/IP Routing、Discovery、Device Management、KNX IP Secure、IPv6、NAT mode、自动 DPT、ETS keyring、场景控制或设备管理。
- 成熟度：软件审计 + 离线编解码 + 独立脚本 UDP 网关、手动/周期 Connection State、显式重连与 knx 独立栈互通 S1-S4b。真实 Interface/Router、Secure、Routing、抓包比对和长稳均为 L2 pending。软件对端通过不得记为现场 PASS。
