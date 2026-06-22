# Phase 2 收尾：Bug 修复 + 读侧闭环

**日期**：2026-06-22
**范围**：6 个协议（GeSrtp / Xinje / Robot.UR / Robot.Efort / OpenProtocol / Robot.Estun + Robot.Yamaha）
**目标**：修复已交付但错误的功能性 Bug，补齐 C 级协议的读侧/写侧闭环，新增 `IRobotControlDevice` 控制接口。
**不在范围**：运动控制（MoveJ/MoveL 下发）、OpenProtocol push 订阅、Beckhoff Handle 缓存、B 级协议功能扩展。

---

## 1. 背景与动机

### 1.1 现状（2026-06-22 实测）

- 协议库 54 个，测试 3886 通过，Release 构建 0 警告。
- 并行审计（机器人 9 个 + PLC/工具 11 个协议）发现三类问题：
  1. **功能性 Bug**（已交付但错误，会读错地址）
  2. **C 级覆盖度**（脚手架级，读侧或写侧空壳）
  3. B 级覆盖度（基本可用，缺高级能力）

### 1.2 本轮聚焦前两类

按"功能覆盖度"判定（而非行数），识别出 6 个协议需要处理。B 级协议与运动控制留待后续轮次。

---

## 2. 架构

### 2.1 新增接口：`Nexus.Core.IRobotControlDevice`

**位置**：`src/Nexus.Core/IRobotControlDevice.cs`（新文件）

**设计原则**：与 `IReadWriteDevice`（数据读写）正交——此接口表达"动作"而非"数据"。机器人控制是"执行一个动作"（启动程序、复位错误），语义不同于"向地址写数据"。

```csharp
namespace Nexus
{
    /// <summary>
    /// 机器人控制设备的控制语义接口。
    /// 与 IReadWriteDevice（数据读写）正交——表达"动作"而非"数据"。
    /// </summary>
    public interface IRobotControlDevice
    {
        /// <summary>写单个数字输出（机器人本体 IO）。</summary>
        OperateResult WriteDigitalOutput(int index, bool value);

        /// <summary>批量写数字输出。</summary>
        OperateResult WriteDigitalOutputs(int[] indices, bool[] values);

        /// <summary>启动程序/任务。programName 为空时启动当前加载的程序。</summary>
        OperateResult StartProgram(string? programName = null);

        /// <summary>停止程序/任务。</summary>
        OperateResult StopProgram();

        /// <summary>复位错误/报警。</summary>
        OperateResult ResetError();

        /// <summary>设置速度倍率（0-100）。</summary>
        OperateResult SetSpeedRatio(double percent);
    }
}
```

**为什么不放进 `IReadWriteDevice`**：`Write(string address, ...)` 是"向某地址写数据"，机器人控制是"执行动作"。混在一起会让契约模糊。

### 2.2 实现者矩阵

| 协议 | 实现 `IRobotControlDevice` | 本轮动作 |
|---|---|---|
| Robot.Efort | ✅ | 新增写侧 + 实现接口 |
| Robot.Estun | ✅ | 适配现有方法 + 补 WriteDO |
| Robot.Yamaha | ✅ | 补 WriteDO + 适配 |
| Robot.UR | ❌ | 本轮只补读侧（控制走 URScript，语义差异大）|
| Robot.ABB/Fanuc/Kuka/Yaskawa/Staubli | ❌ | 后续轮次 |

### 2.3 OpenProtocol 独立处理

OpenProtocol 不是机器人，不实现 `IRobotControlDevice`。Job 管理/订阅作为 `OpenProtocolClient` 类方法暴露。如后续需抽象，再提炼 `ITighteningDevice`——YAGNI，本轮不做。

### 2.4 文件所有权（零冲突并行）

