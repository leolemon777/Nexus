# 架构设计

## 总体结构

```text
Application / MES / SCADA / Gateway / Edge
    │
    ├── Unified API: IReadWriteDeviceClient, ISubscribeDeviceClient, IRawFrameClient
    │
    ├── Data Model: Tag, Address, Value, Quality, Timestamp, Unit, Metadata
    │
    ├── Protocol Plugins
    │     ├── PLC: Modbus, Siemens S7, Mitsubishi MC, Omron FINS, Allen-Bradley CIP
    │     ├── SCADA/OT: OPC UA, IEC 104, DNP3, IEC 61850, BACnet
    │     ├── IoT: MQTT, HTTP, WebSocket, CoAP, LwM2M, AMQP, DDS
    │     └── Custom: Binary/ASCII DSL, checksum, endian, frame splitter
    │
    ├── Codec Layer: PDU/ADU, CRC/LRC/BCC, endian, bit/word/float/string codec
    │
    └── Transport Layer: TCP, UDP, Serial, TLS, WebSocket, CAN, BLE, USB
```

## 关键抽象

### 1. `IReadWriteDeviceClient`

统一读写，屏蔽 PLC/仪表/网关/IoT 端协议差异。

### 2. `ITransport`

只关心字节发送与接收，不理解 PLC 地址和协议语义。

### 3. `IFrameCodec`

把原始字节切帧、验帧、算校验、处理粘包/半包。

### 4. `IAddressParser`

把用户地址字符串解析成协议内部地址，例如：

- `hr:0` -> Modbus Holding Register 0
- `db1.dbw0` -> Siemens S7 DB1 Word 0
- `D100` -> Mitsubishi/Keyence/Omron 风格寄存器
- `ns=2;s=Machine.Speed` -> OPC UA NodeId

## 结果模型

工业通讯失败比成功更常见，所以所有读写都返回 `OperationResult<T>`，不直接抛业务异常。

```csharp
public sealed record OperationResult<T>(
    bool Success,
    T? Value,
    string? ErrorCode,
    string? Message,
    Exception? Exception = null
);
```

## 插件加载

首版使用静态 NuGet 包；中后期支持：

- `IProtocolDriverFactory`
- 反射加载插件
- JSON/YAML 协议描述
- 设备模板库
- 连接池与多设备调度

## 安全默认值

- 默认只读。
- 写入需要显式启用。
- 所有写入可配置白名单。
- 默认超时和重试次数保守。
- 支持连接审计、操作审计、危险地址拦截。
