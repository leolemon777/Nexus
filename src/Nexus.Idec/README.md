# Nexus.Idec

IDEC MicroSmart Computer Link（上位链接）原生协议 client for Nexus.

## 协议说明

**这是 IDEC 自有的 Computer Link（上位链接）原生 ASCII 协议，不是 Modbus。**

基于公开手册 [fc4a_protocol_im.pdf](https://docs.galco.com/techdoc/idec/fc4a_protocol_im.pdf)（IDEC MicroSmart Communication Protocol Manual，公开无需 NDA）实现。

IDEC Computer Link 用于 PC/HMI ↔ MicroSmart / FC4A / FC5A / FC6A PLC 通信。上位机永远为 master，每次事务由 PC 发起请求、PLC 应答。

### 帧格式（ASCII 模式，主推）

```
请求:   [ENQ][站号 hex][命令 2][数据类型码 1][operand 6][count 2][BCC 2][CR]
成功:   [STX][站号][数据][ETX][BCC 2][CR]
失败:   [NAK][站号][错误码 1][BCC 2][CR]
BCC   = 站号到 BCC 前一字节的全部字节 XOR（2 字符 ASCII-HEX）
```

### 命令族

| 命令 | 含义 |
|------|------|
| `R1` / `R2` / `R3` | 读单点 / 连续读（本库主推）/ 扩展读 |
| `W1` / `W2` / `W3` | 写单点 / 连续写（本库主推）/ 扩展写 |

### 数据类型码（1 char）

`D`(数据寄存器)、`X`(输入，八进制)、`Y`(输出，八进制)、`M`(内部继电器)、`T`(定时器)、`C`(计数器)。

## Quick Start

```csharp
using Nexus.Idec;

// FC6A 以太网口透传，或串口服务器
using var client = new IdecHostLinkClient("192.168.1.5", port: 502, station: 0);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

// 读数据寄存器 D100
var r = client.ReadInt16("D100");
if (r.IsSuccess) Console.WriteLine(r.Content);

// 读输入 X7（八进制）
var b = client.ReadBool("X7");

// 写 D100
client.Write("D100", (short)1234);

client.Disconnect();
```

## Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `IdecHostLinkClient` | TCP (ASCII 透传) | Computer Link 原生协议，默认站号 0 |

## 成熟度

- **协议本质**：IDEC Computer Link 原生 ASCII 协议（基于公开手册）
- **实现范围**：R2 连续读 + W2 连续写（覆盖最常用场景），D/X/Y/M/T/C 全区域
- **虚拟服务器**：模拟 R2/W2 主场景，未覆盖 R1/R3/W1/W3 扩展命令
- **实机验证**：未实机验证。默认串口参数 9600/Even/7/1，本库通过 TCP 透传访问
- **规范来源**：fc4a_protocol_im.pdf（公开手册）
