# NEXUS 超越 HSL 作战计划 v2.0

> **目标**: 2 年内成为 .NET 工控通讯库的事实标准 — 代码质量、协议深度、生态完整性全面超越 HslCommunication。
>
> **当前基线**: 41 协议库 / 1098 测试 / 65 个 NotImplementedException / 7 个 IBatchReadWrite / 0 NuGet 下载
>
> **HSL 基线**: ~100 协议模块 / 300 万 NuGet 下载 / 10 年生产验证 / 数万用户

---

## Phase 0: 止血 — 消灭空壳，交付可用 MVP（2 周）

> **原则**: 用户拿到的每一个协议都必须"打开就能用"，不能有 NotImplementedException。

### 0.1 消灭所有 NotImplementedException（65 处）

| 模块 | 桩数 | 动作 |
|------|------|------|
| Mitsubishi MC3E Ascii | 21 | 实现全部读写（基于 MC3E Binary 的 ASCII 编码转换） |
| Mitsubishi MC3E UDP | 22 | 实现（复用 MC3E Binary 逻辑，改用 UdpDeviceBase 收发） |
| Mitsubishi FX Serial | 12 | 补齐 Bool/UInt16/UInt32/Int64/UInt64/Double 读写 |
| Siemens PPI | 10 | 实现基础 Bool/Int16/Float/Bytes 读写（PPI 协议核心） |

### 0.2 核心协议 IBatchReadWrite 补齐

当前 7 个 → 目标 15 个：

| 新增 | 优先级 |
|------|--------|
| AllenBradley CIP | P0 |
| Omron HostLink | P0 |
| Mitsubishi A1E | P1 |
| Panasonic Mewtocol | P1 |
| Keyence KV | P1 |
| Beckhoff ADS | P1 |
| Fatek FBs | P2 |
| Yaskawa Memobus | P2 |

### 0.3 验证标准

```bash
grep -rn "NotImplementedException" src/Nexus.*/  # 必须为 0（排除基类模板）
dotnet test Nexus.slnx                            # 全部通过
```

**Phase 0 完成标志**: 41 个协议库零 NotImplementedException，全部可基础读写。

---

## Phase 1: 地基 — 让核心坚如磐石（3 周）

### 1.1 连接层健壮性

#### 1.1.1 自动重连守护（`AutoReconnectGuard`）

```
TcpDeviceBase 新增:
  - bool AutoReconnect { get; set; }       // 默认 false
  - int ReconnectInterval { get; set; }    // 默认 5000ms
  - 内部 Timer 定期检测 IsConnected，断开则自动重连
  - 重连时触发 OnReconnecting / OnReconnected 事件
  - 可配置最大重连次数、指数退避
```

#### 1.1.2 心跳保活（`KeepAlive`）

```
TcpDeviceBase 新增虚方法:
  - virtual byte[] BuildHeartbeat() => null    // 子类可覆盖
  - 内部 Timer 每隔 HeartbeatInterval 发送心跳
  - 心跳失败 N 次 → 标记断开 → 触发自动重连
```

Modbus: BuildHeartbeat → 读一个已知寄存器
Siemens: BuildHeartbeat → 空 S7 请求
Omron: BuildHeartbeat → FINS 心跳帧

#### 1.1.3 线程安全升级

当前 `lock` 在高并发下会串行化所有请求。升级：

```
TcpDeviceBase:
  - 保留 _lock 用于连接/断开操作
  - 收发操作改用 SemaphoreSlim（可配置并发度）
  - 或引入请求队列 + 单线程发送模式（类似 HSL 的 Token 管网）
```

#### 1.1.4 连接池整合

`ConnectionPool<T>` 已存在但未使用。需要：

```
- 每个协议客户端增加 static ConnectionPool 共享池
- 支持 key = "ip:port:station" 自动池化
- 在 WPF App 中启用连接池模式
- 添加健康检查：池中连接定期 ping
```

### 1.2 DataConverter 升级

#### 1.2.1 完整字节序支持

