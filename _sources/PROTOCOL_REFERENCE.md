# Nexus 协议参考文档

> 本文档从公开协议规范和 HSL 反编译代码中提取的**协议报文流程**，用于指导 Nexus 协议实现。
> **不包含任何源代码复制，仅记录协议帧结构、字节偏移、状态码等公开技术规范。**

---

## 1. Siemens S7 Protocol

### 1.1 连接握手（两阶段）

**阶段1: COTP Connect Request (22 bytes)**
- TPKT Header: `03 00 00 16` (version=3, reserved=0, length=22)
- COTP: `11 E0 00 00 00 01 00 C0` (length=17, PDU type=CR, dest_ref=0x0000, source_ref=0x0001, class=0xC0)
- S7 Comm Parameters: 变体依型号不同

**型号配置:**
| 型号 | ConnectionType (byte[20]) | TSAP计算 (byte[21]) |
|---|---|---|
| S7-1200 | 0x01 | rack*32 + slot, slot默认=1 |
| S7-1500 | 0x01 | rack*32 + slot, slot默认=1 |
| S7-300 | 0x01 | rack*32 + slot, slot默认=2 |
| S7-400 | 0x01 | rack*32 + slot |
| S7-200Smart | N/A | 使用独立的 plcHead1_200smart |
| S7-200 | N/A | 使用独立的 plcHead1_200, TSAP=MW(0x4D57) |

**阶段2: S7 Communication Setup (25 bytes)**
- 固定格式: `03 00 00 19 02 F0 80 32 01 00 00 XX XX ...`
- 协商 PDU 长度，默认 240/960 bytes

### 1.2 S7 PDU 结构

**Read Request:**
- 功能码: `0x04` (Read)
- 地址区域码:
  - `0x05` = SM (S7-200)
  - `0x06` = AI
  - `0x07` = AQ
  - `0x80` = P (Peripheral)
  - `0x81` = I (Input)
  - `0x82` = Q (Output)
  - `0x83` = M (Memory)
  - `0x84` = DB (Data Block) + DB号
  - `0x1F` = T (Timer)
  - `0x1E` = C (Counter)

**Write Request:** 功能码 `0x05`
**Bool读写:** bit offset 在地址最低3位

### 1.3 多地址批量读取
- 最多19个地址/包
- 总字节数 ≤ PDU长度
- 响应中按 `FF 04` (字) 和 `FF 09` (多地址) 分隔

### 1.4 PLC控制命令
- Stop: 功能码 `0x29` (41)
- HotStart: 功能码 `0x28` (40)
- ColdStart: 类似HotStart但带额外参数

### 1.5 S7 String/WString
- String: 首字节=最大长度，次字节=当前长度，后续=ASCII数据
- WString: 首2字节=最大长度，次2字节=当前长度，后续=Unicode数据

### 1.6 FetchWrite 协议
- 独立于 S7 comm，端口同样是102
- 不同的帧格式，用于老型号PLC

---

## 2. Mitsubishi MC Protocol (3E Frame)

### 2.1 帧结构
```
| 子头部 (4B) | 网络号 (1B) | PC号 (1B) | 请求目标IO (2B) | 网络站号 (1B) | 数据 | 等待循环 (2B) |
```
- 默认子头部: `50 00 00 FF` (SLMP Binary)
- 网络号: 0x00
- PC号: 0xFF
- 目标IO: 0x03FF
- 网络站号: 0x00

### 2.2 命令码
| 操作 | 命令码(HEX) | 子命令 |
|---|---|---|
| 批量读取(字) | 04 01 | 00 00 |
| 批量写入(字) | 14 01 | 00 00 |
| 批量读取(位) | 04 01 | 01 00 |
| 批量写入(位) | 14 01 | 01 00 |
| 随机读取(字) | 04 03 | 00 00 |
| 随机写入(字) | 14 02 | 00 00 |

### 2.3 地址区域码
| 区域 | 代码(HEX) |
|---|---|
| X | 009C |
| Y | 009D |
| M | 0090 |
| D | 0168 |
| R | 01B0 |
| ZR | 01B0 |
| B | 00A0 |
| W | 0174 |
| L | 0092 |

### 2.4 A1E 协议
- ASCII帧格式，更简单的命令结构
- 用于较老型号的FX/Q系列

### 2.5 FxSerial 协议
- 串口通信，RS-232/RS-485
- 命令格式: ENQ + 站号 + 命令 + 地址 + 数据 + 校验 + CR/LF

