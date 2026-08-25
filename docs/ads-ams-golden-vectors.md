# Beckhoff ADS/AMS Golden Vectors（软件首轮）

本文件固定 Nexus 首轮 ADS/AMS over TCP 的协议边界。向量和独立 TCP 对端只证明 Rust 编解码、TCP 只读会话和 JSONL/UI 路由，不证明 TwinCAT Router、AMS Route、PLC 型号或现场写入权限。

## 固定请求上下文

| 字段 | 值 |
|---|---|
| Target AMS NetId | `5.72.144.1.1.1` |
| Target AMS Port | `851`（TwinCAT 3 PLC 常见值） |
| Source AMS NetId | `5.72.144.2.1.1` |
| Source AMS Port | `32905` |
| InvokeId | `1` |
| ADS/TCP reserved | `0x0000` |
| AMS StateFlags | 请求 `0x0004`；响应 `0x0005` |

## ADS Read 请求

参数：`IndexGroup=0x0000F000`、`IndexOffset=0x00000020`、`ReadLength=4`。

```text
00 00 2C 00 00 00
05 48 90 01 01 01 53 03
05 48 90 02 01 01 89 80
02 00 04 00 0C 00 00 00 00 00 00 00 01 00 00 00
00 F0 00 00 20 00 00 00 04 00 00 00
```

解释：AMS/TCP 长度为 `44`，等于 32 字节 AMS 头加 12 字节 ADS Read payload。ADS payload 为 `IndexGroup`、`IndexOffset`、`ReadLength` 三个 little-endian u32。

## ADS Read 成功响应

返回数据为 `11 22 33 44`，响应端点按 AMS 规则交换源/目标 NetId 与 Port，InvokeId 保持 `1`。

```text
00 00 2C 00 00 00
05 48 90 02 01 01 89 80
05 48 90 01 01 01 53 03
02 00 05 00 0C 00 00 00 00 00 00 00 01 00 00 00
00 00 00 00 04 00 00 00 11 22 33 44
```

响应 ADS payload 的布局是 `Result=0`、`DataLength=4`、4 字节数据。解析器还会校验 AMS/TCP 长度、AMS `DataLength`、`StateFlags=0x0005`、CommandId、InvokeId，并可按请求上下文严格核对响应源/目标 NetId 与 Port。

## 其他首轮命令

- `ADS Write (0x0003)`：`IndexGroup`、`IndexOffset`、`WriteLength`、写数据；成功响应必须恰好是 4 字节 `Result`。
- `ADS ReadWrite (0x0009)`：`IndexGroup`、`IndexOffset`、`ReadLength`、`WriteLength`、写数据；响应使用 `Result + DataLength + data`。
- `ADS ReadDeviceInfo (0x0001)`：请求 payload 为空；成功响应固定 24 字节。
- `ADS ReadState (0x0004)`：请求 payload 为空；成功响应固定 8 字节，并解析 `AdsState` / `DeviceState`。

## 必须拒绝的输入

1. AMS/TCP reserved 非零、声明长度与实际帧长度不一致、AMS `DataLength` 与 payload 不一致。
2. AMS 响应不是 `StateFlags=0x0005`，或响应 CommandId/InvokeId 与请求不匹配。
3. AMS 路由错误非零、Read/ReadWrite 数据长度不一致、Write 响应不是 4 字节。
4. AMS NetId 不是六个 `0..255` 十进制字节，端口为 0，或单帧超过软件 1 MiB 上限。

## 证据边界

- `rust-core/src/ads.rs` 是编解码层，`rust-core/src/session.rs` 增加 TCP 48898 收帧和 endpoint/InvokeId 只读会话；`ads_build_*` / `ads_parse_*` 与 `open_ads_connection` / `ads_read*` 通过 Rust JSONL、Electron bridge 和 Beckhoff UI 暴露。
- 本轮不自动创建 TwinCAT 路由，不实现符号句柄 Create/Use/Release、Sum Command、Notification、UDP AMS、IPv6 或安全传输；独立 TCP 对端只模拟已声明的只读边界。
- 软件黄金帧和独立 TCP 对端通过标记 S2-S4a；TwinCAT 2/3 Runtime、具体 PLC 型号、AMS Route、抓包、重复读和长稳证据必须另行记录为 L2。
