# 阶段 2:Modbus 主站高级

> 扫描(站号 / 波特率)+ 指令设置 + 轮询 + 多数据类型(4 字节序)。
> 依赖 [阶段 1](./phase-1-modbus-master-core.md) 的全 FC + 全传输编解码就绪。
>
> 主索引:[spec-plan.md](./spec-plan.md)

---

## 目标

把主站从「单次手动读写」推进到「自动化测试工作流」。完成后:
- 用户可扫描 1–247 站号,快速发现在线设备
- 用户可扫描常用波特率,识别未知设备配置
- 用户可配置指令列表(批量、有序执行)
- 用户可连续轮询指定点位,实时刷新
- 读回的寄存器可按 Int16/Int32/Float32/Float64/String + 4 字节序解码显示

### 就绪标准
- [ ] Rust core:`value_codec.rs` 实现 7 种数据类型 + 4 字节序,纯函数
- [ ] Rust core:站号扫描命令(并行探测 1–247)
- [ ] Rust core:波特率扫描命令(遍历常用波特率)
- [ ] Electron:轮询调度器(`setInterval` 驱动,复用阶段 1 的 read_once)
- [ ] UI:`#display-type` / `#byte-order` 下拉激活
- [ ] UI:`#poll-interval` + `连续轮询` 按钮激活
- [ ] UI:`扫描站号` 按钮激活,扫描结果填充 console scan 面板
- [ ] UI:指令设置面板(批量指令列表)

---

## Rust core 变更

### 新模块:`value_codec.rs`(多数据类型解码)

```rust
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum ByteOrder {
    Abcd,   // BigEndian
    Dcba,   // LittleEndian
    Badc,   // MidBigEndian (字序交换)
    Cdab,   // MidLittleEndian (字节序交换)
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum DataType {
    Boolean,
    Int16, UInt16,
    Int32, UInt32,
    Int64, UInt64,
    Float32, Float64,
    String,
}

// 纯函数:把原始寄存器数组解码成各种类型
pub fn decode_value(
    registers: &[u16],
    offset: usize,
    count: usize,
    data_type: DataType,
    byte_order: ByteOrder,
) -> Result<DecodedValue, ModbusError>;

pub enum DecodedValue {
    Bool(bool),
    Int16(i16), UInt16(u16),
    Int32(i32), UInt32(u32),
    Int64(i64), UInt64(u64),
    Float32(f32), Float64(f64),
    String(String),
}

// 反向:把值编码成寄存器数组(写多寄存器时用)
pub fn encode_value(
    value: &DecodedValue,
    byte_order: ByteOrder,
) -> Result<Vec<u16>, ModbusError>;
```

**字节序实现**(对标 .NET `DataConverter`):
- `ABCD`:register[0] 高字、register[1] 低字,字内大端
- `DCBA`:register[0] 低字、register[1] 高字,字内小端
- `BADC`:字内字节交换(字序不换)
- `CDAB`:字序交换(字内不换)

**字符串编码**:ASCII / UTF-8 / UTF-16(对标 .NET 的 `StringEncoding` 枚举)。

### JSONL 新增命令

**数据类型解码**(纯计算):
| 命令 | payload | result |
|---|---|---|
| `decode_values` | `{registers:[u16], dataType, byteOrder, offset, count}` | `{values:[DecodedValue]}` |
| `encode_value` | `{value, dataType, byteOrder}` | `{registers:[u16]}` |

**站号扫描**(端到端,复用阶段 1 的连接):
| 命令 | payload | result |
|---|---|---|
| `scan_station_ids` | `{connectionId?, transport, portOrCom, baudRate?, range:{start:1,end:247}, timeoutMs:500}` | `{found:[{stationId, firstResponseMs}], scanned:247, elapsedMs}` |

扫描策略:
- 对每个 stationId 发 FC03 读 1 个寄存器(地址 0)
- 串行(串口)或并行批次(TCP,每批 10-20 个)
- 超时即视为离线,不报错
- 返回在线列表 + 首次响应时间

**波特率扫描**(仅串口):
| 命令 | payload | result |
|---|---|---|
| `scan_baud_rate` | `{comPort, stationId:1, baudRates:[1200,2400,4800,9600,...], timeoutMs:500}` | `{foundBaudRate, confidenceMs}` |

扫描策略:
- 遍历 `[1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200]`
- 每个波特率用指定 stationId 发 FC03 读
- 首个成功响应即为命中(可选:连续 3 次确认提高可信度)

---

## Electron 层变更

### 轮询调度器(新文件 `electron/poll-scheduler.cjs`)

**阶段 2 的轮询用 Electron 侧 `setInterval` 实现**(不需要协议升级),每个 tick 调一次 read_once:

```javascript
class PollScheduler {
  constructor({ rustCore, serialService, ensureRustCore }) { ... }

  start({ addresses, intervalMs, transport, connectionId }) {
    this.timer = setInterval(async () => {
      for (const addr of addresses) {
        const result = await this.readOnce(addr);
        this.emit("data", { address: addr, value: result });
      }
    }, intervalMs);
  }

  stop() { clearInterval(this.timer); }
}
```

> **注**:阶段 5 会把这个逻辑下沉到 Rust 侧(流式推送),消除 setInterval 开销。阶段 2 先用 Electron 侧实现快速可用。

### 新增 IPC handler

```javascript
ipcMain.handle("nexus:scan_station_ids", ...);
ipcMain.handle("nexus:scan_baud_rate", ...);
ipcMain.handle("nexus:start_poll", ...);   // 返回 pollId
ipcMain.handle("nexus:stop_poll", ...);    // {pollId}
ipcMain.handle("nexus:decode_values", ...);
ipcMain.handle("nexus:encode_value", ...);
```