---

## 3. Omron FINS Protocol

### 3.1 握手 (TCP)
- 发送: `FINS + 00 00 00 0C + 00 00 00 00 + 客户端节点(4B) + 服务器节点(4B)`
- 响应包含: 服务器分配的客户端节点号

### 3.2 FINS 命令帧
```
| ICF(1B) | RSV(1B=0) | GCT(1B=2) | DNA(1B) | DA1(1B) | DA2(1B) | SNA(1B) | SA1(1B) | SA2(1B) | SID(1B) | 命令码(2B) | 数据 |
```

### 3.3 关键命令码
| 操作 | 命令码 |
|---|---|
| 内存区域读取 | 01 01 |
| 内存区域写入 | 01 02 |
| 内存区域填充 | 01 03 |
| 内存区域读取(多地址) | 01 04 |
| 参数区域读取 | 02 01 |
| CPU单元状态读取 | 06 01 |
| CPU单元数据读取 | 05 01 |
| 时间读取 | 07 01 |
| 时间写入 | 07 02 |
| 运行 | 04 01 |
| 停止 | 04 02 |

### 3.4 地址区域码
| 区域 | 代码(HEX) |
|---|---|
| CIO | 80 / B0 |
| WR | B1 |
| HR | B2 |
| AR | B3 |
| DM | 82 / 02 |
| EM | 90-99 |
| TIM | 09 |
| CNT | 0C |

---

## 4. Modbus Protocol (参考)

### 4.1 TCP ADU 结构
```
| TransactionId(2B) | ProtocolId(2B=0) | Length(2B) | UnitId(1B) | PDU |
```

### 4.2 功能码
| 码 | 操作 |
|---|---|
| 01 | 读线圈 |
| 02 | 读离散输入 |
| 03 | 读保持寄存器 |
| 04 | 读输入寄存器 |
| 05 | 写单个线圈 |
| 06 | 写单个寄存器 |
| 15 | 写多个线圈 |
| 16 | 写多个寄存器 |
| 23 | 读写多个寄存器 |

### 4.3 RTU 帧结构
- 无MBAP头，CRC16校验
- `| 设备地址(1B) | 功能码(1B) | 数据 | CRC16(2B) |`

---

## 5. Allen-Bradley CIP Protocol

### 5.1 连接
- 注册Session: `0x65` 命令
- ForwardOpen: 建立显式/隐式连接

### 5.2 CIP Services
| 服务码 | 操作 |
|---|---|
| 0x4C | CIP Read |
| 0x4D | CIP Write |
| 0x52 | Get Attribute All |
| 0x0E | Get Attribute Single |
| 0x10 | Set Attribute Single |

### 5.3 PCCC (老型号)
- DF1/Logix 协议
- 不同的命令结构

---

## 6. Panasonic Mewtocol Protocol

### 6.1 帧格式 (ASCII)
```
| %(1B) | 站号(2B) | 命令(#RD/#WD) | 地址(4-8B) | 数据 | BCC校验(2B) | CR |
```

### 6.2 命令
| 命令 | 说明 |
|---|---|
| #RD | 读取 |
| #WD | 写入 |
| #RCS | 读取单个触点 |
| #WCS | 写入单个触点 |

---

## 7. Keyence Nano Protocol

### 7.1 帧格式 (Binary)
- 命令码(2B) + 数据
- 简单的请求-响应模式

### 7.2 KV 系列
- ASCII命令模式
- Binary命令模式

---

## 8. Delta DVP Protocol

### 8.1 帧格式
- 与Modbus RTU类似但有自己的扩展
- 站号 + 功能码 + 数据 + CRC

---

## 9. Beckhoff ADS Protocol

### 9.1 AMS/TCP 帧结构
```
| AMS/TCP Header (6B) | AMS Header (32B) | Data |
```

### 9.2 ADS 命令
| ID | 操作 |
|---|---|
| 1 | ReadDeviceInfo |
| 2 | Read |
| 3 | Write |
| 4 | ReadState |
| 5 | WriteControl |
| 6 | AddDeviceNotification |
| 7 | DeleteDeviceNotification |
| 8 | DeviceNotification |

---

## 10. LS Electric (LSIS)

### 10.1 Cnet 协议
- ASCII帧，类似 Modbus 但有自己的命令集

### 10.2 FastEnet 协议
- 二进制帧，高速以太网通信
- 支持批量读写、随机读写
