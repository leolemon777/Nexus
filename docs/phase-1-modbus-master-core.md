# 阶段 1:Modbus 主站核心

> 全功能码(FC01–06、15、16)+ 全传输(RTU/ASCII/TCP/UDP/RtuOverTcp/AsciiOverTcp)+ 基础配置。
> 这是整个工作台的基础 —— 所有后续阶段都依赖此阶段的编解码就绪。
>
> 主索引:[spec-plan.md](./spec-plan.md) | 产品架构:[产品架构.md](./产品架构.md)

---

## 目标

把主站从「仅 FC03/FC04 RTU 串口读」推进到「全 FC 读写 + 6 种传输」。完成后:
- 用户可读线圈(FC01)、离散输入(FC02)、保持寄存器(FC03)、输入寄存器(FC04)
- 用户可写单线圈(FC05)、单寄存器(FC06)、多线圈(FC15)、多寄存器(FC16)
- 用户可选择连接方式:RTU 串口 / ASCII 串口 / TCP / UDP / RtuOverTcp / AsciiOverTcp
- 写操作支持广播(站号 0)

### 就绪标准
- [ ] Rust core:`modbus_pdu.rs` 实现全部 8 个 FC 的构建/解析,传输无关
- [ ] Rust core:`modbus_tcp.rs` 实现 MBAP 帧 + CRC-less PDU
- [ ] Rust core:`modbus_ascii.rs` 实现 LRC + ASCII 帧
- [ ] Rust core:`Session` 结构管理 TCP/UDP 连接(socket 在 Rust 手里)
- [ ] Rust core:串口路径仍走 Electron 持有句柄(不破坏现有)
- [ ] Electron:6 种传输的端到端 IPC handler
- [ ] UI:功能码下拉填充 FC01–06、15、16;连接方式切换;写操作表单
- [ ] 四层测试全绿:Rust 单元 + JSONL 集成 + Electron + 冒烟
- [ ] 现有 FC03/FC04 RTU 路径完全不受影响

---

## Rust core 变更

### 新模块 1:`modbus_pdu.rs`(传输无关的 PDU 层)

**核心思想**:把所有 FC 的构建/解析从 `modbus_rtu.rs` 抽出来,变成传输无关的纯 PDU 操作。RTU 只是在 PDU 外面套 `[unit][pdu][crc16]`,TCP 套 MBAP,ASCII 套 `:[hex][lrc]CRLF`。

```rust
// 功能码常量
pub const READ_COILS: u8 = 0x01;
pub const READ_DISCRETE_INPUTS: u8 = 0x02;
pub const READ_HOLDING_REGISTERS: u8 = 0x03;
pub const READ_INPUT_REGISTERS: u8 = 0x04;
pub const WRITE_SINGLE_COIL: u8 = 0x05;
pub const WRITE_SINGLE_REGISTER: u8 = 0x06;
pub const WRITE_MULTIPLE_COILS: u8 = 0x0F;
pub const WRITE_MULTIPLE_REGISTERS: u8 = 0x10;

// 数量上限
pub const MAX_READ_COILS: u16 = 2000;
pub const MAX_READ_REGISTERS: u16 = 125;
pub const MAX_WRITE_COILS: u16 = 1968;
pub const MAX_WRITE_REGISTERS: u16 = 123;

// 读 PDU 构建(返回纯 PDU 字节,不含 unit/CRC/MBAP)
pub fn build_read_coils_pdu(start_address: u16, quantity: u16) -> Result<Vec<u8>, ModbusError>;
pub fn build_read_discrete_inputs_pdu(start_address: u16, quantity: u16) -> Result<Vec<u8>, ModbusError>;
pub fn build_read_holding_registers_pdu(start_address: u16, quantity: u16) -> Result<Vec<u8>, ModbusError>;
pub fn build_read_input_registers_pdu(start_address: u16, quantity: u16) -> Result<Vec<u8>, ModbusError>;

// 写 PDU 构建
pub fn build_write_single_coil_pdu(address: u16, value: bool) -> Result<Vec<u8>, ModbusError>;
pub fn build_write_single_register_pdu(address: u16, value: u16) -> Result<Vec<u8>, ModbusError>;
pub fn build_write_multiple_coils_pdu(address: u16, values: &[bool]) -> Result<Vec<u8>, ModbusError>;
pub fn build_write_multiple_registers_pdu(address: u16, values: &[u16]) -> Result<Vec<u8>, ModbusError>;

// 读响应解析
pub fn parse_read_coils_response(pdu: &[u8], quantity: u16) -> Result<Vec<bool>, ModbusError>;
pub fn parse_read_discrete_inputs_response(pdu: &[u8], quantity: u16) -> Result<Vec<bool>, ModbusError>;
pub fn parse_read_holding_registers_response(pdu: &[u8], quantity: u16) -> Result<Vec<u16>, ModbusError>;
pub fn parse_read_input_registers_response(pdu: &[u8], quantity: u16) -> Result<Vec<u16>, ModbusError>;

// 写响应解析(写响应回显请求头,无 byte_count)
pub fn parse_write_single_coil_response(pdu: &[u8]) -> Result<(u16, bool), ModbusError>;
pub fn parse_write_single_register_response(pdu: &[u8]) -> Result<(u16, u16), ModbusError>;
pub fn parse_write_multiple_coils_response(pdu: &[u8]) -> Result<(u16, u16), ModbusError>;
pub fn parse_write_multiple_registers_response(pdu: &[u8]) -> Result<(u16, u16), ModbusError>;

// 异常响应检查(所有 FC 共用)
pub fn check_exception(pdu: &[u8], expected_fc: u8) -> Result<Option<u8>, ModbusError>;
```

