# 西门子变体路由与软件黄金边界

日期：2026-08-23  
证据等级：S2/S3。Fetch/Write、PPI/USS/RK512 帧见既有黄金文档。本页固定**不得静默回落 S7comm**。

## 1. 变体矩阵

| UI 变体 | kind | 连接 | 读 | 写 | 说明 |
|---|---|---|---|---|---|
| `s7comm` | s7comm | `open_s7_connection` | `s7_read` | `s7_write` | ISO-on-TCP |
| `smart` | s7comm | `open_s7_connection` | `s7_read` | `s7_write` | 地址 profile；slot 0 失败可试 1 |
| `ppi` | ppi | `open_ppi_tcp` | `ppi_read` | `ppi_write` | 透明 TCP 网关 |
| `ppi-serial` | ppi-serial | `get_serial_status` | `ppi_serial_read` | 无 | 原生 COM 只读双拍 |
| `fw` | fetchwrite | `open_fw_tcp` | `fw_read` | `fw_write` | 默认端口 2000，勿用 102 |
| `webapi` | webapi | `s7web_connect` | `s7web_read` | `s7web_write` | HTTPS；密码不落盘 |
| `uss` | uss-serial | `get_serial_status` | `uss_serial_read` | 无 | 参数只读 |
| `rk512` | rk512-serial | `get_serial_status` | `rk512_serial_read` | 无 | 3964R 只读 |

未知或空变体 `kind=unknown`、`online=false`，**不得**回落到 S7comm。USS/RK512/PPI/FW/Web API 连接不得调用 `open_s7_connection`。

SMART 是 S7comm 地址 profile（V 区映射 DB1），不是另一种报文。S7comm-Plus / PROFINET / MPI 在 UI 为禁用项，没有路由。

## 2. 安全

- 数据写入需要确认；CPU HOT-START / COLD-START / STOP 需要输入特定词并审计。
- Web API 当前证书校验为 `rejectUnauthorized=false`，只可作为实验室例外，不能当成生产信任锚；密码不得写入项目文件。
- 软件/虚拟 CPU 通过不等于 S7-200/SMART/1200/1500 L2。

## 3. 关联文档与测试

- `docs/fetchwrite-golden-vectors.md`
- `docs/serial-golden-vectors.md`（PPI / USS / RK512）
- `src/siemens-route.js`、`electron/siemens-route.test.cjs`
- `electron/ppi-serial-service.test.cjs`、`electron/s7-webapi-service.test.cjs`
