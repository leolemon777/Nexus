# 无规则/私有协议 DSL 设计

很多仪表、老设备、国产控制器不是标准协议，而是“起始符 + 长度 + 命令 + 地址 + 数据 + 校验 + 结束符”的私有协议。不要为每个协议硬编码一个类，应提供声明式 DSL。

## JSON 示例

```json
{
  "name": "ExampleMeterAscii",
  "transport": "serial",
  "frame": {
    "start": "0x68",
    "end": "0x16",
    "lengthField": { "offset": 1, "size": 1, "includes": "payload" },
    "checksum": { "type": "sum8", "range": "start..payloadEnd", "offset": -2 }
  },
  "commands": {
    "readHolding": {
      "request": "68 {len:u8} 01 {addr:u16le} {count:u16le} {crc:u8} 16",
      "response": "68 {len:u8} 81 {addr:u16le} {data:bytes} {crc:u8} 16",
      "decode": [
        { "name": "value", "type": "u16", "endian": "little", "scale": 0.1 }
      ]
    }
  }
}
```

## DSL 必备能力

- 起止符切帧。
- 固定长度、长度字段、超时切帧。
- CRC16-Modbus、CRC16-CCITT、LRC、sum8、xor、BCC。
- 大端/小端/字节交换/字交换。
- bit、bool、int16、uint16、int32、uint32、float、double、BCD、ASCII、GBK/UTF-8 string。
- 请求模板和响应模板。
- 地址表达式。
- 批量读取规划。
- 错误码映射。

## 运行模式

- `Strict`：任何字段不匹配即失败。
- `Tolerant`：允许额外字段、可选字段、厂商扩展。
- `Probe`：用于未知设备协议探测和日志分析。