**验证规则**(传输无关):
- 读线圈/离散输入:数量 1..=2000
- 读寄存器:数量 1..=125
- 写多线圈:数量 1..=1968
- 写多寄存器:数量 1..=123
- 地址 + 数量 - 1 不溢出 u16
- **广播策略**:写操作允许 unit 0,读操作禁止 unit 0

### 新模块 2:`modbus_tcp.rs`(MBAP 帧)

```rust
pub struct MbapHeader {
    pub transaction_id: u16,   // AtomicU16,自动递增
    pub protocol_id: u16,      // 恒为 0
    pub length: u16,           // 后续字节数 = unit_id(1) + pdu_len
    pub unit_id: u8,
}

pub fn build_mbap_frame(transaction_id: u16, unit_id: u8, pdu: &[u8]) -> Vec<u8>;
pub fn parse_mbap_frame(bytes: &[u8]) -> Result<(MbapHeader, Vec<u8>), ModbusError>;

// 事务 ID 生成器(线程安全)
pub struct TransactionIdGenerator { counter: AtomicU16 }
impl TransactionIdGenerator {
    pub fn new() -> Self;
    pub fn next(&self) -> u16;
}
```

**注意**:MBAP 无 CRC —— TCP 的完整性由底层保证。

### 新模块 3:`modbus_ascii.rs`(LRC + ASCII 帧)

```rust
// LRC(Longitudinal Redundancy Check)
pub fn compute_lrc(bytes: &[u8]) -> u8;
pub fn verify_lrc(bytes: &[u8], expected: u8) -> bool;

// ASCII 帧格式: ':' + Hex(unit + pdu + lrc) + CR + LF
pub fn build_ascii_frame(unit_id: u8, pdu: &[u8]) -> Vec<u8>;  // 返回完整 ASCII 字节
pub fn parse_ascii_frame(bytes: &[u8]) -> Result<(u8, Vec<u8>), ModbusError>;
```

### 新结构:`Session`(会话状态,阶段 1 引入)

**这是架构转折点** —— `handle_line` 从纯函数变成有状态。

```rust
// lib.rs
pub struct Session {
    connections: HashMap<String, Connection>,
    tid_gen: TransactionIdGenerator,
}

pub enum Connection {
    Tcp { stream: std::net::TcpStream, unit_id: u8 },
    Udp { socket: std::net::UdpSocket, peer: SocketAddr, unit_id: u8 },
    // 串口不在 Connection 里 —— 句柄在 Electron 手里
}

impl Session {
    pub fn new() -> Self;
    pub fn open_tcp(&mut self, id: &str, host: &str, port: u16, unit_id: u8) -> Result<(), CoreError>;
    pub fn open_udp(&mut self, id: &str, host: &str, port: u16, unit_id: u8) -> Result<(), CoreError>;
    pub fn close_connection(&mut self, id: &str) -> Result<(), CoreError>;
    pub fn transact_tcp(&mut self, id: &str, pdu: &[u8]) -> Result<Vec<u8>, CoreError>;
    pub fn transact_udp(&mut self, id: &str, pdu: &[u8]) -> Result<Vec<u8>, CoreError>;
}

// serve() 改为持 Session
pub fn serve<R: BufRead, W: Write>(session: &mut Session, reader: R, writer: W) -> io::Result<()>;
// handle_line 改为
pub fn handle_line(session: &mut Session, line: &str) -> CommandOutcome;
```

