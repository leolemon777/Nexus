# 阶段 6:打磨与扩展工具

> 离线报文解析器 + 数据导出 + 字节编解码计算器 + 桥接工具 + 高级 FC(22/23/43/08)。
> 这是"把工作台从能用变好用"的打磨阶段,依赖所有前序阶段的产品形态就位。
>
> 主索引:[spec-plan.md](./spec-plan.md)

---

## 目标

补齐剩余的高级功能,把工作台打磨到产品级。完成后:
- 离线报文解析器:粘贴任意 hex → 结构化解析(对标 .NET `ModbusPacketParser`)
- 数据导出:CSV / JSON / SQLite(对标 .NET `SqliteDataLogger`)
- 字节编解码计算器:hex/Base64/URL/压缩(对标 HSL `FormByteTransfer`)
- 桥接工具:串口↔TCP、TCP↔TCP(对标 HSL `FormSerialToTcp` / `FormTcpToTcp`)
- 高级 FC:FC22(屏蔽写)、FC23(原子读写)、FC43(设备标识)、FC08(诊断)

### 就绪标准
- [ ] Rust core:`frame_parser.rs` 完整离线解析(RTU/ASCII/TCP/RtuOverTcp/AsciiOverTcp)
- [ ] Rust core:FC22/23/43/08 的 PDU 构建/解析
- [ ] Electron:数据导出服务(CSV/JSON/SQLite)
- [ ] UI:报文解析 view(`data-view-panel="parser"`)
- [ ] UI:字节计算器弹窗
- [ ] UI:桥接配置面板

---

## A. 离线报文解析器

### 定位
独立工具,粘贴 hex 字符串即解析。横切所有产品形态(主站/从站/调试的流量都可"复制到解析器")。

### Rust core:`frame_parser.rs`(完整版)

```rust
pub struct FrameInfo {
    pub transport: Transport,           // Rtu | Ascii | Tcp | RtuOverTcp | AsciiOverTcp
    pub is_valid: bool,
    pub direction: Direction,           // Request | Response | Unknown
    pub transaction_id: Option<u16>,    // TCP only
    pub protocol_id: Option<u16>,       // TCP only
    pub unit_id: u8,
    pub function_code: u8,
    pub function_name: String,          // 中文:"读保持寄存器"
    pub base_function_code: u8,         // 去掉异常位
    pub is_exception: bool,
    pub exception_code: Option<u8>,
    pub exception_name: Option<String>, // "非法功能" 等
    pub address: Option<u16>,
    pub quantity: Option<u16>,
    pub write_address: Option<u16>,     // FC23
    pub write_quantity: Option<u16>,
    pub and_mask: Option<u16>,          // FC22
    pub or_mask: Option<u16>,
    pub byte_count: Option<u8>,
    pub mei_type: Option<u8>,           // FC43
    pub data: Vec<u8>,
    pub registers: Vec<u16>,            // 解码后的寄存器
    pub coils: Vec<bool>,               // 解码后的线圈
    pub checksum: u16,                  // CRC 或 LRC
    pub expected_checksum: u16,
    pub checksum_status: ChecksumStatus,// Valid | Invalid | NotApplicable | Missing
    pub summary: String,                // 人可读摘要
}

pub fn parse_frame(hex: &str, transport: Transport) -> Result<FrameInfo, ParseError>;
pub fn infer_transport(hex: &str) -> Option<Transport>;  // 启发式推断
```

### JSONL 命令
| 命令 | payload | result |
|---|---|---|
| `parse_frame_offline` | `{hex:"01 03 ...", transport:"rtu"\|"auto"}` | `{frameInfo:FrameInfo}` |

### UI:报文解析 view

```
┌─ 报文解析 ────────────────────────────────────────────────────┐
│                                                               │
│  ┌─ 输入 ────────────────────────────────────────────────┐   │
│  │ 传输: ○ 自动识别  ○ RTU  ○ ASCII  ○ TCP  ○ RtuOverTcp│   │
│  │ ┌──────────────────────────────────────────────────┐ │   │
│  │ │ 01 03 14 00 64 00 C8 00 2C ... C5 CD             │ │   │
│  │ └──────────────────────────────────────────────────┘ │   │
│  │ [解析] [清空] [从剪贴板粘贴]                          │   │
│  └───────────────────────────────────────────────────────┘   │
│                                                               │
│  ┌─ 解析结果 ────────────────────────────────────────────┐   │
│  │ 传输方式:    Modbus RTU                               │   │
│  │ 方向:        响应(Response)                          │   │
│  │ 站号:        1                                        │   │
│  │ 功能码:      03 (读保持寄存器)                       │   │
│  │ 字节计数:    20 (0x14)                                │   │
│  │ 寄存器数据:                                          │   │
│  │   [0] 0x0064  (100)                                  │   │
│  │   [1] 0x00C8  (200)                                  │   │
│  │   [2] 0x002C  (44)                                   │   │
│  │   ...                                                 │   │
│  │ CRC 校验:    ✓ 通过 (0xCDC5)                        │   │
│  │ 摘要:        站号1 FC03 响应 10个保持寄存器 CRC正确 │   │
│  └───────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────┘
```

---

## B. 数据导出

### Rust core:无变更(纯 Electron 层)

### Electron:`electron/data-export-service.cjs`

```javascript
class DataExportService {
  exportCsv({ rows, filename }) { ... }       // 寄存器/线圈表格
  exportJson({ data, filename }) { ... }       // 任意结构化数据
  exportTraceLog({ frames, filename }) { ... } // TX/RX 追踪记录
  exportToSqlite({ tables, dbPath }) { ... }   // 对标 .NET SqliteDataLogger
}
```