```csharp
// 当前 DataConverter 只支持大端序，需要增加:
static class DataConverter
{
    // 新增：指定字节序的转换方法
    static short ToInt16(byte[] data, int offset, Endianness byteOrder);
    static int   ToInt32(byte[] data, int offset, Endianness byteOrder);
    static float ToFloat(byte[] data, int offset, Endianness byteOrder);
    // ... 所有数值类型

    // 新增：值 → 字节数组（指定字节序）
    static byte[] GetBytes(short value, Endianness byteOrder);
    static byte[] GetBytes(float value, Endianness byteOrder);
    // ...

    // 新增：反转字节序工具
    static byte[] SwapWordOrder(byte[] data, int offset, int length);
    static byte[] SwapByteOrder(byte[] data, int offset, int length);
}
```

#### 1.2.2 字符串编码深度

```csharp
// 新增字符串工具
static class StringConverter
{
    // S7 String: [maxLen][actualLen][chars...]
    static string DecodeS7String(byte[] data, int offset);
    static byte[] EncodeS7String(string value, ushort maxLength);

    // S7 WString: [maxLen_word][actualLen_word][chars_utf16...]
    static string DecodeWString(byte[] data, int offset);
    static byte[] EncodeWString(string value, ushort maxLength);

    // Mitsubishi String: [len][chars] 或 null-terminated
    static string DecodeMitsubishiString(byte[] data, int offset, int maxLen, Encoding encoding);

    // Modbus String: 支持多种对齐和字节序
    static string DecodeModbusString(byte[] data, int offset, int length, Endianness byteOrder);

    // 通用: BCD 编码、GB2312、Shift-JIS
    static string DecodeBcdString(byte[] data, int offset, int length);
    static byte[] EncodeBcdString(string value, int targetLength);
}
```

#### 1.2.3 结构体映射

```csharp
// HSL 的杀手级特性 — 直接把 PLC 内存映射到 C# struct
static class StructConverter
{
    T FromBytes<T>(byte[] data, int offset = 0) where T : struct;
    byte[] ToBytes<T>(ref T value) where T : struct;
    T FromBytes<T>(byte[] data, int offset, Endianness byteOrder) where T : struct;
}
```

### 1.3 运行时地址参数覆盖

HSL 支持 `x=3;s=2;D100` 格式。Nexus 需要类似机制：

```csharp
// 新增 AddressContext
public sealed class AddressContext
{
    public string Address { get; }
    public byte? StationOverride { get; }
    public byte? SlotOverride { get; }
    public byte? RackOverride { get; }
    public Endianness? ByteOrderOverride { get; }
    public int? TimeoutOverride { get; }

    // 解析: "x=3;D100" → Address="D100", StationOverride=3
    // 解析: "s=2;e=cdab;D100" → Address="D100", SlotOverride=2, ByteOrderOverride=CDAB
    public static AddressContext Parse(string rawAddress);
}

// 所有 ReadXxx / Write 方法内部: context = AddressContext.Parse(address)
```

### 1.4 ILogger 升级

```csharp
public enum LogLevel { Debug, Info, Warn, Error }

public interface ILogger
{
    void Log(LogLevel level, string message);
    // 保留兼容方法
    void Info(string message) => Log(LogLevel.Info, message);
    void Warn(string message) => Log(LogLevel.Warn, message);
    void Error(string message) => Log(LogLevel.Error, message);
    void Debug(string message) => Log(LogLevel.Debug, message);
}

// 新增实现:
public class DelegateLogger : ILogger { ... }  // Action<LogLevel, string>
public class BufferedLogger : ILogger { ... }   // 环形缓冲，用于 WPF 报文查看器
public class FileLogger : ILogger { ... }       // 滚动日志文件
public class MultiplexLogger : ILogger { ... }  // 组合多个 Logger
```

### 1.5 验证标准

```
- TcpDeviceBase 自动重连 + 心跳可通过模拟断线测试
- DataConverter 支持全部 4 种字节序 + 结构体映射
- AddressContext.Parse 覆盖所有协议前缀语法
- dotnet test 全部通过
```

**Phase 1 完成标志**: 核心基础设施达到生产级，支持 7×24 小时无人值守运行。

---

## Phase 2: 深度 — 把已有协议做到极致（6 周）

> **原则**: 宁可 10 个协议做到极致，也不要 40 个半成品。先做用户量最大的。

### 2.1 Modbus 全系列（已有 4486 行，目标 8000 行）

