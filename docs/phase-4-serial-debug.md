# 阶段 4:串口调试

> 透明收发原始串口字节的终端工具 + CRC/LRC 校验 + 报文解析。
> 对标 HSL `FormSerialDebug` / `DebugControl`。这是唯一允许渲染层提交任意字节的模式。
>
> 主索引:[spec-plan.md](./spec-plan.md)

---

## 目标

新增「串口调试」产品形态。完成后:
- 用户可在 hex/ASCII/Modbus-RTU 三种模式下发送任意字节
- 用户可控制收发开关(允许接收、允许发送、加校验)
- 用户可查看带时间戳的收发记录,每行自动尝试 Modbus 报文解析
- 用户可用 CRC-16/LRC 计算器离线校验
- 收到的帧自动判定 CRC/LRC 正确性

### 就绪标准
- [ ] Electron:`serial-debug-service.cjs` 实现透明收发(非 Modbus 事务模型)
- [ ] Rust core:CRC/LRC 计算命令(复用阶段 1 的 `modbus_rtu.rs` / `modbus_ascii.rs`)
- [ ] Rust core:`frame_parser.rs` 在线报文解析(轻量版,完整版在阶段 6)
- [ ] UI:串口调试 view(`data-view-panel="debug"`)完整界面
- [ ] UI:hex 终端、收发控制开关、校验工具、流量记录表

---

## 核心设计:突破"渲染层不能提交任意字节"

当前架构的铁律是「渲染层永远不能提交任意串口字节」。**串口调试模式是刻意打破这个铁律的唯一例外**,通过**显式模式切换**保护:

```
主站模式:    渲染层 → build_read_* → Electron → serial.transact(受控)
从站模式:    渲染层 → slave_set_value → Rust 响应生成(受控)
串口调试模式: 渲染层 → debug_send_bytes → Electron → serial.write(原始!)  ← 唯一例外
```

**安全措施**:
- 进入串口调试 view 时,UI 显示醒目的「调试模式 — 原始字节发送」警告条
- preload 白名单单独标记 `debug_*` 命令
- 串口调试模式与主站模式互斥(不能同时激活)

---

## Electron 层变更

### 新文件:`electron/serial-debug-service.cjs`

不同于 `serial-service.cjs` 的 `transact()`(请求-响应模型),调试服务是**持续监听 + 按需发送**:

```javascript
class SerialDebugService {
  constructor({ serialService }) {
    this.serialService = serialService;
    this.allowReceive = true;
    this.allowSend = true;
    this.appendCrc = false;
    this.sendMode = "hex";           // "hex" | "ascii" | "modbus-rtu"
    this.frameDelimiter = 3.5;       // 字符时间(RTU 帧分隔)
    this.receivedFrames = [];        // 收发记录(环形缓冲,最多 1000 条)
    this.onFrameCallback = null;     // 帧回调(推送到渲染层)
  }

  // 启动持续接收监听
  startListening() {
    this.serialService.port.on("data", (chunk) => {
      this.accumulate(chunk);
    });
  }

  // 帧累积 + 超时分隔
  accumulate(chunk) {
    if (!this.allowReceive) return;
    this.buffer = Buffer.concat([this.buffer, chunk]);
    this.resetTimer();   // 超时后触发 onFrame(this.buffer)
  }

  // 发送任意字节
  async send({ bytes, mode }) {
    if (!this.allowSend) throw new Error("发送已禁用");
    let payload = Buffer.from(bytes);
    if (this.appendCrc || mode === "modbus-rtu") {
      const crc = await this.rustCore.request("compute_crc16", { bytes: [...payload] });
      payload = Buffer.concat([payload, Buffer.from([crc & 0xFF, crc >> 8])]);
    }
    await this.serialService.port.write(payload);
    await this.serialService.port.drain();
    this.recordFrame({ direction: "TX", bytes: [...payload] });
  }

  // 帧到达时记录 + 推送
  onFrame(frame) {
    const record = {
      timestamp: Date.now(),
      direction: "RX",
      bytes: [...frame],
      hex: frame.toString("hex").match(/../g).join(" "),
    };
    this.receivedFrames.push(record);
    this.onFrameCallback?.(record);
  }
}
```

### 新增 IPC handler

```javascript
ipcMain.handle("nexus:debug_send", (e, { bytes, mode }) => debugService.send({ bytes, mode }));
ipcMain.handle("nexus:debug_set_receive", (e, { enabled }) => debugService.allowReceive = enabled);
ipcMain.handle("nexus:debug_set_send", (e, { enabled }) => debugService.allowSend = enabled);
ipcMain.handle("nexus:debug_set_crc", (e, { enabled }) => debugService.appendCrc = enabled);
ipcMain.handle("nexus:debug_clear_log", ...);
ipcMain.handle("nexus:debug_get_log", ...);

// 帧推送(主进程 → 渲染层)
mainWindow.webContents.on("did-finish-load", () => {
  debugService.onFrameCallback = (record) => {
    mainWindow.webContents.send("nexus:debug_frame", record);
  };
});
```

---

## Rust core 变更

### 新增 JSONL 命令(校验 + 在线解析)

| 命令 | payload | result |
|---|---|---|
| `compute_crc16` | `{bytes:[u8]}` | `{crc:u16, crcHexLo, crcHexHi}` |
| `compute_lrc` | `{bytes:[u8]}` | `{lrc:u8, lrcHex}` |
| `verify_crc16` | `{bytes:[u8], receivedCrc:u16}` | `{valid:bool, expected:u16}` |
| `verify_lrc` | `{bytes:[u8], receivedLrc:u8}` | `{valid:bool, expected:u8}` |
| `parse_frame_online` | `{bytes:[u8], transport:"rtu"\|"ascii"\|"tcp"}` | `{parsed:FrameInfo}` |