### JSONL 新增命令(阶段 1)

**串口路径**(RTU + ASCII,Electron 持句柄):
| 命令 | payload | result |
|---|---|---|
| `build_read_coils` | `{unitId, startAddress, quantity}` | `{adu, requestHex, expectedResponseLength, exceptionResponseLength}` |
| `parse_read_coils` | `{response, unitId, quantity}` | `{status, exceptionCode, exceptionName, coils:[bool]}` |
| `build_read_discrete_inputs` | 同上 | 同上 |
| `parse_read_discrete_inputs` | 同上 | 同上 |
| `build_write_single_coil` | `{unitId, address, value:bool}` | `{adu, ...}` |
| `parse_write_single_coil` | `{response, unitId}` | `{status, address, value}` |
| `build_write_single_register` | `{unitId, address, value:u16}` | `{adu, ...}` |
| `parse_write_single_register` | `{response, unitId}` | `{status, address, value}` |
| `build_write_multiple_coils` | `{unitId, address, values:[bool]}` | `{adu, ...}` |
| `parse_write_multiple_coils` | `{response, unitId}` | `{status, address, quantity}` |
| `build_write_multiple_registers` | `{unitId, address, values:[u16]}` | `{adu, ...}` |
| `parse_write_multiple_registers` | `{response, unitId}` | `{status, address, quantity}` |
| `build_ascii_*` / `parse_ascii_*` | 同上但生成 ASCII 帧 | 同上 |

**TCP/UDP 路径**(Rust 持 socket,端到端命令):
| 命令 | payload | result |
|---|---|---|
| `open_tcp_connection` | `{connectionId, host, port, unitId}` | `{connected:true}` |
| `open_udp_connection` | `{connectionId, host, port, unitId}` | `{connected:true}` |
| `close_connection` | `{connectionId}` | `{closed:true}` |
| `tcp_read_coils` | `{connectionId, startAddress, quantity}` | `{status, coils, elapsedMs, exceptionCode}` |
| `tcp_read_discrete_inputs` | 同上 | 同上 |
| `tcp_read_holding_registers` | 同上 | `{status, registers, elapsedMs, ...}` |
| `tcp_read_input_registers` | 同上 | 同上 |
| `tcp_write_single_coil` | `{connectionId, address, value}` | `{status, elapsedMs}` |
| `tcp_write_single_register` | `{connectionId, address, value}` | 同上 |
| `tcp_write_multiple_coils` | `{connectionId, address, values}` | 同上 |
| `tcp_write_multiple_registers` | `{connectionId, address, values}` | 同上 |
| `udp_*` | 同 tcp_* | 同上 |

> **设计决策**:串口路径保留 build/transact/parse 三段式(因为 socket 在 Electron);TCP/UDP 路径用端到端命令(因为 socket 在 Rust,拆开没意义)。两条路径的高层语义对齐。

### 新增错误码(`error.rs`)

```rust
// 写专用
InvalidWriteQuantity { quantity, max }              → INVALID_WRITE_QUANTITY
WriteByteCountMismatch { expected, received }       → WRITE_BYTE_COUNT_MISMATCH
CoilValueInvalid { value }                          → COIL_VALUE_INVALID  // FC05 值不在 {0x0000,0xFF00}

// TCP 专用
ConnectionNotFound { connection_id }                → CONNECTION_NOT_FOUND
ConnectionFailed { host, port, reason }             → CONNECTION_FAILED
MbapFrameTooShort { len }                           → MBAP_FRAME_TOO_SHORT
MbapProtocolMismatch { received }                   → MBAP_PROTOCOL_MISMATCH  // protocol_id != 0
MbapLengthMismatch { expected, received }           → MBAP_LENGTH_MISMATCH
TransactionIdMismatch { expected, received }        → TRANSACTION_ID_MISMATCH

// ASCII 专用
AsciiFrameTooShort { len }                          → ASCII_FRAME_TOO_SHORT
AsciiStartByteMissing                              → ASCII_START_BYTE_MISSING  // 缺 ':'
AsciiEndBytesMissing                               → ASCII_END_BYTES_MISSING   // 缺 CR LF
AsciiHexDecodeFailed { char }                       → ASCII_HEX_DECODE_FAILED
LrcMismatch { expected, received }                  → LRC_MISMATCH
```