```
src/Nexus.Core/IRobotControlDevice.cs         — 新文件
src/Nexus.GeSrtp/GeSrtpClient.cs              — Bug 修复
src/Nexus.Xinje/XinjeClient.cs                — Bug 修复
src/Nexus.Robot.Ur/UrClient.cs                — RT 读侧实现
src/Nexus.Robot.Ur/UrVirtualServer.cs         — 扩展 RT 流模拟
src/Nexus.Robot.Efort/EfortClient.cs          — 写侧实现
src/Nexus.Robot.Efort/EfortVirtualServer.cs   — 扩展写响应
src/Nexus.Robot.Estun/EstunClient.cs          — 接口适配
src/Nexus.Robot.Yamaha/YamahaRcxClient.cs     — WriteDO 补充
src/Nexus.OpenProtocol/OpenProtocolClient.cs  — Job + 订阅 + 清理伪实现
src/Nexus.OpenProtocol/OpenProtocolVirtualServer.cs — 新增（轻量 MID 响应）
```

每个协议改自己的文件，无交叉。`Nexus.Core` 只加一个新文件，不碰既有代码。

---

## 3. 各协议详细改动

### 3.1 Bug 修复

#### 3.1.1 GeSrtp — `Incr` 丢前缀（`GeSrtpClient.cs:230`）

**现状**（错误）：
```csharp
private static string Incr(string address, int offset = 1) {
    var (mt, num) = ParseAddress(address);
    return $"R{num + offset}";   // 永远返回 R，丢失 I/Q/M/T
}
```

**影响**：`%M/%I/%Q/%T/%AI/%AQ` 区域的 32/64 位读会读错地址（读到 R 区域）。

**`Incr` 的调用者**（6 处，均在 `GeSrtpClient.cs`）：
- `ReadInt32`（行 186）：读高低字
- `ReadInt64`（行 188）：读 4 个字
- `ReadBytes`（行 193）：按字循环读
- `Write(address, int)`（行 198）：写高低字
- `Write(address, long)`（行 200）：写 4 个字
- `Write(address, byte[])`（行 205）：按字循环写

即所有 GeSrtp 的 32/64 位读写和批量字节读写，只要地址前缀不是 R，都会触发此 Bug。

**修复**：保留原前缀，新增显式映射表替代 char 算术。
```csharp
private static string Incr(string address, int offset = 1) {
    var (mt, num) = ParseAddress(address);
    return $"{MemTypeToPrefix(mt)}{num + offset}";
}

private static string MemTypeToPrefix(byte mt) {
    switch (mt) {
        case 0x08: return "R";
        case 0x10: return "I";
        case 0x12: return "Q";
        case 0x14: return "M";
        case 0x16: return "T";
        case 0x18: return "AI";
        case 0x1A: return "AQ";
        default: throw new ArgumentException($"未知内存类型: 0x{mt:X2}");
    }
}
```

#### 3.1.2 GeSrtp — `WriteBools` 反推前缀（`GeSrtpClient.cs:282`）

**现状**（错误）：
```csharp
string addr = $"{(char)('A' + (mt - 0x08))}{off + i}";
// mt=0x10(I) → '(' ; mt=0x12(Q) → '*' ; mt=0x14(M) → ',' ; mt=0x16(T) → '.'
// 全部算出错误字符
```

**修复**：同一处改用 `MemTypeToPrefix(mt)`。

#### 3.1.3 Xinje — `ReadPlcModel` 偏移（`XinjeClient.cs:288`）

**现状**（错误）：
```csharp
int dataStart = r.Content.Length > 3 ? 2 : 0;
// 三元判断与 SendReceive 返回结构不符，型号字符串错位
```

**修复**：`SendReceive` 返回已去 MBAP 头，前缀固定为 `[FC][byteCount][data...]`。
```csharp
if (r.Content.Length < 3) return OperateResult<string>.Failed("响应过短");
if ((r.Content[0] & 0x80) != 0)
    return OperateResult<string>.Failed($"Modbus 异常码: {r.Content[2]}");
int dataStart = 2;
```

### 3.2 UR Real-Time Interface 读侧闭环

**问题**：`Connect()` 不连 RT 端口（`_rtClient` 始终 null），导致：
- `ReadFloatRegister` 等 11 个 ReadXxx 全部返回失败
- `PollMonitors` 订阅功能实质损坏（调用失败的 ReadXxx）

