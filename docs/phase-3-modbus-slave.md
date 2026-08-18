# 阶段 3:Modbus 从站模拟

> Rust 虚拟从站 + 赋值/置零 + 多站号 + 串口参数 + 客户端会话列表。
> 对标 Modbus Slave 软件 / HSL `FormModbusServer` / .NET `ModbusTcpSimulator`。
>
> 主索引:[spec-plan.md](./spec-plan.md)

---

## 目标

新增「从站模拟」产品形态。完成后:
- 用户可启动一个 Modbus TCP 从站(监听指定端口)
- 用户可启动一个 Modbus RTU/ASCII 串口从站(占用 COM 口)
- 4 个内存区(线圈/离散输入/输入寄存器/保持寄存器)可编辑、赋值、置零
- 从站响应 FC01–06、15、16
- 支持多站号(一个从站实例可响应多个 unit id)
- TCP 模式显示在线客户端会话列表

### 就绪标准
- [ ] Rust core:`modbus_slave.rs` 实现从站响应生成(FC01–06、15、16)
- [ ] Rust core:`SlaveServer` 持 `TcpListener`,每客户端一线程
- [ ] Rust core:内存区 4×65536,支持预置/赋值/置零/批量填充
- [ ] Rust core:RTU 串口从站(Electron 持句柄,JSONL 指令驱动)
- [ ] Electron:`modbus-slave-service.cjs` 管理从站生命周期
- [ ] UI:从站模拟 view(`data-view-panel="slave"`)完整界面
- [ ] UI:内存区可编辑表格、赋值/置零按钮、会话列表
- [ ] 测试:主站(阶段 1)可连接虚拟从站完成全 FC 往返(无硬件闭环)

---

## Rust core 变更

### 新模块:`modbus_slave.rs`

```rust
use std::sync::{Arc, Mutex};

/// 4 个内存区,每个 65536 项
pub struct SlaveMemory {
    pub coils: [bool; 65536],
    pub discrete_inputs: [bool; 65536],
    pub holding_registers: [u16; 65536],
    pub input_registers: [u16; 65536],
}

/// 从站配置
pub struct SlaveConfig {
    pub allowed_station_ids: Vec<u8>,   // 空 = 允许所有;否则白名单
    pub listen_mode: ListenMode,
    pub memory: Arc<Mutex<SlaveMemory>>,
}

pub enum ListenMode {
    Tcp { port: u16 },
    Serial { port_name: String, baud_rate: u32, /* ... */ },
}

/// 处理一个 PDU 请求,生成响应 PDU
pub fn handle_request(
    unit_id: u8,
    pdu: &[u8],
    config: &SlaveConfig,
) -> Result<Vec<u8>, SlaveError>;

/// TCP 从站服务器(阻塞,每客户端一线程)
pub struct TcpSlaveServer {
    config: SlaveConfig,
    listener: std::net::TcpListener,
    sessions: Arc<Mutex<Vec<ClientSession>>>,
}

impl TcpSlaveServer {
    pub fn new(config: SlaveConfig) -> Result<Self, SlaveError>;
    pub fn run(&self) -> !;   // 阻塞,accept 循环
    pub fn stop(&self);
    pub fn sessions(&self) -> Vec<ClientSessionInfo>;
}

pub struct ClientSession {
    pub peer_addr: String,
    pub connect_time: std::time::SystemTime,
    pub last_request: Option<std::time::SystemTime>,
    pub request_count: u64,
}
```

### FC 响应生成逻辑(`handle_request`)

| 收到 FC | 从站动作 | 响应 |
|---|---|---|
| 01 读线圈 | 读 `coils[addr..addr+qty]` | FC01 + byte_count + 位打包 |
| 02 读离散输入 | 读 `discrete_inputs[...]` | FC02 + 同上 |
| 03 读保持寄存器 | 读 `holding_registers[...]` | FC03 + byte_count + 大端 u16 |
| 04 读输入寄存器 | 读 `input_registers[...]` | FC04 + 同上 |
| 05 写单线圈 | 写 `coils[addr]`,值 `0xFF00`/`0x0000` | FC05 回显请求 |
| 06 写单寄存器 | 写 `holding_registers[addr]` | FC06 回显请求 |
| 15 写多线圈 | 批量写 `coils[addr..]` | FC15 + addr + qty |
| 16 写多寄存器 | 批量写 `holding_registers[addr..]` | FC16 + addr + qty |
| 站号不在白名单 | 不响应(静默丢弃) | 无 |
| 未知 FC | 异常码 0x01 | FC\|0x80 + 0x01 |
| 地址越界 | 异常码 0x02 | FC\|0x80 + 0x02 |