---

## Electron 层变更

### `rust-core-client.cjs` — 扩展 COMMANDS

```javascript
const COMMANDS = Object.freeze({
  // 现有
  HELLO: "hello",
  VALIDATE_SERIAL_CONFIG: "validate_serial_config",
  BUILD_READ_HOLDING_REGISTERS: "build_read_holding_registers",
  // ... 现有命令 ...
  SHUTDOWN: "shutdown",

  // 阶段 1 新增 — 串口路径
  BUILD_READ_COILS: "build_read_coils",
  PARSE_READ_COILS: "parse_read_coils",
  BUILD_READ_DISCRETE_INPUTS: "build_read_discrete_inputs",
  PARSE_READ_DISCRETE_INPUTS: "parse_read_discrete_inputs",
  BUILD_WRITE_SINGLE_COIL: "build_write_single_coil",
  PARSE_WRITE_SINGLE_COIL: "parse_write_single_coil",
  BUILD_WRITE_SINGLE_REGISTER: "build_write_single_register",
  PARSE_WRITE_SINGLE_REGISTER: "parse_write_single_register",
  BUILD_WRITE_MULTIPLE_COILS: "build_write_multiple_coils",
  PARSE_WRITE_MULTIPLE_COILS: "parse_write_multiple_coils",
  BUILD_WRITE_MULTIPLE_REGISTERS: "build_write_multiple_registers",
  PARSE_WRITE_MULTIPLE_REGISTERS: "parse_write_multiple_registers",
  // + ASCII 变体 build_ascii_* / parse_ascii_*

  // 阶段 1 新增 — TCP/UDP 路径
  OPEN_TCP_CONNECTION: "open_tcp_connection",
  OPEN_UDP_CONNECTION: "open_udp_connection",
  CLOSE_CONNECTION: "close_connection",
  TCP_READ_COILS: "tcp_read_coils",
  TCP_READ_DISCRETE_INPUTS: "tcp_read_discrete_inputs",
  TCP_READ_HOLDING_REGISTERS: "tcp_read_holding_registers",
  TCP_READ_INPUT_REGISTERS: "tcp_read_input_registers",
  TCP_WRITE_SINGLE_COIL: "tcp_write_single_coil",
  TCP_WRITE_SINGLE_REGISTER: "tcp_write_single_register",
  TCP_WRITE_MULTIPLE_COILS: "tcp_write_multiple_coils",
  TCP_WRITE_MULTIPLE_REGISTERS: "tcp_write_multiple_registers",
  // + UDP 变体
});
```

### `modbus-master-service.cjs` — 扩展编排

沿用现有 `readRegistersOnce` 模式,新增:
- `readCoilsOnce` / `readDiscreteInputsOnce`
- `writeSingleCoilOnce` / `writeSingleRegisterOnce`
- `writeMultipleCoilsOnce` / `writeMultipleRegistersOnce`
- TCP 路径的对应函数(调端到端命令,不走 transact)

### `main.cjs` — 新增 IPC handler

```javascript
// 串口路径(RTU + ASCII)
ipcMain.handle("nexus:read_coils_once", ...);
ipcMain.handle("nexus:read_discrete_inputs_once", ...);
ipcMain.handle("nexus:write_single_coil_once", ...);
ipcMain.handle("nexus:write_single_register_once", ...);
ipcMain.handle("nexus:write_multiple_coils_once", ...);
ipcMain.handle("nexus:write_multiple_registers_once", ...);

// TCP/UDP 路径
ipcMain.handle("nexus:open_tcp_connection", ...);
ipcMain.handle("nexus:open_udp_connection", ...);
ipcMain.handle("nexus:close_connection", ...);
ipcMain.handle("nexus:tcp_read_*", ...);
ipcMain.handle("nexus:tcp_write_*", ...);
```

### `preload.cjs` — 扩展白名单

加入上述所有新命令名。

---

## UI 变更

### 功能码下拉填充(`index.html:113`)

```html
<!-- 当前 -->
<select id="function-code">
  <option value="3">03 读保持寄存器</option>
  <option value="4">04 读输入寄存器</option>
</select>

<!-- 阶段 1 目标 -->
<select id="function-code">
  <optgroup label="读操作">
    <option value="1">01 读线圈</option>
    <option value="2">02 读离散输入</option>
    <option value="3" selected>03 读保持寄存器</option>
    <option value="4">04 读输入寄存器</option>
  </optgroup>
  <optgroup label="写操作">
    <option value="5">05 写单线圈</option>
    <option value="6">06 写单寄存器</option>
    <option value="15">15 写多线圈</option>
    <option value="16">16 写多寄存器</option>
  </optgroup>
</select>
```