| 模块 | 当前 | 目标 | 新增 |
|------|------|------|------|
| ModbusTcpClient | 887 行 | 1500 行 | FC23 批量读写增强、字到位转换、线圈批量、异常码诊断 |
| ModbusRtuClient | ~600 行 | 1200 行 | 完整 FC01-06/15/16、RS485 帧同步优化、CRC 校验增强 |
| ModbusAsciiClient | ~500 行 | 1000 行 | 完整功能码、LRC 校验、帧定界符处理 |
| ModbusUdpClient | ~400 行 | 800 行 | 广播设备发现、多响应收集 |
| ModbusRtuOverTcp | ~300 行 | 600 行 | DTU 透传场景优化、断线检测 |

**新增 Modbus 高级特性:**

```
- ModbusTcpServer 增强:
  · 支持自定义回调处理（用户定义寄存器读写逻辑）
  · 支持 Modbus Gateway 模式（转发到其他设备）
  · 性能基准：10,000 requests/sec

- Modbus 诊断工具:
  · 报文解析器（十六进制 → 人类可读）
  · 异常码翻译（中文）
  · 报文录制/回放
```

### 2.2 Siemens 全系列（已有 2896 行，目标 6000 行）

| 模块 | 当前 | 目标 | 新增 |
|------|------|------|------|
| SiemensS7Client | 1277 行 | 3000 行 | 深度批量、结构体映射、时钟读写、DB 扫描、安全模式 |
| SiemensFetchWriteClient | ~300 行 | 800 行 | 完整 FetchWrite 实现 |
| SiemensPpiClient | ~300 行 | 1200 行 | S7-200/200Smart 完整 PPI 协议 |
| SiemensS7VirtualPlc | ~400 行 | 1000 行 | 多 DB 模拟、定时器/计数器、S7String 支持 |

**新增 Siemens 高级特性:**

```
- S7-1200/1500 专有:
  · 优化的块读写（S7 Plus 协议）
  · DB 编号自动发现
  · 数据块下载/上传
  · PLCSIM Advance 对接

- PPI 协议:
  · S7-200 全系列（CPU221/222/224/226）
  · S7-200Smart（额外支持 REST 接口）
  · 自由口模式支持

- 安全功能:
  · PLC 密码保护验证
  · 保护等级读取
  · 访问权限管理
```

### 2.3 Mitsubishi 全系列（已有 3191 行，目标 6000 行）

| 模块 | 当前 | 目标 | 新增 |
|------|------|------|------|
| Mc3EBinaryClient | 1019 行 | 2000 行 | 随机读写、批量优化、标签访问 |
| Mc3EAsciiClient | 99 行 | 800 行 | 完整 ASCII 协议 |
| Mc3EUdpClient | 125 行 | 600 行 | UDP 完整实现 |
| MelsecA1EClient | 502 行 | 1000 行 | 批量读写、字符串编码 |
| FxSerialClient | 171 行 | 1200 行 | FX 全系列（0N/1N/2N/3U/3G）、全部命令 |
| Mc3EVirtuServer | 593 行 | 1000 行 | 多型号模拟、定时器/计数器 |
| MitsubishiFx 新增 | 0 | 1500 行 | **FX5U MC 协议（Q 系列兼容）** |

### 2.4 Omron 全系列（已有 3916 行，目标 6000 行）

| 模块 | 当前 | 目标 | 新增 |
|------|------|------|------|
| FinsTcpClient | 1026 行 | 1800 行 | 批量读写增强、结构体映射 |
| FinsUdpClient | 618 行 | 1200 行 | 设备发现、广播、完整 FINS UDP |
| HostLink | 486 行 | 1000 行 | 完整 HostLink 命令集 |
| HostLinkSerial | 321 行 | 800 行 | 串口完整实现 |
| **FinsSerialClient 新增** | 0 | 1000 行 | FINS 串口协议 |
| **NxClient 新增** | 0 | 1500 行 | **NX/NJ 系列专用协议** |

### 2.5 AllenBradley 全系列（已有 3402 行，目标 5500 行）

| 模块 | 当前 | 目标 | 新增 |
|------|------|------|------|
| CipClient | 1231 行 | 2500 行 | Tag Fragmented 读写、数组读写、UDT 支持 |
| PcccClient | 831 行 | 1500 行 | SLC500/PLC5 完整实现 |
| CipVirtualServer | 766 行 | 1500 行 | 模拟更多数据类型、Tag 数据库 |

**新增:**
```
- EtherNet/IP 显式消息（CIP Generic）
- ControlLogix 标签浏览器（读控制器项目文件）
- CompactLogix 专用优化
- MicroLogix 支持（PCCC 子集）
```

