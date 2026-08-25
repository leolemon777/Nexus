# FATEK FBs 原生 ASCII golden vectors

本文件固定 Nexus FATEK 边界：使用厂商原生 ASCII 编程协议的编解码，并在软件 TCP 会话层执行只读字/位读取；不把旧客户端的错误包络或自证明虚拟服务器当成实机证据。典型传输为 TCP 5000，具体端口以 Ethernet 模块配置为准。

## 包络

```text
STX(0x02) + station(2 位大写十六进制) + command/data + checksum(2 位大写十六进制) + ETX(0x03)
```

校验为从 STX 到校验字段前所有 ASCII 字节的 8-bit 加和，不包含校验字段和 ETX。站号只允许 `01H..FEH`。

## 命令向量

| 操作 | 输入 | 命令正文 | 关键检查 |
|---|---|---|---|
| 字读取 | 站号 1、`R12`、3 字 | `46 03 R00012` | 请求 ASCII `014603R0001275`（示例校验） |
| 位读取 | 站号 1、`X0`、1 点 | `44 01 X0000` | X/Y/M/S/T/C，地址四位十进制 |
| 位写入 | 站号 1、`Y0`、`1,0,1,1` | `45 04 Y0000 1011` | 单帧 1..255 点 |
| 字写入 | 站号 1、`R12`、小端字节 `34 12` | `47 01 R00012 1234` | 单帧 1..64 字 |
| 状态 | 站号 1 | `40` | 返回 status + 校验 |

字地址支持 `R/D/RT/RC`；位区的字操作数使用 `WX/WY/WM/WS/WT/WC`。位地址上限为 9999，R/D 字地址上限为 99999，RT/RC 为 9999。

## 响应向量和拒绝边界

- 正常响应必须检查 STX、站号、期望命令、status=`0`、两位十六进制校验和及 ETX。
- status 非 `0` 返回 `FATEK_DEVICE_ERROR` 并保留状态码；短帧、站号/命令不匹配和坏校验均 fail-closed。
- 位数量超过 255、字数量超过 64、`R99999 × 2`、`X9999 × 2`、非法区或 Word/Bit 类型错配必须拒绝。

## TCP 只读会话边界

- `open_fatek_connection` 显式打开 TCP 5000（无额外握手），保存站号和读写超时；连接成功不代表 FBs/Gateway L2。
- `fatek_read_words` 使用 46 命令，按 ETX 收完整响应，严格校验 STX/站号/命令/状态/加和校验，并要求响应数据为 `count×4` 个 ASCII 十六进制字符。
- `fatek_read_discrete` 使用 44 命令，要求响应数据恰为 `count` 个 ASCII `0/1`；写入、40/41 控制和自动重连不进入 live path。
- 独立回环 TCP 对端验证了 46 读请求、ETX 分帧、响应解析和只读返回；这只代表 S2-S4a 软件边界，不代表真实 FBs、B1 或 Ethernet Gateway。

## 路由与证据边界

- `open_fatek_connection`、`fatek_read_words`、`fatek_read_discrete`、`fatek_parse_address`、`fatek_build_*`、`fatek_pack_command` 和 `fatek_parse_response` 由 Rust core 提供，Electron 页面通过 preload/main 直达 Rust JSONL。
- 独立 TCP 对端只证明软件连接、ETX 分帧和只读事务；没有真实 FBs/B1 型号/固件记录、WinProladder 抓包、Ethernet Gateway 差异或 RUN/STOP 权限验证。
- 现场 L2、长连接、跨帧拆分、型号差异和任何写入必须另建门禁。
