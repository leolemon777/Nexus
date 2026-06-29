# Nexus 新协议使用指南

> 本文档介绍 Nexus 2.0 新增的 9 个工业协议库的使用方法。

---

## 目录

1. [Schneider Modicon (施耐德)](#1-schneider-modicon)
2. [Inovance H5U (汇川)](#2-inovance-h5u)
3. [Mitsubishi FX5U (三菱)](#3-mitsubishi-fx5u)
4. [Siemens S7 Plus (西门子)](#4-siemens-s7-plus)
5. [Omron NX/NJ (欧姆龙)](#5-omron-nxnj)
6. [EtherNet/IP (罗克韦尔/AB)](#6-ethernetip)
7. [CC-Link IE (三菱)](#7-cc-link-ie)
8. [BACnet MS/TP (楼宇自动化)](#8-bacnet-mstp)
9. [HART (过程仪表)](#9-hart)

---

## 通用模式

所有协议客户端都遵循统一的 `IReadWriteDevice` 接口：

```csharp
// 1. 创建客户端
var client = new XxxClient("192.168.1.100");

// 2. 连接
var connectResult = client.Connect();
if (!connectResult.IsSuccess)
{
    Console.WriteLine($"连接失败: {connectResult.Message}");
    return;
}

// 3. 读写数据
var readResult = client.ReadInt16("D100");
if (readResult.IsSuccess)
    Console.WriteLine($"D100 = {readResult.Content}");

var writeResult = client.Write("D100", (short)12345);
if (!writeResult.IsSuccess)
    Console.WriteLine($"写入失败: {writeResult.Message}");

// 4. 断开连接
client.Disconnect();
```

**异步用法**（推荐用于 WPF / ASP.NET）：

```csharp
var client = new XxxClient("192.168.1.100");
await client.ConnectAsync();

var result = await client.ReadInt16Async("D100");
if (result.IsSuccess)
    Console.WriteLine($"D100 = {result.Content}");

await client.WriteAsync("D100", (short)12345);
```

**批量读写**（实现 `IBatchReadWrite` 的协议）：

```csharp
// 批量读取
var batchResult = client.BatchRead(new[] { "D100", "D101", "D102" });
if (batchResult.IsSuccess)
    foreach (var kv in batchResult.Content)
        Console.WriteLine($"{kv.Key} = {kv.Value}");

// 批量写入
client.BatchWrite(new Dictionary<string, object>
{
    ["D100"] = (short)100,
    ["D101"] = (short)200,
    ["D102"] = (float)3.14f
});
```

---

## 1. Schneider Modicon

> 施耐德 Unity Pro / Modicon M580/M340 系列 PLC，基于 Modbus TCP 协议。

### 安装

```xml
<ProjectReference Include="..\Nexus.Schneider.Modicon\Nexus.Schneider.Modicon.csproj" />
```

### 创建客户端

```csharp
using Nexus.Schneider.Modicon;

// 默认端口 502
var client = new SchneiderModiconClient("192.168.1.100", port: 502, station: 1);
client.Connect();
```

### 地址格式

| 地址 | 说明 | 示例 |
|------|------|------|
| `%MW` | 保持寄存器（字） | `%MW100` |
| `%MB` | 保持寄存器（字节） | `%MB100` |
| `%MD` | 保持寄存器（双字） | `%MD100` |
| `%MX` | 线圈（位） | `%MX100.5` |
| `%IW` | 输入寄存器（字） | `%IW0` |
| `%IX` | 离散输入（位） | `%IX0.3` |
| `%QW` | 输出寄存器（字） | `%QW0` |
| `%QX` | 输出线圈（位） | `%QX0.1` |
| `%NW` | 网络寄存器（字） | `%NW100` |

### 示例

```csharp
// 读取保持寄存器
var temp = client.ReadInt16("%MW100");
var pressure = client.ReadFloat("%MD200");

// 读取位
var bit = client.ReadBool("%MX100.5");

// 写入
client.Write("%MW100", (short)2500);
client.Write("%MD200", (float)3.14f);
client.Write("%MX100.5", true);

// 读取模块诊断
var diag = client.ReadModuleDiagnostics(0, 10);
```

### 诊断

```csharp
// 读取模块诊断信息
var result = client.ReadModuleDiagnostics(startRegister: 0, count: 10);
if (result.IsSuccess)
    Console.WriteLine($"诊断数据: {BitConverter.ToString(result.Content)}");
```

---

## 2. Inovance H5U

> 汇川 H5U 系列 PLC，基于 Modbus TCP 协议，支持数据订阅。

### 安装

```xml
<ProjectReference Include="..\Nexus.Inovance.H5u\Nexus.Inovance.H5u.csproj" />
```

### 创建客户端

```csharp
using Nexus.Inovance.H5u;

var client = new InovanceH5uClient("192.168.1.100", port: 502, station: 1);
client.Connect();
```

### 地址格式

| 地址 | 说明 | 示例 |
|------|------|------|
| `D` | 数据寄存器 | `D100`, `D0` |
| `M` | 位寄存器 | `M100`, `M0` |
| `X` | 输入 | `X0`, `X10` |
| `Y` | 输出 | `Y0`, `Y10` |
| `T` | 定时器当前值 | `T0`, `T10` |
| `C` | 计数器当前值 | `C0`, `C10` |
| `S` | 步进继电器 | `S0`, `S10` |

### 示例

```csharp
// 读写数据寄存器
var value = client.ReadInt16("D100");
client.Write("D100", (short)1234);

// 读写浮点数
var temp = client.ReadFloat("D200");
client.Write("D200", (float)36.5f);

// 读写位
var m0 = client.ReadBool("M0");
client.Write("M0", true);

// 读写定时器
var t0 = client.ReadInt16("T0");
```

### 数据订阅

```csharp
// 订阅数据变化
client.Subscribe("D100", intervalMs: 500, dataType: "Int16");
client.Subscribe("D200", intervalMs: 1000, dataType: "Float");

// 监听变化事件
client.OnDataChanged += (sender, e) =>
{
    Console.WriteLine($"[{e.Timestamp:HH:mm:ss}] {e.Address}: {e.OldValue} → {e.NewValue}");
};

// 启动订阅
client.StartSubscriptions(globalIntervalMs: 200);

// 停止订阅
client.StopSubscriptions();
```

---

## 3. Mitsubishi FX5U

> 三菱 FX5U 系列 PLC，基于 MC 协议（二进制格式），默认端口 4999。

### 安装

```xml
<ProjectReference Include="..\Nexus.Mitsubishi.Fx5u\Nexus.Mitsubishi.Fx5u.csproj" />
```

### 创建客户端

```csharp
using Nexus.Mitsubishi.Fx5u;

var client = new Fx5uClient("192.168.1.100", port: 4999);
client.Connect();
```

### 地址格式

| 地址 | 说明 | 示例 |
|------|------|------|
| `D` | 数据寄存器 | `D100`, `D0` |
| `M` | 辅助继电器 | `M100`, `M0` |
| `X` | 输入继电器 | `X0`, `X10` |
| `Y` | 输出继电器 | `Y0`, `Y10` |
| `T` | 定时器 | `T0`, `T10` |
| `C` | 计数器 | `C0`, `C10` |
| `R` | 文件寄存器 | `R0`, `R100` |
| `SM` | 特殊辅助继电器 | `SM100` |
| `SD` | 特殊数据寄存器 | `SD100` |
| `W` | 链接寄存器 | `W0` |

### 示例

```csharp
// 读写数据寄存器
var d100 = client.ReadInt16("D100");
client.Write("D100", (short)500);

// 读写浮点数
var d200 = client.ReadFloat("D200");
client.Write("D200", (float)3.14f);

// 读写位
var m0 = client.ReadBool("M0");
client.Write("M0", true);

// 读写 32 位整数
var d300 = client.ReadInt32("D300");
client.Write("D300", 100000);

// 读写 64 位浮点数
var d400 = client.ReadDouble("D400");
client.Write("D400", 3.14159265358979);
```

### 数据订阅

```csharp
client.Subscribe("D100", intervalMs: 1000, dataType: "Int16");
client.OnDataChanged += (s, e) => Console.WriteLine($"{e.Address}: {e.OldValue} → {e.NewValue}");
client.StartSubscriptions();
```

---

## 4. Siemens S7 Plus

> 西门子 S7-1500 系列 PLC（TIA Portal），扩展 S7 协议，支持更大 PDU。

### 安装

```xml
<ProjectReference Include="..\Nexus.Siemens.S7Plus\Nexus.Siemens.S7Plus.csproj" />
```

### 创建客户端

```csharp
using Nexus.Siemens.S7Plus;

var client = new S7PlusClient("192.168.1.100", port: 102);
client.Connect(); // 自动执行 COTP + S7 握手
```

### 地址格式

| 地址 | 说明 | 示例 |
|------|------|------|
| `DB` | 数据块 | `DB1.DBX0.0`, `DB1.DBW0`, `DB1.DBD0` |
| `I` | 输入 | `I0.0`, `I0` |
| `Q` | 输出 | `Q0.0`, `Q0` |
| `M` | 标志位 | `M0.0`, `M0` |
| `T` | 定时器 | `T0` |
| `C` | 计数器 | `C0` |
| `V` | 变量 | `V0.0` |

### 示例

```csharp
// 读写 DB 块
var dbValue = client.ReadInt16("DB1.DBW0");
client.Write("DB1.DBW0", (short)100);

// 读写浮点数
var dbFloat = client.ReadFloat("DB1.DBD10");
client.Write("DB1.DBD10", (float)3.14f);

// 读写位
var dbBit = client.ReadBool("DB1.DBX0.0");
client.Write("DB1.DBX0.0", true);

// 读写 I/Q/M
var i0 = client.ReadByte("I0");
var q0 = client.ReadByte("Q0");
var m0 = client.ReadBool("M0.0");
client.Write("M0.0", true);

// 读写字符串
var str = client.ReadString("DB1.DBW20", 20);
client.Write("DB1.DBW20", "Hello");
```

---

## 5. Omron NX/NJ

> 欧姆龙 NX/NJ 系列控制器，基于 FINS 协议，支持扩展内存区域。

### 安装

```xml
<ProjectReference Include="..\Nexus.Omron.NxNj\Nexus.Omron.NxNj.csproj" />
```

### 创建客户端

```csharp
using Nexus.Omron.NxNj;

var client = new OmronNxNjClient("192.168.1.100", port: 9600);
client.Connect(); // 自动执行 FINS 握手
```

### 地址格式

| 地址 | 说明 | 示例 |
|------|------|------|
| `D` | 数据存储器 | `D100`, `D0` |
| `W` | 工作区域 | `W100`, `W0` |
| `H` | 保持区域 | `H100`, `H0` |
| `CIO` | CIO 区域 | `CIO100`, `CIO0` |
| `A` | 辅助区域 | `A100`, `A0` |
| `E` | 扩展存储器 | `E0.100`（bank 0, offset 100） |
| `I` | 索引寄存器 | `I0`, `I1` |

### 示例

```csharp
// 读写数据存储器
var d100 = client.ReadInt16("D100");
client.Write("D100", (short)1234);

// 读写浮点数
var d200 = client.ReadFloat("D200");
client.Write("D200", (float)3.14f);

// 读写位
var w0 = client.ReadBool("W0.5");
client.Write("W0.5", true);

// 读写扩展存储器
var e0 = client.ReadInt16("E0.100");
client.Write("E0.100", (short)500);

// 读写 CIO
var cio100 = client.ReadInt16("CIO100");

// 读写保持区域
var h0 = client.ReadInt16("H0");
client.Write("H0", (short)999);
```

### 数据订阅

```csharp
client.Subscribe("D100", intervalMs: 1000, dataType: "Int16");
client.OnDataChanged += (s, e) => Console.WriteLine($"{e.Address}: {e.OldValue} → {e.NewValue}");
client.StartSubscriptions();
```

---

## 6. EtherNet/IP

> 罗克韦尔 (Allen-Bradley) / 欧姆龙 / 施耐德等支持 EtherNet/IP 的设备，基于 CIP 显式消息。

### 安装

```xml
<ProjectReference Include="..\Nexus.EtherNetIp\Nexus.EtherNetIp.csproj" />
```

### 创建客户端

```csharp
using Nexus.EtherNetIp;

var client = new EtherNetIpClient("192.168.1.100", port: 44818);
client.Connect(); // 自动注册 CIP 会话
```

### 地址格式

| 地址 | 说明 | 示例 |
|------|------|------|
| 标签名 | 直接使用标签名 | `MyTag` |
| 数组元素 | 标签名[索引] | `MyArray[0]` |
| 结构体成员 | 标签名.成员 | `MyUDT.Member` |

### 示例

```csharp
// 读写标量标签
var temp = client.ReadInt16("Temperature");
client.Write("Temperature", (short)2500);

var pressure = client.ReadFloat("Pressure");
client.Write("Pressure", (float)3.14f);

var running = client.ReadBool("MotorRunning");
client.Write("MotorRunning", true);

// 读写数组元素
var val = client.ReadInt16("DataArray[0]");
client.Write("DataArray[0]", (short)100);

// 读写字符串
var name = client.ReadString("DeviceName", 20);
client.Write("DeviceName", "PLC1");

// 读写 32 位整数
var count = client.ReadInt32("Counter");
client.Write("Counter", 1000000);
```

---

## 7. CC-Link IE

> 三菱 CC-Link IE 控制器网络，基于 MC 协议（二进制格式）。

### 安装

```xml
<ProjectReference Include="..\Nexus.CcLinkIe\Nexus.CcLinkIe.csproj" />
```

### 创建客户端

```csharp
using Nexus.CcLinkIe;

var client = new CcLinkIeClient("192.168.1.100", port: 4999);
client.Connect();
```

### 地址格式

| 地址 | 说明 | 示例 |
|------|------|------|
| `R` | 链接继电器 | `R0`, `R100` |
| `WR` | 链接寄存器 | `WR0`, `WR100` |
| `LR` | 链接寄存器（保持） | `LR0`, `LR100` |
| `SW` | 特殊链接寄存器 | `SW0` |
| `SB` | 特殊链接继电器 | `SB0` |
| `DX` | 输入 | `DX0` |
| `DY` | 输出 | `DY0` |
| `W` | 内部寄存器 | `W0`, `W100` |
| `B` | 内部继电器 | `B0`, `B100` |
| `D` | 数据寄存器 | `D0`, `D100` |

### 示例

```csharp
// 读写数据寄存器
var d100 = client.ReadInt16("D100");
client.Write("D100", (short)1234);

// 读写链接寄存器
var wr0 = client.ReadInt16("WR0");
client.Write("WR0", (short)500);

// 读写位
var r0 = client.ReadBool("R0.5");
client.Write("R0.5", true);

// 读写浮点数
var d200 = client.ReadFloat("D200");
client.Write("D200", (float)3.14f);

// 读写内部继电器
var b0 = client.ReadBool("B0");
client.Write("B0", true);
```

---

## 8. BACnet MS/TP

> 楼宇自动化 BACnet MS/TP 协议，通过 RS-485 串口通信。

### 安装

```xml
<ProjectReference Include="..\Nexus.Bacnet.Mstp\Nexus.Bacnet.Mstp.csproj" />
```

### 创建客户端

```csharp
using Nexus.Bacnet.Mstp;
using System.IO.Ports;

// 创建串口
var serialPort = new SystemSerialPort("COM1")
{
    BaudRate = 9600,
    DataBits = 8,
    Parity = Parity.None,
    StopBits = StopBits.One
};

var client = new BacnetMstpClient(serialPort, sourceAddress: 0);
client.Connect();
```

### 地址格式

```
network:device.objectType:instance.property
```

| 字段 | 说明 | 示例 |
|------|------|------|
| network | 网络号 | `1` |
| device | 设备 ID | `1001` |
| objectType | 对象类型 | `0`=Analog Input, `1`=Analog Output, `2`=Analog Value, `3`=Binary Input, `4`=Binary Output, `5`=Binary Value, `13`=Multi-State Input, `14`=Multi-State Output |
| instance | 实例号 | `0` |
| property | 属性 ID | `85`=Present Value |

### 示例

```csharp
// 读取模拟输入的当前值
var ai = client.ReadFloat("1:1001.0:0.85");

// 读取模拟输出
var ao = client.ReadFloat("1:1001.1:0.85");

// 写入模拟值
client.Write("1:1001.2:0.85", (float)25.5f);

// 读取二进制输入
var bi = client.ReadBool("1:1001.3:0.85");

// 写入二进制输出
client.Write("1:1001.4:0.85", true);

// 读取多状态值
var ms = client.ReadInt16("1:1001.13:0.85");
```

---

## 9. HART

> 过程控制仪表 HART 协议，通过串口通信，支持短地址和长地址。

### 安装

```xml
<ProjectReference Include="..\Nexus.Hart\Nexus.Hart.csproj" />
```

### 创建客户端

```csharp
using Nexus.Hart;

var serialPort = new SystemSerialPort("COM1")
{
    BaudRate = 1200,
    DataBits = 8,
    Parity = Parity.Odd,
    StopBits = StopBits.One
};

var client = new HartClient(serialPort);
client.Connect();
```

### 地址格式

| 格式 | 说明 | 示例 |
|------|------|------|
| 短地址 | 0-15 | `0`, `1`, `15` |
| 长地址 | 38位设备ID（十六进制） | `0x1234567890ABCDEF` |

### 示例

```csharp
// 使用短地址读取
var pv = client.ReadFloat("0");        // 主设备 PV
var sv = client.ReadFloat("1");        // 第二变量

// 使用长地址读取
var value = client.ReadFloat("0x1234567890");

// HART 命令说明:
// Cmd0: 读取设备唯一 ID
// Cmd1: 读取过程变量 (PV)
// Cmd2: 读取电流值
// Cmd3: 读取 PV 和电流
// Cmd6: 写入轮询地址

// 读取设备信息
var deviceId = client.ReadBytes("0", 20);

// 写入轮询地址（将设备短地址设为 5）
client.Write("0", (short)5);
```

---

## 高级用法

### 连接池

对于高并发场景，使用 `ConnectionPool` 复用连接：

```csharp
using Nexus;

// 创建连接池
var pool = new ConnectionPool<ModbusTcpClient>(
    factory: () =>
    {
        var c = new ModbusTcpClient("192.168.1.100");
        c.SetPersistentConnection();
        return c;
    },
    maxPoolSize: 10);

// 使用连接池
var client = pool.Acquire("192.168.1.100:502:1");
try
{
    var result = client.ReadInt16("40001");
}
finally
{
    pool.Release("192.168.1.100:502:1", client);
}
```

### 日志记录

```csharp
using Nexus;

var client = new SchneiderModiconClient("192.168.1.100");
client.SetLogger(new ConsoleLogger());

// 或使用自定义日志
client.SetLogger(new DelegateLogger((level, msg) =>
{
    Console.WriteLine($"[{level}] {msg}");
}));

// 监听报文事件
client.OnMessageSent += (s, hex) => Console.WriteLine($"TX: {hex}");
client.OnMessageReceived += (s, hex) => Console.WriteLine($"RX: {hex}");
client.OnError += (s, msg) => Console.WriteLine($"ERR: {msg}");
```

### 地址上下文参数

所有协议支持运行时地址参数覆盖：

```csharp
// 指定站号（适用于 Modbus 变体）
var result = client.ReadInt16("s=2;D100");  // 站号 2

// 指定字节序
var result = client.ReadInt16("bo=le;D100");  // 小端序

// 支持的字节序: be(大端), le(小端), badc(中大端), cdab(中小端)
```

### 自动重连

```csharp
var client = new SchneiderModiconClient("192.168.1.100");
client.SetPersistentConnection();
client.AutoReconnect = true;
client.ReconnectInterval = 3000;       // 重连间隔 3 秒
client.MaxReconnectAttempts = 10;      // 最多重试 10 次

// 监听重连事件
client.OnReconnecting += attempt => Console.WriteLine($"正在重连 (第 {attempt} 次)...");
client.OnReconnected += () => Console.WriteLine("重连成功");
client.OnReconnectFailed += reason => Console.WriteLine($"重连失败: {reason}");
```

### 心跳保活

```csharp
var client = new SchneiderModiconClient("192.168.1.100");
client.SetPersistentConnection();
client.HeartbeatEnabled = true;
client.HeartbeatInterval = 30000;  // 每 30 秒发一次心跳
client.MaxHeartbeatFailures = 3;   // 连续 3 次失败则标记断开
```

---

## 协议对比表

| 协议 | 传输层 | 默认端口 | 批量读写 | 数据订阅 | 地址前缀 |
|------|--------|---------|---------|---------|---------|
| Schneider Modicon | TCP | 502 | ✅ | ❌ | `%` |
| Inovance H5U | TCP | 502 | ✅ | ✅ | `D/M/X/Y/T/C/S` |
| Mitsubishi FX5U | TCP | 4999 | ✅ | ✅ | `D/M/X/Y/T/C/R/SM/SD/W` |
| Siemens S7 Plus | TCP | 102 | ✅ | ❌ | `DB/I/Q/M/T/C/V` |
| Omron NX/NJ | TCP | 9600 | ✅ | ✅ | `D/W/H/CIO/A/E/I` |
| EtherNet/IP | TCP | 44818 | ✅ | ❌ | 标签名 |
| CC-Link IE | TCP | 4999 | ✅ | ❌ | `R/WR/LR/SW/SB/DX/DY/W/B/D` |
| BACnet MS/TP | Serial | - | ✅ | ❌ | `network:device.ot:inst.prop` |
| HART | Serial | - | ✅ | ❌ | `0-15` 或 `0x...` |

---

## 常见问题

### Q: 如何选择使用哪个协议？

- **施耐德 PLC** → `SchneiderModiconClient`
- **汇川 H5U** → `InovanceH5uClient`
- **三菱 FX5U** → `Fx5uClient`（FX3U 用 `FxSerialClient`）
- **西门子 S7-1500** → `S7PlusClient`（S7-1200/300 用 `SiemensS7Client`）
- **欧姆龙 NX/NJ** → `OmronNxNjClient`（旧款用 `FinsTcpClient`）
- **AB/罗克韦尔** → `EtherNetIpClient` 或 `AllenBradleyCipClient`
- **三菱 CC-Link IE** → `CcLinkIeClient`
- **楼宇自动化** → `BacnetMstpClient`
- **过程仪表** → `HartClient`

### Q: 连接超时怎么办？

```csharp
// 增加超时时间（默认 5000ms）
var client = new SchneiderModiconClient("192.168.1.100", timeout: 10000);
```

### Q: 如何处理 Modbus 异常码？

```csharp
var result = client.ReadInt16("%MW100");
if (!result.IsSuccess)
{
    Console.WriteLine($"错误: {result.Message}");
    Console.WriteLine($"错误码: {result.ErrorCode}");
    // ErrorCode 1=非法功能码, 2=非法地址, 3=非法值, 4=设备故障
}
```

### Q: 如何在 WPF 中使用？

```csharp
// 在 ViewModel 中使用 async/await
private async Task ReadDataAsync()
{
    var result = await _client.ReadInt16Async("%MW100");
    if (result.IsSuccess)
        Temperature = result.Content;
}
```

### Q: 支持哪些数据类型？

所有协议统一支持：`bool`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `string`, `byte[]`
