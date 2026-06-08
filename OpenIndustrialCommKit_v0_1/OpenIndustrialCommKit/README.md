# OpenIndustrialCommKit

> 开源工业通讯协议库蓝图与 .NET 8 首版骨架。目标是做成类似 HSLCommunication 这种“工业现场数据接入 + IoT 传输 + 自定义协议解析”的通用通讯库，但源码、协议适配层、测试夹具和文档全部开放。

## 设计边界

本项目不把“所有协议”理解为一个巨大类库，而是拆成四层：

1. **Transport / 传输层**：TCP、UDP、TLS、串口 RS-232/RS-485、CAN、BLE、USB、WebSocket、QUIC。
2. **Frame / 编解码层**：MBAP、RTU、ASCII、SLIP、COBS、长度头、起止符、CRC/LRC/BCC、自定义二进制帧。
3. **Protocol / 协议适配层**：Modbus、S7、FINS、MC、CIP、OPC UA、BACnet、IEC 104、DNP3、MQTT、HTTP、CoAP 等。
4. **Model / 数据模型层**：统一 Tag、Address、Read/Write、Subscribe、Batch、Quality、Timestamp、Unit、Scale、Metadata。

## 首版可实现范围

- `OpenIndustrialComm.Core`：统一接口、结果模型、地址模型、协议描述。
- `OpenIndustrialComm.Transports`：TCP Transport 抽象与实现骨架。
- `OpenIndustrialComm.Modbus`：Modbus TCP Client、Modbus RTU CRC 与帧工具骨架。
- `docs/protocol-matrix.csv`：协议覆盖矩阵。
- `docs/implementation-roadmap.md`：分期路线。
- `docs/custom-protocol-dsl.md`：无规则/私有协议解析 DSL 设计。

## 快速示例

```csharp
using OpenIndustrialComm.Modbus;

await using var plc = new ModbusTcpClient("192.168.1.10", port: 502, unitId: 1);
await plc.ConnectAsync();

var value = await plc.ReadUInt16Async("hr:0");
Console.WriteLine(value.Value);

await plc.WriteUInt16Async("hr:10", 1234);
```

## 命名建议

可选项目名：

- `OpenIndustrialCommKit` / `OICK`
- `OpenOTComm`
- `PlantLink`
- `OpenFactoryBus`
- `UniIndustrialNet`

## 法务与协议注意

- 不复制 HSLCommunication 或其他商业库源码。
- 优先实现公开标准与公开文档协议。
- 专有协议、认证协议、受商标/会员约束的协议做成独立插件，必要时只提供接口和测试框架。
- 真实工业现场接入必须提供只读模式、写入白名单、速率限制、审计日志和安全提示。

## 推荐开源策略

- 首版许可证：MIT。若未来涉及大量企业贡献和专利风险，建议评估 Apache-2.0。
- 插件命名：`OpenIndustrialComm.Protocols.Modbus`、`OpenIndustrialComm.Protocols.SiemensS7`。
- 兼容目标：`.NET 8` 为主，后续可补 `.NET Standard 2.0/2.1`。