### 2.6 B 级协议深度提升

| 协议 | 当前行数 | 目标 | 关键新增 |
|------|---------|------|---------|
| Beckhoff ADS | 604 | 1800 | ADS 读写、符号表、通知订阅、路由 |
| Panasonic Mewtocol | 592 | 1500 | 批量读写、FP 全系列、字符串 |
| Keyence KV | 523 | 1200 | KV-3000/5000/7000、批量读写 |
| Yaskawa Memobus | 1770 | 2500 | 完整功能码、V 系列专用命令 |
| Yokogawa | 1469 | 2200 | Vnet/IP 深度、多子地址 |
| Inovance | 948 | 1800 | H3U/AM/H5U 全系列 |
| Fatek | 1206 | 1800 | FBs 全系列 |
| LsElectric | 462 | 1200 | XGT 完整协议 |
| GeSrtp | 640 | 1200 | 90-30/70/PACSystems |
| Delta | 428 | 1200 | DVP/AS 完整 |

### 2.7 机器人协议深度

| 模块 | 当前行数 | 目标 | 关键新增 |
|------|---------|------|---------|
| KUKA EKI | 283 | 800 | 重构继承 TcpDeviceBase、变量批量读写、轨迹控制 |
| FANUC FOCAS | 461 | 1500 | FOCAS2 完整 API、坐标读取、程序管理、I/O 控制 |
| ABB RobotWare | 498 | 1200 | RobotWare SDK 深度、EGM 实时通讯 |
| Yaskawa Motoman | 508 | 1000 | MotoPlus、CIOPhone 完整 |
| Estun | 324 | 600 | 运动控制增强 |
| Efort | 338 | 600 | 机器人状态读取增强 |
| Fanuc Robot | 456 | 800 | PCDK 深度 |
| Yamaha | 281 | 600 | RCX 协议增强 |

### 2.8 验证标准

```
- 每个 B 级以上协议至少有 50 个单元测试
- 每个协议有地址解析测试 → 报文构建测试 → 响应解析测试 → 集成测试
- 关键协议有性能基准（ReadInt16 1000 次延迟 < 50ms 在 localhost）
- 新增测试总量: 1098 → 3000+
```

**Phase 2 完成标志**: 前 10 大协议深度对标 HSL，测试覆盖 > 3000。

---

## Phase 3: 广度 — 填补协议空白（8 周）

### 3.1 国内高需求协议（中国工厂必备）

| 新协议 | 目标行数 | 重要性 | 说明 |
|--------|---------|--------|------|
| **Schneider Modicon M580/M340** | 2000 | ⭐⭐⭐⭐⭐ | 施耐德 Unity Pro/Modicon，国内保有量大 |
| **S7 Plus (TIA Portal)** | 1500 | ⭐⭐⭐⭐ | S7-1500 高级协议 |
| **Mitsubishi FX5U MC** | 1500 | ⭐⭐⭐⭐ | FX3U 的替代，已出货百万台 |
| **Omron NX/NJ** | 1500 | ⭐⭐⭐⭐ | 新一代欧姆龙 |
| **Inovance H5U/EasyWeb** | 1000 | ⭐⭐⭐⭐ | 汇川最新型号 |
| **Xinje XC/XG 完整** | 800 | ⭐⭐⭐ | 信捷完整协议 |
| **Delta DVP 完整** | 800 | ⭐⭐⭐ | 台达串口完整 |

### 3.2 工业以太网协议

| 新协议 | 目标行数 | 重要性 | 说明 |
|--------|---------|--------|------|
| **EtherNet/IP (显式消息)** | 2000 | ⭐⭐⭐⭐ | AB/Omron/Schneider 通用 |
| **Profinet IO** | 2000 | ⭐⭐⭐⭐ | 西门子工业以太网 |
| **CC-Link IE** | 1500 | ⭐⭐⭐ | 三菱工业以太网 |
| **POWERLINK** | 1500 | ⭐⭐ | B&R 工业以太网 |
| **EtherCAT** | 2000 | ⭐⭐⭐ | 倍福/Beckhoff，需要实时内核支持 |

> ⚠️ 工业以太网协议（Profinet/EtherCAT）的实时版本需要内核驱动，纯 .NET 无法实现。
> 可以做的是 **非实时版本**（配置/诊断/参数化）。