### 写操作表单切换

当 FC 是写操作时,UI 动态切换:
- FC05/06:数量框隐藏,显示「写入值」输入框(bool / u16)
- FC15/16:数量框变为「写入值列表」(可批量输入)

### 连接方式选择(新增)

在连接区上方加传输方式单选:
```html
<div class="transport-selector">
  <label><input type="radio" name="transport" value="rtu" checked> RTU 串口</label>
  <label><input type="radio" name="transport" value="ascii"> ASCII 串口</label>
  <label><input type="radio" name="transport" value="tcp"> TCP</label>
  <label><input type="radio" name="transport" value="udp"> UDP</label>
  <label><input type="radio" name="transport" value="rtu-over-tcp"> RtuOverTcp</label>
  <label><input type="radio" name="transport" value="ascii-over-tcp"> AsciiOverTcp</label>
</div>
```
- 串口类(RTU/ASCII):显示 COM/波特率/校验表单(现有)
- 网络类(TCP/UDP/RtuOverTcp/AsciiOverTcp):显示 IP/端口/站号表单

### 激活现有 disabled 占位

- `写入数据` 按钮(index.html:117)→ 启用,触发写操作
- `readCommand()` 的 `[3,4].includes(functionCode)` 约束放开为 `[1,2,3,4,5,6,15,16]`

---

## 测试要求

### Rust 单元测试
- `modbus_pdu.rs`:每个 FC 的 build/parse 往返、边界值、异常响应
- `modbus_tcp.rs`:MBAP 编解码、事务 ID 递增、长度校验
- `modbus_ascii.rs`:LRC 向量、ASCII 帧编解码、非法 hex 字符
- 写操作的广播策略(unit 0 允许)

### JSONL 集成测试(`tests/jsonl_protocol.rs`)
- 每个 FC 的 build+parse 端到端
- `open_tcp_connection` → `tcp_read_*` → `close_connection` 全流程(用 `TcpListener` 回环)
- 错误码稳定性:断言每个错误路径返回正确 code

### Electron 测试
- `modbus-master-service.cjs`:新增 write/scan 函数的 mock 测试
- TCP 连接生命周期测试

### 冒烟测试
- `scripts/smoke-electron.mjs`:确认新 UI 元素加载无报错

---

## 风险与注意事项

1. **`Session` 引入破坏纯函数测试** —— `handle_line` 签名变更,现有 `tests/jsonl_protocol.rs` 的 spawn 方式仍可用(serve 内部持 Session),但单元测试需要重构为传 `&mut Session`。
2. **TCP 路径的并发** —— `Session::transact_tcp` 需要可变借用;如果未来要支持多连接并发事务,需要 `Arc<Mutex<Connection>>` 或消息通道。阶段 1 保持串行即可。
3. **写操作的安全性** —— 工业现场写操作有风险。UI 应加确认弹窗(对标 .NET Nexus 的 `WriteConfirmationService`)。阶段 1 可先做最小确认(confirm 对话框),完整审计链留后续。
4. **ASCII 帧的帧分隔** —— 串口 ASCII 模式靠 `:` 起始、CRLF 结束分隔帧,与 RTU 的 3.5 字符时间不同。`serial-service.cjs` 的 collector 需要适配 ASCII 模式。
5. **`modbus_rtu.rs` 的现有代码** —— 阶段 1 把 FC03/04 逻辑迁移到 `modbus_pdu.rs`,`modbus_rtu.rs` 只保留 RTU 帧(`RtuFrame`)+ CRC。保持向后兼容。

---

## 对标参考

| 参考 | 文件 | 借鉴点 |
|---|---|---|
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusTcpClient.cs` | FC API surface、MBAP 实现 |
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusRtuClient.cs` | RTU 帧响应长度判断 |
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusAsciiClient.cs` | LRC + ASCII 帧 |
| .NET Nexus | `Nexus/src/Nexus.Core/CrcCalculator.cs` | CRC16 + LRC 实现 |
| HSL Demo | `i3195/.../Modbus/FormModbus.cs` | TCP 主站表单 |
| HSL Demo | `i3195/.../Modbus/ModbusControl.cs` | FC17 读写一体测试控件 |
