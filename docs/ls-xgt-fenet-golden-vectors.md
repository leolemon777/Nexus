# LS Electric XGT FEnet golden vectors（TCP 只读软件边界）

本页记录首轮新增的 LS Electric XGT FEnet 协议证据。实现边界是 XGT Dedicated/FEnet 的 20 字节头、显式变量地址、单变量/连续读写构帧和响应边界校验；默认 TCP 2004，软件 TCP 只读会话和独立对端只标记 S4a，不把模拟结果当作现场通过。

## 请求：`%DW100` 单变量 Word Read

- TCP 端口：`2004`
- Company ID：`LSIS-XGT`，后续两字节填零
- CPU：`0xA0`（XGK）
- Source：`0x33`
- InvokeId：`0x0007`
- Base/Slot：`0/3`
- Application length：`0x0010`
- Header checksum：对 byte `0..18` 求 8-bit 累加

20 字节头（前 8 字节）示例：

```text
4C 53 49 53 2D 58 47 54 00 00 00 00 A0 33 07 00 10 00 03 XX
```

Read application：

```text
54 00 02 00 00 00 01 00 06 00 25 44 57 31 30 30
```

`0x54` 是 Read request，`0x02` 是 Word；DWord 应使用 `%DD100`，变量名按协议保留显式 `%DW100`。

## 响应：Read response + 4 bytes

响应使用 `Source=0x11`、`Command=0x55`，并要求 `BlockCount=1`、`DataLength=4`：

```text
55 00 02 00 00 00 01 00 00 00 00 00 01 00 04 00 11 22 33 44
```

其中 application 的语义字段为：`55 00 02 00 00 00 01 00 00 00 00 00 01 00 04 00 11 22 33 44`；响应解析还会检查 Company ID、InvokeId、头校验和、application length、错误状态和数据边界。

## 当前证据边界

- S2/S3：Rust codec、JSONL 路由、Electron IPC allow-list、UI 生成/解析、上述向量文档。
- S2-S4a 软件 TCP 只读：`open_ls_xgt_connection` 建立 TCP 2004 会话；`ls_xgt_read` 和 `ls_xgt_read_continuous` 按 20 字节头的 application length 收帧，校验 Company ID、响应 source、InvokeId、header checksum、block count、data length 和 Read response command。独立 TCP 对端只模拟软件边界。
- 真实 PLC L2：尚未完成；需要记录具体 XGK/XGI/XGR 型号、固件、FEnet 模块、IP/端口、变量只读结果和抓包。
- 不包含：真实型号/L2、RUN/STOP、程序/文件服务、批量优化、事件订阅、虚拟 XGT 服务器、Modbus TCP/EtherNet/IP/OPC UA 变体、任何 live 写入。