### 3.3 行业专用协议

| 新协议 | 目标行数 | 重要性 | 说明 |
|--------|---------|--------|------|
| **DNP3** | 1500 | ⭐⭐⭐ | 电力行业标准 |
| **IEC 61850** | 2000 | ⭐⭐⭐ | 智能变电站 |
| **BACnet MSTP** | 1200 | ⭐⭐⭐ | 楼宇自动化串口 |
| **CANopen** | 1500 | ⭐⭐ | 汽车与自动化（需要 CAN 适配器） |
| **LonWorks** | 800 | ⭐⭐ | 楼宇/照明 |
| **HART** | 800 | ⭐⭐ | 过程控制仪表 |
| **Profibus DP** | 1200 | ⭐⭐ | 旧式现场总线 |

### 3.4 现有 IoT 协议增强

| 协议 | 增强方向 |
|------|---------|
| MQTT | MQTT 5.0 完整支持、内置 Broker、遗嘱消息增强、SSL/TLS |
| Redis | 集群模式、Sentinel、Pipeline、Pub-Sub 增强 |
| OPC UA | 安全模式（Sign/Encrypt）、证书管理、历史数据读取、方法调用 |
| IEC 104 | 平衡式传输、总召唤、时钟同步、ASDU 全类型 |

### 3.5 验证标准

```
- 新协议每个至少 30 个单元测试
- 有 VirtualServer 的协议需要集成测试
- 新增协议总量: 41 → 65+
- 新增测试总量: 3000 → 5000+
```

**Phase 3 完成标志**: 协议覆盖全面超越 HSL（特别是中国工控市场所需协议）。

---

## Phase 4: 生态 — 让用户离不开你（4 周）

### 4.1 NuGet 发布

```xml
<!-- 每个协议独立发布 NuGet 包 -->
Nexus.Core                 — 核心抽象
Nexus.Modbus               — Modbus 全系列
Nexus.Siemens              — 西门子全系列
Nexus.Mitsubishi           — 三菱全系列
... 每个 NuGet 包一个协议

<!-- 打包元包 -->
Nexus.All                   — 包含所有协议的元包
Nexus.Modbus.Tcp            — Modbus TCP 精简包
Nexus.Siemens.S7            — S7 精简包
```

发布流程:
```
- GitHub Actions CI/CD
- 自动版本号（基于 git tag）
- 符号包（.snupkg）发布到 nuget.org
- 每个包的 README.md 自动生成
```

### 4.2 API 文档系统

```
工具链: DocFX → 生成静态文档站

文档结构:
├── getting-started/
│   ├── quickstart-modbus.md        — 5 分钟上手
│   ├── quickstart-siemens.md
│   ├── quickstart-mitsubishi.md
│   └── migration-from-hsl.md       — HSL 迁移指南
├── api/
│   └── (自动生成 XML 文档)
├── protocols/
│   ├── modbus/
│   │   ├── address-format.md       — 地址格式详解
│   │   ├── function-codes.md       — 功能码说明
│   │   ├── byte-order.md           — 字节序
│   │   ├── examples.md             — 10+ 个示例
│   │   └── troubleshooting.md      — 常见问题
│   ├── siemens/
│   │   ├── s7-connection.md        — 连接配置
│   │   ├── s7-address.md           — DB/I/Q/M 地址
│   │   ├── s7-string.md            — 字符串编码
│   │   ├── s7-batch.md             — 批量读写
│   │   └── plc-control.md          — PLC 控制
│   └── ... (每个协议一个目录)
├── advanced/
│   ├── connection-pool.md
│   ├── batch-read-write.md
│   ├── struct-mapping.md
│   ├── custom-logger.md
│   ├── thread-safety.md
│   └── performance.md
├── deployment/
│   ├── wpf-integration.md
│   ├── aspnet-integration.md
│   ├── windows-service.md
│   └── linux-iot.md
└── benchmarks/
    ├── modbus-throughput.md
    └── s7-throughput.md
```

### 4.3 示例代码仓库

