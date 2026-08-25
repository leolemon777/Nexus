# Fuji MICREX-SX SPH Loader Command golden vectors

本文件固定 Nexus Fuji 边界：使用 MICREX-SX SPH Loader Command 的二进制编解码，并在软件 TCP 会话层执行只读字读取；不把旧 ASCII 客户端或同源虚拟服务器当成实机证据。典型 TCP 18245，具体以 CPU/以太网模块配置为准。

## 地址与头

| 字段 | 约束 |
|---|---|
| 地址 | `M1.word`=`02H`、`M3.word`=`04H`、`M10.word`=`08H`、`I/Q.word`=`01H`；字地址为 24 位小端 |
| 位后缀 | 可解析 `M1.0.3`、`I0.0` 等 `.0..15`；首轮字构帧拒绝位后缀，位写入不开放 |
| 固定头 | 20 字节 `FB 80 80 00 FF 7B`，连接 ID 在 byte 6，`11` 在 byte 8 |
| 命令 | `00H` 读取、`01H` 写入；payload 从 byte 18/19 以小端给出 |
| 单帧 | 1..230 字，写入数据必须是 `wordCount×2` 字节 |

## 请求向量

```text
00H read M10.258 × 2:
FB 80 80 00 FF 7B FE 00 11 00 00 00 00 00 00 00 00 01 06 00 08 02 01 00 02 00

01H write M1.0 × 1, data 34 12:
FB 80 80 00 FF 7B FE 00 11 00 00 00 00 00 01 00 00 01 08 00 02 00 00 00 01 00 34 12
```

## 响应与拒绝边界

- 正常响应必须校验固定头、连接 ID、期望命令、payload length、CPU error byte、类型码、地址/字数回显和数据长度。
- CPU error 非零、短帧、长度字段不匹配、连接 ID/命令不匹配和期望数据长度不符均 fail-closed。
- `M1.16777215 × 2`、位后缀用于字构帧、未知区和超过 230 字必须拒绝。

## 路由与证据边界

- `open_fuji_sph_connection`、`fuji_sph_read`、`fuji_sph_parse_address`、`fuji_sph_build_read`、`fuji_sph_build_write` 和 `fuji_sph_parse_response` 由 Rust core 提供，Electron 页面通过 preload/main 直达 Rust JSONL。
- 软件会话显式连接 TCP 18245（SPH 无独立握手），按 20 字节响应头的 payload length 收满一帧，校验连接 ID、命令、地址/类型/字数回显和数据长度，然后只返回读取数据。
- 独立回环 TCP 对端验证了 26 字节读请求、分段响应收取、小端数据解释和回显拒绝；这只代表 S2-S4a 软件边界，不代表真实 SPH CPU、固件或以太网模块 L2。
- 真实连接、长连接、多帧拆分、I/Q 写权限、型号差异和现场写入必须另建门禁；live path 不提供写入或自动重连。
