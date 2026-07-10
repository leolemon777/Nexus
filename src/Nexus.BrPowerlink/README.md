# Nexus.BrPowerlink

B&R POWERLINK SDO protocol client for Nexus.

## 重要说明 / Honesty Note

**本库实现 POWERLINK 的 SDO（Service Data Object）请求-应答访问，不是完整的 POWERLINK 实时通信实现。**

POWERLINK 是 EPSG（EtherNET POWERLINK Standardization Group）公开标准的实时以太网协议。完整的实时通信涉及 MN（Managing Node）与 CN（Controlled Node）之间复杂的 **Preq/Pres 周期调度帧**，实现完整实时调度非常复杂。

本库的定位：
- ✅ **实现**：SDO 请求-应答，用于读写对象字典（Object Dictionary）中的节点配置与参数
- ❌ **不实现**：Preq/Pres 实时周期调度（实时数据交换）

适合配置/参数读写、监控场景；**不适合实时周期数据交换**。

为简化实现，本库采用 **TCP 上的 SDO 封装**（自定义封装，非 EPSG 标准 UDP 多播实时帧）。

## 地址格式

对象字典地址：`[node.]index.subindex`

- `1.6000.0` — 节点 1、索引 0x6000、子索引 0
- `6000.0x00` — 默认节点 1、索引 0x6000、子索引 0（十六进制）
- `0x6000.1` — 索引支持十进制或 0x 十六进制

## Quick Start

```csharp
using Nexus.BrPowerlink;

using var client = new BrPowerlinkClient("192.168.1.1", port: 34962);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

// 读对象字典节点 1 / 0x6000 / 子索引 0
var r = client.ReadInt16("1.6000.0");
if (r.IsSuccess) Console.WriteLine(r.Content);

// 写参数
client.Write("1.6000.0", (short)42);

client.Disconnect();
```

## 帧格式（TCP SDO 封装）

```
读请求:  [0x01][nodeId 1B][index 2B BE][subIndex 1B][size 2B]
写请求:  [0x02][nodeId 1B][index 2B BE][subIndex 1B][size 2B][data N]
响应头:  [error 4B BE][payloadLen 2B BE]  error=0 时后跟 payload
```

## Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `BrPowerlinkClient` | TCP (SDO 封装) | 自定义端口 34962，默认节点 1 |

## 成熟度

- **协议本质**：POWERLINK SDO（基于 EPSG 公开规范），TCP 封装
- **实现范围**：SDO 读/写对象字典（非完整 Preq/Pres 实时调度）
- **虚拟服务器**：模拟 MN 的 SDO 请求-应答
- **实机验证**：未实机验证。实时周期通信需另行实现完整 POWERLINK 调度（不在本库范围）
- **规范来源**：EPSG POWERLINK Communication Profile 规范（公开）