**UR RT 报文格式**（端口 30003，公开文档，参考 UR `rtde_interface` 与 `ur_rtde` 开源库的字段编号约定）：
- 固定 packet，主流 e 系列 1044 字节，UR3 非 e 系列 812 字节
- Packet 是 little-endian double 数组，**字段编号从 0 起，每字段 8 字节**
- 字段编号 → 字节偏移换算：`byteOffset = fieldIndex * 8`
- 关键字段（field index，非字节偏移）：
  - `field[252]` 实际关节角（6×double，连续 6 个字段 252-257）
  - `field[264]` 实际笛卡尔 TCP（6×double，264-269）
  - `field[288]` 关节电流（288-293）
  - `field[300]` 笛卡尔力矩（300-305）
  - `field[312]` 关节温度（312-317）
  - `field[235]` 浮点寄存器区起始
  - `field[79]` 安全模式
  - `field[18]` 时间戳

> **实现注意**：本仓库 `src/Nexus.Robot.Ur/` 目前**没有任何 RT 字段常量定义**（已核对 `UrClient.cs`/`UrModel.cs`/`UrConnectionPool.cs`/`UrVirtualServer.cs`）。RT 解析是全新代码，实现时需新建 `UrRtState.cs` 定义字段偏移常量，并在代码注释中标注每个字段的文档来源。

**改动**：

1. **`Connect()` 增加 RT 端口连接**：
```csharp
_rtClient = new TcpClient();
_rtClient.Connect(IpAddress, RealTimePort);
// RT 端口是单向流（机器人持续推数据，不回应请求）
```

2. **新增 packet 大小探测**：首读尝试 1044，失败回退 812（覆盖主流 e 系列）。

3. **新增 `ReadRtState()`**：从 RT 流读取完整 packet，解析成结构化 `UrRtState`。

4. **修复 ReadFloatRegister 等**：基于 `ReadRtState` 实现真实读取。

5. **新增语义读取方法**：
   - `ReadJointPositions()` — RT[252]
   - `ReadTcpPosition()` — RT[264]
   - `ReadJointTemperatures()` — RT[312]
   - `ReadDigitalInputs()` — RT[73]

6. **修复 `PollMonitors`**：订阅依赖的 ReadXxx 修复后自动恢复。

**风险点**：RT 流是持续的，读时可能读到半截 packet。需循环读到完整 packet 或按消息边界对齐。

### 3.3 Efort 写侧闭环

**问题**：11 个 `Write` 重载全部 `Failed("EFORT 机器人不支持写入操作")`，纯只读。读侧已非常完整（解析 788 字节数据包）。

**协议基础**：EFORT 使用 KEBA 固定帧协议。读侧已实现 788 字节固定帧解析（`ReadRobotData` 解析报文头/心跳/状态/DO×32/DI×32/IO/工程名/程序名/错误文本/7 轴角度/笛卡尔/速度等 20+ 字段）。

**写帧的"对称推导"含义**：
- 读帧结构（已实现）：`[报文头][心跳][状态字节×8][模式][速度][DO×32位][DI×32位][整数IO×32][工程名][程序名][错误文本][7轴角度][笛卡尔6维]...`
- 写帧对称推导：假设写帧与读帧**同构**（相同偏移布局），写命令通过构造相同大小的帧、在对应字段位写入新值后整体发送
- **关键不确定点**：KEBA 实际写帧的报文头命令码、是否需要先发"写请求"再发"写数据"两步握手——这些没有真机文档确认
- **缓解**：VirtualServer 测试验证帧格式对称性（客户端构造的写帧能被 server 正确解析回字段值）；代码注释明确标注"未经真机验证，帧结构基于读帧对称假设"

**改动**：

1. **新增 `BuildWriteCommand`**：构造写帧（基于读帧对称假设）。**强制要求**：代码注释明确标注未经真机验证，并说明假设依据。

2. **实现 11 个 Write 的真实逻辑**：
```csharp
public override OperateResult Write(string address, short value) {
    if (address.StartsWith("DO.")) return WriteDigitalOutput(int.Parse(address.Substring(3)) - 1, value != 0);
    if (address == "CMD") return WriteCommandWord(value);
    return WriteRegisterRaw(address, value);
}
// 其余 Write 重载按类型分发
```