```
Nexus.Samples/  (独立仓库)
├── samples/
│   ├── modbus-tcp-basics/          — 基础读写
│   ├── modbus-serial-rtu/          — 串口通讯
│   ├── siemens-s7-read-db/         — 读 DB 块
│   ├── siemens-s7-batch/           — 批量读写
│   ├── mitsubishi-mc3e/            — MC 协议
│   ├── omron-fins/                 — FINS 通讯
│   ├── allen-bradley-cip/          — CIP 标签读写
│   ├── data-logger/                — 数据采集到 SQLite
│   ├── web-dashboard/              — ASP.NET Web 监控
│   └── wpf-scada/                  — WPF 上位机
└── README.md
```

### 4.4 WPF 调试前端增强

| 功能 | 说明 |
|------|------|
| **报文录制/回放** | 记录所有 TX/RX 报文，可导出/回放 |
| **报文解析器** | 十六进制 → 人类可读（协议字段高亮） |
| **地址浏览器** | 连接 PLC 后浏览可用地址/DB 块 |
| **批量读写 UI** | 一次配置多个地址，批量执行 |
| **曲线监控** | LiveChart 实时趋势图（已有 MonitorPage，需增强） |
| **报警规则引擎** | 阈值报警 + 变化率报警 + 延时报警 |
| **虚拟 PLC 管理** | 启动/配置/预设数据/场景保存 |
| **连接模板** | 保存连接参数，下次一键连接 |
| **数据导出** | CSV/Excel/JSON 导出 |
| **暗色/亮色主题** | 已有 mono soft，增加 more themes |
| **多语言** | 中文/英文 UI 切换 |
| **窗口布局保存** | 记住用户窗口布局偏好 |

### 4.5 验证标准

```
- NuGet 包成功发布（可通过 dotnet add package Nexus.Modbus 安装）
- 文档站可访问（GitHub Pages）
- 每个协议至少 3 个示例代码
- WPF 调试器可稳定运行 8 小时以上不崩溃
```

**Phase 4 完成标志**: 用户可以在 5 分钟内通过 NuGet + 文档 + 示例跑通第一个 PLC 读写。

---

## Phase 5: 差异化 — HSL 没有的，Nexus 独有（4 周）

### 5.1 虚拟 PLC 生态（HSL 完全没有这个）

这是 Nexus 最大的差异化优势。

```
Nexus.VirtualPlc/
├── Nexus.VirtualPlc.Core/              — 虚拟 PLC 框架
│   ├── IVirtualPlc                     — 虚拟 PLC 接口
│   ├── MemoryModel                     — 内存模型（线圈/寄存器/DB/定时器/计数器）
│   ├── LadderEngine                    — 简易梯形图引擎（执行逻辑运算）
│   └── ScenarioRunner                  — 场景脚本（Python/C# DSL）
├── Nexus.VirtualPlc.S7/                — S7 虚拟 PLC
│   ├── S7VirtualPlc : IVirtualPlc      — 完整 S7 协议栈模拟
│   ├── S7MemoryModel                   — DB/I/Q/M/T/C/Z 完整内存映射
│   ├── S7CommunicationLayer            — TPKT+COTP+S7 三层协议
│   └── S7Scenarios/                    — 预置场景
│       ├── conveyor_belt.json          — 传送带场景
│       ├── motor_control.json          — 电机控制场景
│       └── temperature_pid.json        — 温度 PID 场景
├── Nexus.VirtualPlc.Modbus/            — Modbus 虚拟 PLC（增强现有）
├── Nexus.VirtualPlc.Mitsubishi/        — 三菱虚拟 PLC
└── Nexus.VirtualPlc.Omron/             — 欧姆龙虚拟 PLC
```

**场景脚本格式:**
```json
{
  "name": "温度 PID 控制",
  "plc": "S7-1200",
  "initial_state": { "DB1.DBD0": 25.0, "DB1.DBD4": 0.0 },
  "rules": [
    { "trigger": "DB1.DBX10.0", "action": "set DB1.DBD4 = pid(DB1.DBD0, setpoint=50)" },
    { "trigger": "every 1000ms", "action": "DB1.DBD0 = DB1.DBD0 + random(-0.5, 0.5)" }
  ]
}
```

### 5.2 数据自动采集引擎

