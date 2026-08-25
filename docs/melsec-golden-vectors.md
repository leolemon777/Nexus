# 三菱 MELSEC / FX / MC 软件黄金向量与变体矩阵

日期：2026-08-23  
证据等级：S2/S3（Rust MC/FX/C24 编解码、虚拟从站、JSONL、Electron 路由）；**不是** FX3U/Q/L/iQ-F 真机 L2。

十个可选变体各自有稳定 ID。未知变体 fail-closed，不得回落到 3E Binary。C24 本阶段只读。

## 1. 变体矩阵

| UI 变体 | kind | 连接 | 读 | 写 | 状态 |
|---|---|---|---|---|---|
| `3e` | mc-tcp | `open_mc_tcp_connection` | `mc_tcp_read` | `mc_tcp_write` | 在线读写（软件） |
| `4e` | mc-tcp | `open_mc_tcp_connection` | `mc_tcp_read` | `mc_tcp_write` | 在线读写（软件） |
| `ascii-3e` | mc-ascii | `open_mc_ascii_connection` | `mc_ascii_read` | `mc_ascii_write` | 在线读写（软件） |
| `ascii-4e` | mc-ascii | `open_mc_ascii_connection` | `mc_ascii_read` | `mc_ascii_write` | 在线读写（软件） |
| `mc-udp-3e` | mc-udp | `open_mc_udp_connection` | `mc_udp_read` | `mc_udp_write` | 在线读写（软件） |
| `mc-udp-4e` | mc-udp | `open_mc_udp_connection` | `mc_udp_read` | `mc_udp_write` | 在线读写（软件） |
| `mc-1e` | mc-1e | `open_mc_1e_tcp` | `mc_1e_read` | `mc_1e_write` | 在线读写（软件） |
| `mc-c24` | mc-c24-serial | `get_serial_status` | `mc_c24_serial_read` | 无 | **只读**；写入禁用 |
| `fx-links` | fx-links | `get_serial_status` | `fx_serial_transact` | `fx_serial_transact` | 共享 COM |
| `fx-prog` | fx-prog | `get_serial_status` | `fx_serial_transact` | `fx_serial_transact` | 共享 COM |

C24 写若误走 `fx_serial_transact("c24")`，Electron 返回 `FX_BAD_PROTOCOL`。UI 在 C24 会话禁用写按钮与 CPU 控制。

## 2. 地址语义（软件门禁）

| 区 | 解析 |
|---|---|
| X / Y | 八进制 |
| B / W / ZR | 十六进制 |
| D / M | 十进制 |

现场仍须用 GX Works 同地址只读比对；软件解析通过不等于型号手册已核完。

## 3. 黄金帧（3E Binary 读 D100 × 1）

请求（文档 §2.1.4-(2)，Rust `mc_frame`）：

```text
50 00 00 FF FF 03 00 0C 00 10 00 01 04 01 00 64 00 00 A8 01 00
```

成功响应，D100 = `0x1234`（数据小端）：

```text
D0 00 00 FF FF 03 00 04 00 00 00 34 12
```

写成功响应仅结束码、无数据：

```text
D0 00 00 FF FF 03 00 02 00 00 00
```

结束码非 0 必须显示，不得当成功。CPU RUN/STOP/RESET 继续要求输入特定词，默认测试不要做。

## 4. C24 只读步骤

1. 在 Modbus 主站页打开 C24 使用的 COM。
2. 三菱页选择 `mc-c24`，填站号，点连接（只检查串口已打开）。
3. 读软元件；写按钮应禁用。若仍点写，提示「MC-C24 串口 写入未开放」。
4. 不宣称 Q 系列 C24 真机 L2。

## 5. 关联测试

- `src/melsec-route.js`、`electron/melsec-route.test.cjs`
- `rust-core/src/mc_frame.rs`、`rust-core/tests/mc_jsonl_e2e.rs`
- `electron/main.cjs` 的 `mc_c24_serial_read` / `FX_BAD_PROTOCOL`