### JSONL 新增命令

| 命令 | payload | result |
|---|---|---|
| `start_tcp_slave` | `{slaveId, port, allowedStationIds:[]}` | `{running:true, port}` |
| `stop_slave` | `{slaveId}` | `{stopped:true}` |
| `slave_set_value` | `{slaveId, area:"holding", address, values:[u16]}` | `{set:true}` |
| `slave_set_coil` | `{slaveId, area:"coil", address, values:[bool]}` | `{set:true}` |
| `slave_clear` | `{slaveId, area?, addressRange?}` | `{cleared:true}` |
| `slave_fill` | `{slaveId, area, addressRange, pattern:"zero"\|"random"\|"increment"\|"sine"}` | `{filled:true}` |
| `slave_get_memory` | `{slaveId, area, address, count}` | `{values:[u16]\|[bool]}` |
| `slave_get_sessions` | `{slaveId}` | `{sessions:[{peerAddr, connectTime, lastRequest, requestCount}]}` |
| `slave_inject_exception` | `{slaveId, stationId, fc, exceptionCode}` | `{injected:true}` |

### 串口从站(特殊处理)

Windows COM 口独占 —— 两种方案:

**方案 A:Electron 持句柄,Rust 通过 JSONL 驱动**(推荐,阶段 3 采用)
- Electron 打开 COM 口,持续监听 RX
- 收到字节后通过 JSONL `slave_handle_serial_bytes` 发给 Rust
- Rust 解析帧、生成响应,通过 JSONL `slave_serial_respond` 告诉 Electron 发什么字节
- Electron 把响应字节写入串口

```javascript
// electron/slave-serial-bridge.cjs
serialPort.on("data", async (bytes) => {
  const response = await rustCore.request("slave_handle_serial_bytes", {
    slaveId, bytes
  });
  if (response.shouldRespond) {
    serialPort.write(Buffer.from(response.responseBytes));
  }
});
```

**方案 B:Rust 直接用 `serialport` crate**(阶段 6 考虑)
- 引入 `serialport` 依赖,打破零依赖原则
- 但简化架构,Rust 全权管理串口

---

## Electron 层变更

### 新文件:`electron/modbus-slave-service.cjs`

```javascript
class ModbusSlaveService {
  constructor({ rustCore, ensureRustCore }) { ... }

  async startTcpSlave({ slaveId, port, allowedStationIds }) {
    return this.rustCore.request("start_tcp_slave", { slaveId, port, allowedStationIds });
  }

  async stopSlave({ slaveId }) { ... }
  async setValue({ slaveId, area, address, values }) { ... }
  async clearArea({ slaveId, area }) { ... }
  async fillPattern({ slaveId, area, pattern }) { ... }
  async getSessions({ slaveId }) { ... }

  // 串口从站桥接
  startSerialSlaveBridge({ slaveId, serialService }) {
    serialService.onData((bytes) => this.handleSerialBytes(slaveId, bytes));
  }
}
```

### 新增 IPC handler

```javascript
ipcMain.handle("nexus:start_tcp_slave", ...);
ipcMain.handle("nexus:stop_slave", ...);
ipcMain.handle("nexus:slave_set_value", ...);
ipcMain.handle("nexus:slave_set_coil", ...);
ipcMain.handle("nexus:slave_clear", ...);
ipcMain.handle("nexus:slave_fill", ...);
ipcMain.handle("nexus:slave_get_memory", ...);
ipcMain.handle("nexus:slave_get_sessions", ...);
ipcMain.handle("nexus:start_serial_slave", ...);
```

---

## UI 变更

### 新建从站模拟 view

`<section data-view-panel="slave" hidden>` —— 全新界面:

```
┌─ 从站模拟 ────────────────────────────────────────────────────┐
│                                                               │
│  ┌─ 监听配置 ────────────────────────────────────────────┐   │
│  │ 模式: ○ TCP [端口 5020]  ○ RTU 串口 [COM5] [9600]   │   │
│  │ 允许站号: [1,2,3-10] (空=全部)                        │   │
│  │ [启动从站]  [停止]    状态: ● 运行中(2 个客户端在线) │   │
│  └───────────────────────────────────────────────────────┘   │
│                                                               │
│  ┌─ 内存区 ─── [线圈] [离散输入] [输入寄存器] [保持寄存器] ┐ │
│  │ 地址范围: [0] 到 [99]  [跳转]                          │ │
│  │ ┌──────────────────────────────────────────────────┐   │ │
│  │ │ 地址  值(十六进制)  值(十进制)  备注           │   │ │
│  │ │ 0     0x1234         4660         —              │   │ │
│  │ │ 1     0x0000         0            —              │   │ │
│  │ │ 2     0xABCD         43981        传感器A        │   │ │
│  │ │ ...   (可双击编辑)                                │   │ │
│  │ └──────────────────────────────────────────────────┘   │ │
│  │ [置零此区] [随机填充] [递增填充] [正弦波填充]           │ │
│  └───────────────────────────────────────────────────────┘   │
│                                                               │
│  ┌─ 客户端会话(TCP 模式)──────────────────────────────┐   │
│  │ 远程地址        连接时间      最后请求      请求数   │   │
│  │ 127.0.0.1:5432  14:30:01      14:32:15.3    142     │   │
│  │ 192.168.1.100   14:31:20      14:32:14.8    89      │   │
│  └───────────────────────────────────────────────────────┘   │
│                                                               │
│  ┌─ 流量日志(最近 100 条)─────────────────────────────┐   │
│  │ 时间        方向  站号  FC    hex                     │   │
│  │ 14:32:15.3  RX    1     03    01 03 00 00 00 0A ...  │   │
│  │ 14:32:15.3  TX    1     03    01 03 14 00 64 ...     │   │
│  └───────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────┘
```

### 内存区表格交互
- 双击单元格可编辑值(对标 Modbus Slave 软件)
- 编辑后调 `nexus:slave_set_value` 即时更新内存
- 置零按钮:整区清零或选区清零
- 填充模式:
  - 随机:`Math.random()` 填充
  - 递增:`address * 10` 等
  - 正弦波:`Math.sin(addr / 10) * 1000`(对标 .NET `ModbusTcpSimulator` 的 sine 种子)

### 启动从站的端口冲突检测
- 启动 TCP 从站前检查端口是否被占用
- 串口从站前检查 COM 是否已被主站打开(同一会话内互斥)

---

## 测试要求

### Rust 单元测试
- `modbus_slave.rs::handle_request`:每个 FC 的正常 + 异常响应
- 站号白名单过滤
- 内存区边界(地址 65535、跨区访问拒绝)

### JSONL 集成测试(关键:无硬件闭环)
- 启动 TCP 从站 → 用阶段 1 的 `tcp_read_*` / `tcp_write_*` 连接它 → 全 FC 往返
- 这是**最重要的测试** —— 证明主站+从站可在无硬件情况下端到端验证

```rust
// 伪代码
let slave = start_tcp_slave(port=5020);
slave.set_value(area="holding", address=0, values=[0x1234, 0xABCD]);

let result = tcp_read_holding_registers(connectionId, address=0, quantity=2);
assert_eq!(result.registers, vec![0x1234, 0xABCD]);

slave.stop();
```

### Electron 测试
- `modbus-slave-service.cjs`:start/stop/setValue/clear 生命周期
- 串口从站桥接的字节往返(mock serial)

### 冒烟测试
- 从站 tab 可切换
- 启动/停止从站无报错

---

## 风险与注意事项

1. **TCP 从站的线程安全** —— `SlaveMemory` 用 `Arc<Mutex<>>` 共享;多客户端并发读时不能阻塞太久。读操作用 `read().unwrap()`(共享锁),写操作用 `write().unwrap()`(独占锁)。
2. **串口从站的延迟** —— 方案 A(JSONL 桥接)有跨进程延迟,RTU 3.5 字符时间在 9600bps 下 ≈ 4ms,JSONL 往返可能超时。阶段 3 先做 TCP 从站,串口从站标注为"实验性"。
3. **端口冲突** —— 502 是 Modbus TCP 标准端口,可能被系统占用。默认用 5020,UI 可改。
4. **内存区的初始种子数据** —— 对标 .NET `ModbusTcpSimulator` 的种子(128, 256, 365, 正弦波),让从站启动即有"活"数据,方便测试。
5. **异常注入** —— `slave_inject_exception` 用于测试主站的异常处理能力。需要 UI 配置(哪个 FC 返回哪个异常码)。

---

## 对标参考

| 参考 | 文件 | 借鉴点 |
|---|---|---|
| .NET Nexus | `Nexus/src/Nexus.App/Services/ModbusTcpSimulator.cs` | 内存区 + FC 响应 + 种子数据 |
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusTcpServer.cs` | 完整 539 行 TCP 服务器(FC01-06,08,15,16,17,43) |
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusVirtualServer.cs` | 轻量 ConcurrentDictionary 版本 |
| HSL Demo | `i3195/.../Modbus/FormModbusServer.cs` | 从站表单 + 会话列表 UI |
| HSL Demo | `i3195/.../DemoControl/UserControlReadWriteServer.cs` | 从站 5-tab host(LogInfo/BatchRead/DataTable/Simulate/Others) |