### UI
- 主站/从站/调试 view 的流量记录区都加「导出」按钮
- 导出格式下拉:CSV / JSON / SQLite

---

## C. 字节编解码计算器

### 定位
独立小工具弹窗,对标 HSL `FormByteTransfer`。

### 功能
| 操作 | 输入 → 输出 |
|---|---|
| Hex ↔ 字符串 | `48656C6C6F` ↔ `Hello` |
| Base64 | `SGVsbG8=` ↔ `Hello` |
| URL 编码 | `Hello%20World` ↔ `Hello World` |
| Deflate 压缩 | 压缩/解压字节 |
| 大小端转换 | `0x12345678` ↔ `0x78563412` |
| Float ↔ Hex | `3.14` ↔ `0x4048F5C3` |

### 实现
纯前端 JavaScript(不需要 Rust),各操作函数内联。

---

## D. 桥接工具

### 定位
对标 HSL `FormSerialToTcp` / `FormTcpToTcp`。

### 类型
| 桥接 | 说明 |
|---|---|
| 串口 → TCP | 监听 TCP 端口,收到数据转发到串口;串口数据回传 TCP |
| TCP → 串口 | 同上,方向描述不同 |
| TCP → TCP | TCP 代理,记录流量(对标 .NET `ModbusGateway`) |

### 实现
Electron 层实现(`electron/bridge-service.cjs`),用 `net.createServer` + `serialport`。

### UI
```
┌─ 桥接配置 ─────────────────────────────────────┐
│ 类型: ○ 串口→TCP  ○ TCP→串口  ○ TCP→TCP     │
│ 监听: [0.0.0.0:8080]                           │
│ 目标: [COM3] [9600]  或  [192.168.1.5:502]    │
│ [启动桥接] [停止]                              │
│ 流量: [实时记录,可导出]                        │
└─────────────────────────────────────────────────┘
```

---

## E. 高级功能码(FC22/23/43/08)

### Rust core:`modbus_pdu.rs` 扩展

```rust
// FC22 屏蔽写寄存器
pub fn build_mask_write_register_pdu(address: u16, and_mask: u16, or_mask: u16) -> Vec<u8>;
pub fn parse_mask_write_register_response(pdu: &[u8]) -> Result<(u16, u16, u16), ModbusError>;

// FC23 原子读写多寄存器
pub fn build_read_write_multiple_registers_pdu(
    read_address: u16, read_quantity: u16,
    write_address: u16, write_values: &[u16],
) -> Result<Vec<u8>, ModbusError>;
pub fn parse_read_write_multiple_registers_response(pdu: &[u8], quantity: u16) -> Result<Vec<u16>, ModbusError>;

// FC43/14 读设备标识
pub fn build_read_device_id_pdu(read_device_id_code: u8, object_id: u8) -> Vec<u8>;
pub fn parse_read_device_id_response(pdu: &[u8]) -> Result<DeviceIdentification, ModbusError>;

// FC08 诊断
pub fn build_diagnostics_pdu(sub_function: u8, data: u16) -> Vec<u8>;
pub fn parse_diagnostics_response(pdu: &[u8]) -> Result<u16, ModbusError>;
```

### 新增 JSONL 命令(对应高层操作)
- `tcp_mask_write_register` / `tcp_read_write_multiple` / `tcp_read_device_id` / `tcp_diagnostics`
- 串口路径的 `build_*` / `parse_*`

---

## 测试要求

### Rust 单元测试
- `frame_parser.rs`:每种 transport × 每个 FC 的解析(正常 + 异常)
- FC22/23/43/08 的 build/parse 往返
- `infer_transport` 启发式(如 MBAP 头 protocol_id=0 → TCP)

### Electron 测试
- `data-export-service.cjs`:CSV/JSON/SQLite 导出格式正确性
- `bridge-service.cjs`:桥接字节转发(mock 双端)

### 冒烟测试
- 报文解析 view 可用
- 导出按钮可用

---

## 风险与注意事项

1. **FC43 的 MEI 类型扩展** —— Read Device Identification (0x0E) 只是 MEI 类型之一,未来可能加其他(如 0x0D 文件记录)。PDU 解析需要可扩展。
2. **桥接的字节顺序** —— 串口↔TCP 桥接时,RTU 帧(含 CRC)和 TCP 帧(无 CRC,有 MBAP)的格式不同。纯透传会破坏帧。需要可选的"协议转换"模式(RTU↔TCP 格式转换)。
3. **数据导出的大文件** —— SQLite 导出高频轮询数据时,可能产生大量行。需要分批写入 + 事务优化。
4. **报文解析的歧义** —— 同一段 hex 可能符合多种 transport 格式。`infer_transport` 用启发式(优先级:TCP > RtuOverTcp > RTU > ASCII),但不可能 100% 准确。UI 应允许用户手动切换 transport 重新解析。

---

## 对标参考

| 参考 | 文件 | 借鉴点 |
|---|---|---|
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusPacketParser.cs` | 结构化解析(643 行,5 种 transport) |
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusDiagnostics.cs` | 人可读解码(599 行) |
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusGateway.cs` | TCP 网关代理 |
| .NET Nexus | `Nexus/src/Nexus.App/Services/SqliteDataLogger.cs` | SQLite 日志 |
| .NET Nexus | `Nexus/src/Nexus.App/Services/DataExportService.cs` | CSV/JSON 导出 |
| HSL Demo | `i3195/.../HslDebug/FormByteTransfer.cs` | 字节编解码计算器 |
| HSL Demo | `i3195/.../HslDebug/FormSerialToTcp.cs` | 串口↔TCP 桥接 |
| HSL Demo | `i3195/.../HslDebug/FormTcpToTcp.cs` | TCP↔TCP 桥接 |