```csharp
// HSL 没有内建采集引擎，Nexus 独有
public class DataAcquisitionEngine : IDisposable
{
    // 添加采集点
    void AddPoint(string id, IReadWriteDevice device, string address,
                  string dataType, int intervalMs);

    // 启动/停止
    void Start();
    void Stop();

    // 数据变化事件
    event EventHandler<DataPointChangedEventArgs> OnDataChanged;

    // 数据存储（可插拔）
    IDataSink DataSink { get; set; }
}

// 数据存储后端
public interface IDataSink
{
    Task WriteAsync(IEnumerable<DataPoint> points);
}

// 内建实现:
class SqliteDataSink : IDataSink { ... }     // SQLite 存储
class CsvDataSink : IDataSink { ... }        // CSV 文件滚动
class InfluxDbDataSink : IDataSink { ... }   // InfluxDB 时序数据库
class MemoryDataSink : IDataSink { ... }     // 内存环形缓冲
```

### 5.3 协议网关/转换器

```csharp
// 将 Modbus 数据桥接到 OPC UA / MQTT
public class ProtocolBridge : IDisposable
{
    // Modbus → MQTT
    static ProtocolBridge CreateModbusToMqtt(
        ModbusTcpClient modbus, MqttClient mqtt, string topicPrefix);

    // S7 → OPC UA
    static ProtocolBridge CreateS7ToOpcUa(
        SiemensS7Client s7, OpcUaServer opcua, string nodeIdPrefix);

    // Modbus → Redis
    static ProtocolBridge CreateModbusToRedis(
        ModbusTcpClient modbus, RedisClient redis, string keyPrefix);

    void AddMapping(string sourceAddress, string targetAddress, int intervalMs);
    void Start();
    void Stop();
}
```

### 5.4 报文录制/回放/分析

```csharp
public class PacketRecorder
{
    // 录制
    void Attach(IReadWriteDevice device);
    void StartRecording(string filePath);
    void StopRecording();
    // 录制文件格式: JSON Lines
    // {"ts":"2026-06-07T10:30:00.123","dir":"TX","hex":"00 01 00 00 ...","parsed":{...}}

    // 回放
    void Replay(string filePath, IReadWriteDevice target, ReplayOptions options);

    // 分析
    PacketAnalysis Analyze(string filePath);
}

public class PacketAnalysis
{
    int TotalPackets { get; }
    TimeSpan Duration { get; }
    Dictionary<string, int> FunctionCodeDistribution { get; }
    List<PacketAnomaly> Anomalies { get; }  // 超时、重传、异常响应
    double AverageResponseTime { get; }
}
```

### 5.5 Web API 远程管理

```csharp
// ASP.NET Core 集成包
// Nexus.Web/

public class NexusWebExtensions
{
    // IServiceCollection.AddNexusWeb()
    // 暴露 REST API:
    //   GET  /api/devices                    — 列出所有设备
    //   POST /api/devices/{id}/connect       — 连接设备
    //   GET  /api/devices/{id}/read?address=D100&type=Int16
    //   POST /api/devices/{id}/write         — 写入数据
    //   GET  /api/devices/{id}/status        — 设备状态
    //   GET  /api/monitor/stream             — SSE 实时数据流
    //   GET  /api/packets                    — 报文日志
    //   GET  /api/alarms                     — 报警记录
}
```

### 5.6 验证标准

```
- 虚拟 PLC 可加载 JSON 场景运行
- 数据采集引擎可采集 1000 点/秒
- 协议网关 Modbus→MQTT 可稳定运行
- Web API 可通过 curl 执行设备读写
- 报文录制/回放功能完整
```

**Phase 5 完成标志**: Nexus 拥有 HSL 完全不具备的差异化功能，形成护城河。

---

## Phase 6: 品质 — 生产级可靠性（持续）

### 6.1 性能基准

```
目标基准（localhost, 单连接）:
  Modbus TCP ReadInt16:   1000 次 < 1s  (1ms/op)
  Siemens S7 ReadInt16:   1000 次 < 2s  (2ms/op)
  Modbus TCP Batch 100 地址: < 100ms
  Siemens S7 Batch 19 地址: < 50ms
  Modbus TCP Server:       10,000 req/s

基准测试框架:
  BenchmarkDotNet 集成
  GitHub Actions 每次提交自动跑基准
  性能回归自动报警
```

### 6.2 长时间稳定性测试

```
  自动化测试:
  - 模拟断网重连 1000 次 → 全部自动恢复
  - 7×24 小时读写压测 → 无内存泄漏
  - 100 并发客户端同时操作 → 无死锁
  - 模拟 PLC 重启 → 客户端自动重连恢复
```

### 6.3 安全审计