3. **实现 `IRobotControlDevice`**：
```csharp
public OperateResult WriteDigitalOutput(int index, bool value) { /* 构造帧，在 DO 偏移写位 */ }
public OperateResult StartProgram()  => WriteCommandWord(EfortCommands.Start);
public OperateResult StopProgram()   => WriteCommandWord(EfortCommands.Stop);
public OperateResult ResetError()    => WriteCommandWord(EfortCommands.Reset);
public OperateResult SetSpeedRatio(double percent) { /* 写速度寄存器 */ }
```

**EfortCommands 常量**：Start/Stop/Reset/Error 等命令字值，按 KEBA 常用映射定义（注释标注未经真机验证）。

### 3.4 OpenProtocol — Job 管理 + 订阅 + 清理

**问题**：
- `ReadInt32` 等被错误映射到 `GetTighteningResult` 且返回固定 0（伪实现）
- 缺 Job 管理（拧紧工具最核心能力）
- 缺拧紧结果订阅

**改动**：

1. **移除伪 `IReadWriteDevice` 实现**：
   - `ReadInt32/ReadBool/ReadString` 等改为返回明确的失败信息
   - 消息："OpenProtocol 不是寄存器协议，请使用具体 MID 方法（GetTighteningResult/SelectJob 等）"
   - **破坏向后兼容**（从"返回0"变"返回失败"），但原行为本身就是错的

2. **新增 Job 管理**（MID 0035/0036/0038/0045）：
```csharp
public OperateResult SelectJob(int jobId)      // MID 0038
public OperateResult StartJob()                 // MID 0035
public OperateResult AbortJob()                 // MID 0036
public OperateResult UnlockTool()               // MID 0045
```

3. **新增拧紧结果轮询订阅**：
   - OpenProtocol 真订阅是"服务端主动推送 MID0061"，与现有 `ISubscribeDevice`（轮询）不同
   - 本轮先做**轮询版**（定时 `GetTighteningResult` 检测新结果，比对时间戳/ID 变化）
   - 真 push 订阅下轮（需改 `TcpDeviceBase` 支持异步接收）

### 3.5 Estun / Yamaha 接口适配

**Estun**：已有 `RobotStart/RobotStop/RobotResetError/SetGlobalSpeed`，只需：
- `: IRobotControlDevice`
- 方法重命名/适配接口签名
- 补 `WriteDigitalOutput`（基于现有 Modbus TCP 通道）

**Yamaha**：补 `WriteDO`（`@DO(1)=1` 命令格式），其余 `IRobotControlDevice` 方法基于现有 `Run/Stop/Reset` 适配。

---

## 4. 测试策略

### 4.1 现有 VirtualServer 资产

| 协议 | VirtualServer | 状态 |
|---|---|---|
| Robot.UR | `UrVirtualServer.cs` (165行) | 需扩展（加 RT 流模拟）|
| Robot.Efort | `EfortVirtualServer.cs` (275行) | 需扩展（加写响应）|
| GeSrtp | `GeSrtpVirtualServer.cs` | 现有可用 |
| Xinje | `XinjeVirtualServer.cs` | 现有可用 |
| Robot.Yamaha | `YamahaRcxVirtualServer.cs` | 现有可用 |
| OpenProtocol | **无** | 需新增（轻量）|
| Robot.Estun | **无** | 接口契约测试即可（基于现有 Modbus）|

### 4.2 测试清单

#### A. Bug 回归（~12 个）
- GeSrtp: `Incr_PreservesPrefix_For_I/Q/M/T/AI/AQ`（6 个 Theory）
- GeSrtp: `MemTypeToPrefix_Maps_All_Known_Types`
- GeSrtp: `WriteBools_Uses_Correct_Prefix_For_High_MemTypes`（VirtualServer 集成）
- Xinje: `ReadPlcModel_Parses_Model_String_From_Offset_2`（VirtualServer）
- Xinje: `ReadPlcModel_Handles_Modbus_Exception_FC`

#### B. UR RT 读侧（~10 个）
- `Connect_Connects_Rt_Port`
- `ReadRtState_Parses_1044_Byte_Packet`
- `ReadJointPositions_Returns_6_Doubles`
- `ReadTcpPosition_Returns_Xyzabc`
- `ReadFloatRegister_Returns_Correct_Value`（修复回归）
- `ReadRtState_Handles_Partial_Packet_Reassembly`
- `PollMonitors_Raises_OnDataChanged_After_Rt_Fix`（订阅修复回归）

