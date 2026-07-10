# Nexus.Toshiba

Toshiba V200/V100 PLC Modbus-variant protocol client for Nexus.

## 重要说明 / Honesty Note

**这是标准 Modbus TCP 直通客户端，不是 Toshiba 私有协议实现（如老型号 TOSDIC）。**

Toshiba V200 是内建以太网的微型 PLC，原生支持 Modbus TCP/IP（Client 与 Server 双角色）。本客户端将其作为 Modbus TCP Server 访问。

V200 的数据寄存器通过 Toshiba 约定映射到标准 Modbus 区（详见 Toshiba TIC V200 Ethernet Function Manual）。地址使用标准 Modbus 编号。

底层通讯完全复用标准 Modbus TCP（`Nexus.Modbus.ModbusTcpClient`）。

注意：老型号 TOSDIC 系列使用 Toshiba 私有帧协议（无公开规范），本库**不支持**。本库仅覆盖支持标准 Modbus TCP 的新一代 V-series 设备。

## Quick Start

```csharp
using Nexus.Toshiba;

using var client = new ToshibaClient("192.168.1.10", port: 502, station: 1);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

// 读保持寄存器 400101
var r = client.ReadInt16("400101");
if (r.IsSuccess) Console.WriteLine(r.Content);

// 写线圈 00005
client.Write("00005", true);

client.Disconnect();
```

## Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `ToshibaClient` | TCP (标准 Modbus 直通) | 默认端口 502，默认站号 1 |

## 成熟度

- **协议本质**：标准 Modbus TCP（成熟）
- **地址映射**：V200 寄存器区按 Toshiba 约定，地址使用标准 Modbus 编号；详细寄存器映射需参考具体工程的 Ethernet Function Manual
- **实机验证**：未实机验证
- **不支持**：老型号 TOSDIC 私有帧协议（无公开规范）