```
  - 无硬编码密钥/凭证
  - 无 SQL 注入（参数化查询）
  - 网络输入验证（防止恶意报文导致缓冲区溢出）
  - Dispose/Finalizer 审计（无资源泄漏）
  - 线程安全审计（无竞态条件）
```

### 6.4 兼容性矩阵

```
  测试目标:
  .NET Framework 4.6.2   ✅
  .NET Framework 4.8     ✅
  .NET Core 3.1          ✅
  .NET 6                 ✅
  .NET 8                 ✅
  Windows x64            ✅
  Windows x86            ✅
  Linux x64              ✅
  Linux ARM64            ✅ (树莓派/IoT 场景)
  macOS x64              ✅
```

### 6.5 真实设备验证矩阵

```
  目标: 公开测试报告，每种协议标注"已验证设备型号"

  Siemens:   S7-200Smart / S7-1200 / S7-1500 / S7-300
  Modbus:    任意 Modbus TCP/RTU 设备
  Mitsubishi: Q03UDVCPU / FX3U-485-BD / iQ-R
  Omron:     CP1H / CJ2M / NX1P2
  AB:        ControlLogix 5580 / CompactLogix 5380
  ...
```

**Phase 6 完成标志**: Nexus 达到生产级可靠性，有公开的性能数据和兼容性矩阵。

---

## 时间线总览

```
Month 1-2:  Phase 0 + Phase 1   — 止血 + 地基
Month 2-4:  Phase 2              — 协议深度（Top 10）
Month 4-6:  Phase 2 继续 + Phase 3 开始 — 深度 + 新协议
Month 6-8:  Phase 3              — 广度（新协议填补）
Month 8-9:  Phase 4              — 生态（NuGet/文档/示例）
Month 9-10: Phase 5              — 差异化（虚拟PLC/采集/网关）
Month 10+:  Phase 6              — 持续品质提升
```

---

## 最终目标数字

| 指标 | 当前 | Phase 2 后 | Phase 4 后 | 最终 |
|------|------|-----------|-----------|------|
| 协议库数量 | 41 | 41 | 65+ | 80+ |
| NotImplementedException | 65 | **0** | 0 | 0 |
| IBatchReadWrite | 7 | 15 | 20+ | 25+ |
| ISubscribeDevice | 3 | 10 | 15+ | 20+ |
| 虚拟 Server | 13 | 20 | 25 | 30+ |
| 单元测试 | 1098 | 3000+ | 5000+ | 8000+ |
| 源码总行数 | 48K | 80K | 120K | 150K+ |
| NuGet 下载 | 0 | — | 1000+ | 50万+ |
| 协议文档页 | 0 | 50 | 150 | 300+ |
| 示例代码 | 2 | 30 | 50 | 100+ |

---

## Agent 并行策略

基于文件所有权的零冲突并行:

```
Agent 1:  Phase 0 — Mitsubishi 全系列（Ascii/UDP/FX）
Agent 2:  Phase 0 — Siemens PPI 完整实现
Agent 3:  Phase 1 — TcpDeviceBase 自动重连 + 心跳 + 线程安全
Agent 4:  Phase 1 — DataConverter + StringConverter + StructConverter
Agent 5:  Phase 1 — AddressContext + ILogger 升级
Agent 6:  Phase 2 — Modbus 深度增强
Agent 7:  Phase 2 — Siemens 深度增强
Agent 8:  Phase 2 — Mitsubishi 深度增强
Agent 9:  Phase 2 — Omron 深度增强
Agent 10: Phase 2 — AllenBradley 深度增强
Agent 11: Phase 2 — B 级协议批量提升（Panasonic/Keyence/Beckhoff/LS/Fatek）
Agent 12: Phase 2 — 机器人协议重构（KUKA/FANUC/ABB/Yaskawa）
Agent 13: Phase 3 — Schneider Modicon 新协议
Agent 14: Phase 3 — S7 Plus + FX5U MC 新协议
Agent 15: Phase 3 — Omron NX/NJ + FINS Serial 新协议
Agent 16: Phase 3 — DNP3 + IEC 61850 电力协议
Agent 17: Phase 3 — BACnet MSTP + CANopen
Agent 18: Phase 3 — OPC UA / MQTT / Redis 增强
Agent 19: Phase 4 — NuGet 打包 + CI/CD + 文档站
Agent 20: Phase 5 — 虚拟 PLC 框架 + 场景引擎
```