#### C. Efort 写侧（~6 个）
- `WriteDigitalOutput_Builds_Correct_Frame`
- `StartProgram_Writes_Command_Word`
- `WriteDO_Integration_With_VirtualServer`
- `IRobotControlDevice_All_Methods_Succeed`

#### D. OpenProtocol（~6 个）
- `SelectJob_Sends_MID0038`
- `StartJob_Receives_ACK`
- `AbortJob_Sends_MID0036`
- `ReadInt32_Returns_Clear_NotSupported_After_Cleanup`
- `SubscribeTightening_Polls_MID0060`

#### E. Estun/Yamaha 适配（~4 个）
- `Estun_Implements_IRobotControlDevice`
- `Yamaha_WriteDO_Sends_Correct_Command`

**合计**：~38 个新测试。从 3886 → ~3924。

### 4.3 测试原则

- **零硬件依赖**：所有新增功能必须有离线测试路径（VirtualServer 或 byte[] 报文解析）
- **Bug 修复必须有回归测试**：每个修的 Bug 都新增测试覆盖
- **VirtualServer 优先于 mock**：与本仓库现有模式一致

---

## 5. 设计原则（硬规矩）

1. **零硬件依赖**：禁止"只有真机才能验证"的代码。
2. **Legal 合规**：只参考协议消息流（MID 文档/KEBA 帧格式/UR RT 报文格式），不复制 HSL 代码。
3. **不破坏现有契约**：`IReadWriteDevice` 契约不变。新增能力通过新方法或新接口（`IRobotControlDevice`）暴露，不修改既有方法签名。
   - **例外**：OpenProtocol 的伪 `ReadInt32` 等移除属于"修复错误"，可接受破坏。
4. **netstandard2.0 限制**：遵守 AGENTS.md 约束（无 Span、无 MemoryExtensions 等）。
5. **未验证代码明确标注**：Efort 写帧等未经真机验证的逻辑，注释中明确标注。

---

## 6. 验收标准

1. `dotnet test Nexus.slnx` 全绿（0 失败），测试数从 3886 → ~3924。
2. `dotnet build Nexus.slnx -c Release` 零警告（保持当前 0 警告）。
3. 6 个目标协议的 VirtualServer 集成测试在 localhost 跑通。
4. `IRobotControlDevice` 有 3 个实现者（Efort/Estun/Yamaha）。
5. 无新增 `NotImplementedException`（保持 Phase 0 成果）。
6. Bug 修复点各有回归测试覆盖。

---

## 7. 不做的事（YAGNI 边界）

- ❌ 运动控制（MoveJ/MoveL 下发）——风险高（下发错误命令可能撞机），下轮
- ❌ OpenProtocol 真 push 订阅——需改 `TcpDeviceBase` 支持异步接收，下轮
- ❌ Beckhoff Handle 缓存 / ADS Notification——独立工作面，下轮
- ❌ Estun 坐标读取——EstunData 结构改动大，下轮
- ❌ ABB/Fanuc/Kuka/Yaskawa/Staubli 新功能——本轮只处理 C 级和 Bug
- ❌ UR 控制侧（IRobotControlDevice）——UR 控制走 URScript，语义差异大，下轮

---

## 8. 风险与缓解

| 风险 | 缓解 |
|---|---|
| UR RT packet 大小版本差异（1044 vs 812）| 首读探测，失败回退 |
| UR RT 半截 packet 重组 | 循环读到完整 packet 或按边界对齐 |
| Efort 写帧未真机验证 | 注释明确标注，VirtualServer 验证帧格式对称性 |
| OpenProtocol 移除伪 ReadInt32 破坏兼容 | 原行为本身就是错的，返回明确失败比静默返回 0 好 |
| KEBA 命令字值不准 | 按公开 KEBA 文档常用值定义，注释标注来源 |

---

## 9. 估算

- **代码改动**：约 +2500 行（实现）+ ~600 行（测试）
- **测试新增**：~38 个
- **涉及文件**：11 个 src 文件 + 7 个 test 文件
- **建议执行顺序**：Bug 修复（快、安全）→ 接口新增 → 读侧闭环 → 写侧闭环 → OpenProtocol