`FrameInfo`(轻量版,完整版在阶段 6):
```json
{
  "isValid": true,
  "transport": "rtu",
  "unitId": 1,
  "functionCode": 3,
  "functionName": "读保持寄存器",
  "isException": false,
  "address": 0,
  "quantity": 10,
  "data": [0x1234, 0xABCD],
  "crcValid": true,
  "crcHex": "C5CD",
  "direction": "request",
  "summary": "站号1 FC03 读保持寄存器 地址0 数量10"
}
```

---

## UI 变更

### 新建串口调试 view

`<section data-view-panel="debug" hidden>`:

```
┌─ 串口调试  ⚠ 原始字节发送模式 ────────────────────────────────┐
│                                                               │
│  ┌─ 收发控制 ────────────────────────────────────────────┐   │
│  │ ☑ 允许接收   ☑ 允许发送   ☐ 自动追加 CRC   ☐ 时间戳   │   │
│  │ 帧分隔超时: [3.5 字符时间 ▼]                          │   │
│  └───────────────────────────────────────────────────────┘   │
│                                                               │
│  ┌─ 发送 ────────────────────────────────────────────────┐   │
│  │ 发送模式: ○ HEX  ○ ASCII  ○ Modbus RTU(自动CRC)    │   │
│  │ ┌──────────────────────────────────────────────────┐ │   │
│  │ │ 01 03 00 00 00 0A                                │ │   │
│  │ └──────────────────────────────────────────────────┘ │   │
│  │ [发送] [清空输入] [发送并等待] [循环发送(每 1000ms)] │   │
│  └───────────────────────────────────────────────────────┘   │
│                                                               │
│  ┌─ 收发记录 ────────────────────────────────────────────┐   │
│  │ 时间          方向  hex                          解析   │   │
│  │ 14:32:01.123  TX    01 03 00 00 00 0A C5 CD     FC03  │   │
│  │ 14:32:01.245  RX    01 03 14 00 64 ... CRC=✓    10寄存 │   │
│  │ 14:32:02.001  TX    01 06 00 05 00 FF C8 32     FC06  │   │
│  │ ...                                                    │   │
│  │                                          [清空] [导出] │   │
│  └───────────────────────────────────────────────────────┘   │
│                                                               │
│  ┌─ 校验工具 ────────────────────────────────────────────┐   │
│  │ 输入 hex: [01 03 00 00 00 0A              ]           │   │
│  │ [计算 CRC-16] → 0xCDC5 (低字节 C5, 高字节 CD)         │   │
│  │ [计算 LRC]   → 0xF1                                   │   │
│  └───────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────┘
```

### 发送模式详解

| 模式 | 输入 | 发送内容 |
|---|---|---|
| **HEX** | `01 03 00 00 00 0A` | 原样发送这 6 字节(可选追加 CRC) |
| **ASCII** | `Hello` | 发送 ASCII 字节 `48 65 6C 6C 6F` |
| **Modbus RTU** | `01 03 00 00 00 0A` | 自动计算并追加 CRC → 发送 8 字节 |

### 循环发送
- 可配间隔(100–60000ms)
- 用于压力测试或持续探测
- 有明显的「停止循环」按钮

### 点击行解析
- 点击流量记录的任意一行 → 展开解析详情(调用 `parse_frame_online`)
- 显示字段拆解:unit / FC / 地址 / 数量 / 数据 / CRC 状态

---

## 测试要求

### Rust 单元测试
- `compute_crc16` / `compute_lrc`:已知向量(`crc16_modbus(b"123456789") == 0x4B37`)
- `parse_frame_online`:RTU/ASCII/TCP 各 transport 的正常 + 异常帧解析

### Electron 测试
- `serial-debug-service.cjs`:send/receive 控制、CRC 追加、帧累积超时
- mock serial port 验证字节往返

### 冒烟测试
- 串口调试 tab 可切换
- 警告条显示
- 发送/接收开关可切换

---

## 风险与注意事项

1. **帧分隔的超时精度** —— Node.js 的 `setTimeout` 精度约 1-4ms,RTU 3.5 字符时间在 115200bps 下 ≈ 0.3ms,无法精确分隔。方案:用字节间空闲 heuristic(超过 N ms 无新字节即判定帧结束),N 可配(默认 10ms)。
2. **缓冲区溢出** —— 高波特率下接收缓冲可能快速膨胀。`receivedFrames` 用环形缓冲,最多保留 1000 条,超出丢弃最旧。
3. **调试模式与主站模式的串口互斥** —— 同一 COM 口不能同时被主站和调试模式使用。切换 view 时检查端口占用。
4. **原始字节发送的安全确认** —— 发送前可选确认弹窗(可关闭),防止误发危险指令(如 FC06 写寄存器到关键地址)。

---

## 对标参考

| 参考 | 文件 | 借鉴点 |
|---|---|---|
| HSL Demo | `i3195/.../HslDebug/FormSerialDebug.cs` | 串口终端完整 UI(端口/波特/校验/流控配置 + hex 收发) |
| HSL Demo | `i3195/.../HslDebug/DebugControl.cs` | 共享 hex 渲染组件(时间戳、方向着色) |
| HSL Demo | `i3195/.../HslDebug/FormTcpDebug.cs` | TCP 客户端调试(阶段 6 扩展) |
| HSL Demo | `i3195/.../HslDebug/FormByteTransfer.cs` | 字节编解码计算器(阶段 6) |
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusPacketParser.cs` | 结构化报文解析(643 行) |
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusDiagnostics.cs` | 人可读报文解码(599 行) |
