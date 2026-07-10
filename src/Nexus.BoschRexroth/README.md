# Nexus.BoschRexroth

Bosch Rexroth ctrlX PLC EtherNet/IP CIP protocol client for Nexus.

## 协议说明

**Bosch Rexroth ctrlX PLC 通过标准 EtherNet/IP-CIP（ODVA 公开规范）访问，而非 Bosch 私有协议。**

Bosch Rexroth 的 ctrlX CORE 控制器运行 ctrlX WORKS，支持标准 EtherNet/IP-CIP 访问 PLC 变量（Symbol/Tag）。协议层级与 Allen-Bradley ControlLogix 完全一致：

```
TCP → ENIP (Encapsulation) → CIP (Common Industrial Protocol)
```

因此本客户端**直接继承**成熟的 `Nexus.AllenBradley.AllenBradleyCipClient`，复用其：
- CIP 显式消息（explicit message）帧构造
- Tag 路径编码（`Program:xxx.Tag`、多维数组、符号段 0x91）
- 分段读写（Fragmented Read/Write，服务码 0x52/0x53）
- Multiple Service Packet 批量读
- `CipVirtualServer`（集成测试用，783 行，端到端验证）
- 4 个测试文件的覆盖（含并发、错误码、数据类型）

地址使用 ctrlX WORKS 中定义的符号变量名（Symbol），语法与 AB Tag 一致。

## Quick Start

```csharp
using Nexus.BoschRexroth;

using var client = new BoschCtrlxClient("192.168.1.1", port: 44818, slot: 0);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

// 读 ctrlX 符号变量（Tag）
var r = client.ReadInt32("MyDintVar");
if (r.IsSuccess) Console.WriteLine(r.Content);

// 写
client.Write("MyDintVar", 42);

// 数组元素 / 嵌套路径
var arr = client.ReadInt16("MyArray[0]");

client.Disconnect();
```

## Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `BoschCtrlxClient` | EtherNet/IP-CIP (TCP) | 继承 AllenBradleyCipClient，默认端口 44818 |

## 成熟度

- **协议本质**：标准 EtherNet/IP-CIP（ODVA 公开规范，成熟）
- **实现**：完整复用 `AllenBradleyCipClient`（含虚拟服务器 + 端到端测试）
- **实机验证**：继承自 AB CIP，建议首接核对 ctrlX 的 CIP Vendor ID 与路径配置
- **依赖**：`Nexus.AllenBradley`（CIP 栈提供方）

## 注意

- Bosch Rexroth 老型号 IndraControl L/XM 走 Sercos III / EtherCAT，不在本库覆盖范围
- 本库仅覆盖支持标准 EtherNet/IP-CIP 的 ctrlX 系列设备
