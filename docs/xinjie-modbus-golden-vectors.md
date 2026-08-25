# 信捷 XC/XD/XL Modbus 地址 profile golden vectors

本文件固定 Nexus 首轮信捷边界：XC 与 XD/XL 只使用标准 Modbus RTU/TCP 的已确认 D 数据寄存器映射。D 地址为 0-based holding register，FC03 读取、FC06 写入。旧 C# 中其他区域的硬编码偏移与不同系列资料不一致，因此没有手册和抓包之前一律拒绝，不把猜测写成协议支持。

## 已确认向量

| 系列 | 软元件 | 访问类型 | Modbus 区域 | 地址 | 读功能码 | 写功能码 |
|---|---|---|---|---:|---:|---:|
| XC | `D0` | word/auto | holding register | `0x0000` | FC03 | FC06 |
| XC | `D100` | word/auto | holding register | `0x0064` | FC03 | FC06 |
| XD/XL | `D100` | word/auto | holding register | `0x0064` | FC03 | FC06 |
| XD/XL | `D65535` | word/auto | holding register | `0xFFFF` | FC03 | FC06 |

## 必须拒绝的向量

| 输入 | 原因 | 结果 |
|---|---|---|
| `X10` | 位区偏移/八进制规则未按型号确认 | `XINJE_ADDRESS_UNSUPPORTED` |
| `M0` | 内部继电器偏移存在 XC/XD 差异 | `XINJE_ADDRESS_UNSUPPORTED` |
| `HD0` / `SD0` | 扩展寄存器地址未确认 | `XINJE_ADDRESS_UNSUPPORTED` |
| `D65536` | 超出 Modbus 16-bit 地址 | `XINJE_PARAM_INVALID` |
| `D12.1` | D 首轮不支持点号位寻址 | `XINJE_PARAM_INVALID` |

## 路由与证据边界

- `xinjie_parse_address` 由 `rust-core/src/xinjie.rs` 提供，Electron 页面通过 preload/main 直达 Rust JSONL。
- 返回结果明确标记 `underlyingProtocol=Modbus RTU / Modbus TCP`、D 区、FC03/FC06 和 `D-register-confirmed` 证据。
- 本轮只完成独立 D profile 与软件黄金向量；没有真实 TCP/RTU 会话、设备身份记录、厂家工具同地址回读或写入。
- XC、XD、XL 具体型号、固件、端口/站号、串口参数和其他软元件区域必须另建 L2 资料；JSONL/UI 通过不能升级为实机 PASS。
