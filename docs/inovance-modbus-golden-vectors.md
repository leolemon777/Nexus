# 汇川 H3U/H5U Modbus 地址 profile golden vectors

本文件固定 Nexus 首轮汇川边界：H3U/H5U 只使用标准 Modbus RTU/TCP 传输，软件层负责软元件到 Modbus 区域、地址和功能码的映射。它不证明 EasyNet 私有帧、Connected CIP、ComputerLink、AM/AC/Easy 型号兼容或真实 PLC L2。

## H3U 位/字向量

| 软元件 | 访问类型 | Modbus 区域 | 地址 | 读功能码 | 写功能码 | 备注 |
|---|---|---|---:|---:|---:|---|
| `X10` | bit | coil | `0xF808` | FC01 | — | `10` 按八进制解释，X 只读 |
| `Y17` | bit | coil | `0xFC0F` | FC01 | FC05 | `17` 按八进制解释 |
| `M7679` | bit | coil | `0x1DFF` | FC01 | FC05 | M 第一段上界 |
| `M8000` | bit | coil | `0x2400` | FC01 | FC05 | M 第二段起点 |
| `D8511` | word | holding register | `0x213F` | FC03 | FC06 | D 字区上界 |
| `C200` | word | holding register | `0xF700` | FC03 | FC16 | 32 位计数器，寄存器宽度 2 |

## H5U 位/字向量

| 软元件 | 访问类型 | Modbus 区域 | 地址 | 读功能码 | 写功能码 | 备注 |
|---|---|---|---:|---:|---:|---|
| `Y1777` | bit | coil | `0xFFFF` | FC01 | FC05 | 八进制地址上界 |
| `X177.7` | bit | coil | `0xFBFF` | FC01 | — | 点号位为八进制组内 `.0..7`，X 只读 |
| `M7999` | bit | coil | `0x1F3F` | FC01 | FC05 | M 上界 |
| `B0` | bit | coil | `0x3000` | FC01 | FC05 | B 区基址 |
| `D7999` | word | holding register | `0x1F3F` | FC03 | FC06 | D 上界 |
| `R32767` | word | holding register | `0xAFFF` | FC03 | FC16 | R 区上界 |

## 必须拒绝的输入

1. H3U `M7680..M7999` 空洞、H3U `X` 写入、超出 H3U/H5U 系列边界的地址。
2. `D100.5`、`ReadInt16(M0)`、`ReadBool(D0)` 等 Bit/Word 类型错配。
3. X/Y 非八进制数字或点号位不是 `.0..7`；H5U `Y2000` 等超过 `0o1777` 的输入。
4. `series=am`、AM/AC/Easy 未提供具体型号和手册时返回 `INOVANCE_SERIES_UNSUPPORTED`，不猜测统一地址表。

## 软件路由与证据边界

- `inovance_parse_address` 由 `rust-core/src/inovance.rs` 提供，Electron 页面通过 preload/main 直达 Rust JSONL；旧的通用 `brand_parse_address` 不作为新汇川页面路由。
- 返回结果明确标记 `underlyingProtocol=Modbus RTU / Modbus TCP`、读/写功能码、只读状态和计数器寄存器宽度。
- 本轮只做独立 profile 和软件黄金向量；没有真实 TCP/RTU 会话、设备身份记录、抓包、厂家工具同地址比对或写入。
- H3U/H5U 具体型号、固件、工程版本、站号/IP、串口参数和现场读回必须另建 L2 证据，不能把 JSONL/UI 通过升级为实机 PASS。