轮询结果通过 `webContents.send("nexus:poll_data", {pollId, address, value})` 推送到渲染层。

---

## UI 变更

### 激活数据类型 + 字节序下拉

```html
<!-- 当前 disabled -->
<select id="display-type" disabled><option>UInt16</option></select>
<select id="byte-order" disabled><option>AB</option></select>

<!-- 阶段 2 激活 -->
<select id="display-type">
  <option value="UInt16">UInt16</option>
  <option value="Int16">Int16</option>
  <option value="UInt32">UInt32</option>
  <option value="Int32">Int32</option>
  <option value="Float32">Float32</option>
  <option value="Float64">Float64</option>
  <option value="String">String</option>
</select>
<select id="byte-order">
  <option value="ABCD">ABCD (大端)</option>
  <option value="DCBA">DCBA (小端)</option>
  <option value="BADC">BADC (字内交换)</option>
  <option value="CDAB">CDAB (字序交换)</option>
</select>
```

`renderRegisters()` 改为按选定的 `displayType` + `byteOrder` 解码:
- 读 2 个寄存器 → 按 Float32 + CDAB 解出 `3.14`
- 读 4 个寄存器 → 按 Float64 + ABCD 解出 `3.141592653589`

### 激活轮询(`#poll-interval` + `连续轮询`)

```html
<!-- 当前 disabled -->
<input id="poll-interval" type="number" value="1000" disabled />
<button type="button" disabled>连续轮询</button>

<!-- 阶段 2 激活 -->
<input id="poll-interval" type="number" value="1000" min="100" max="60000" />
<button id="start-poll" type="button">连续轮询</button>
<button id="stop-poll" type="button" disabled>停止</button>
```

轮询时的表格更新策略:
- **keyed-row 更新**(而非 `replaceChildren`):每个地址对应一个 `<tr data-addr="...">`,只更新值单元格
- 高频轮询(>2Hz)时开启"闪烁"高亮提示值变化

### 激活站号扫描(`扫描站号` 按钮)

点击后:
1. 弹出扫描配置(范围 1–247、超时)
2. 调 `nexus:scan_station_ids`
3. 结果填充到 console 的 scan 子面板(`index.html:135` 已有骨架)
4. 扫描中的进度条显示 `已扫描 X/247`

### 指令设置面板(新增)

在 command-panel 下加一个可展开的「指令列表」:
```
┌─ 指令列表 ───────────────────────────────────────┐
│ [+] 添加指令                                     │
│ ┌────────────────────────────────────────────┐  │
│ │ #1  FC03  站号 1  地址 0    数量 10  [删除]│  │
│ │ #2  FC06  站号 1  地址 100  值 1234  [删除]│  │
│ │ #3  FC03  站号 2  地址 0    数量 4   [删除]│  │
│ └────────────────────────────────────────────┘  │
│ [执行全部] [保存为配方] [加载配方]               │
└─────────────────────────────────────────────────┘
```
- 「执行全部」:按顺序依次执行每条指令,记录每条结果
- 「保存为配方」:序列化为 JSON 存到本地(对标 HSL 的 XML 持久化)
- 「加载配方」:从 JSON 恢复指令列表

---

## 测试要求

### Rust 单元测试
- `value_codec.rs`:每种数据类型 × 每种字节序的组合(7 × 4 = 28 组)
- `value_codec.rs`:边界值(Int32 溢出、Float NaN、空字符串等)
- 站号扫描逻辑(mock 连接,断言 found 列表正确)

### JSONL 集成测试
- `decode_values` 端到端(已知输入 → 已知输出)
- `scan_station_ids` 用 `TcpListener` + 虚拟从站模拟(依赖阶段 3 的虚拟从站,或用简单 echo)

### Electron 测试
- `poll-scheduler.cjs`:start/stop 生命周期、interval 精度
- 轮询数据推送回调

### 冒烟测试
- 数据类型/字节序下拉可选
- 轮询按钮可点击

---

## 风险与注意事项

1. **轮询频率 vs 串口带宽** —— RTU 串口 9600bps 下,一次 FC03 读 10 寄存器 ≈ 30ms;轮询间隔 < 50ms 会堆积。UI 应根据波特率自动设置最小间隔提示。
2. **轮询期间的手动操作冲突** —— 轮询占用 `busy` 锁时,用户点「读取一次」应被阻止或排队。需要 `busy` 状态在轮询期间正确反映。
3. **多数据类型的寄存器数量推断** —— Float32 需要 2 个寄存器,Float64 需要 4 个。用户选 Float32 但 quantity=1 时应自动校正或报错。
4. **站号扫描的超时累积** —— 247 个站号 × 500ms 超时 = 2 分钟(最坏全离线)。TCP 路径应并行批次(如每批 20 个并发),串口路径只能串行。
5. **指令配方的持久化格式** —— JSON Schema 需要版本化,防止未来格式变更破坏旧配方。

---

## 对标参考

| 参考 | 文件 | 借鉴点 |
|---|---|---|
| .NET Nexus | `Nexus/src/Nexus.Core/DataConverter.cs` | 4 字节序实现 |
| .NET Nexus | `Nexus/src/Nexus.Core/StringConverter.cs` | 多字符串编码 |
| .NET Nexus | `Nexus/src/Nexus.App/ViewModels/DeviceScannerViewModel.cs` | 并行站号扫描 |
| HSL Demo | `i3195/.../Modbus/StationSearchControl.cs` | 站号扫描 UI |
| HSL Demo | `i3195/.../DemoControl/UserControlReadWriteDevice.cs` | 批量读 + 数据表持久化 |
