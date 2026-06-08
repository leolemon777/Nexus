# 实现路线图

## Phase 0：底座

- Core API：`OperationResult<T>`、`DeviceEndpoint`、`ProtocolDescriptor`、`IReadWriteDeviceClient`。
- Transport：TCP、UDP、Serial 抽象。
- Codec：Endian、CRC16、LRC、长度头、起止符、粘包处理。
- Test：协议帧 golden vectors。

## Phase 1：工业最常用协议

优先实现：

1. Modbus TCP / RTU / ASCII
2. Siemens S7 ISO-on-TCP 基础读写
3. Mitsubishi MC 3E/4E Binary/ASCII
4. Omron FINS TCP/UDP
5. Allen-Bradley EtherNet/IP CIP Tag Read/Write
6. OPC UA Client 基础读写与订阅
7. MQTT 3.1.1/5 Client 与 Sparkplug B 数据模型适配
8. HTTP/HTTPS REST 与 WebSocket

## Phase 2：行业协议

- 电力：IEC 60870-5-101/104、DNP3、IEC 61850 MMS/GOOSE/SV、DL/T 645、DLMS/COSEM。
- 楼宇：BACnet/IP、BACnet MS/TP、KNX/IP、M-Bus、DALI、LonWorks。
- 机器人/CNC：FANUC FOCAS、MTConnect、OPC UA Machine Tools、UR RTDE、ABB/KUKA/Yaskawa 适配。

## Phase 3：实时以太网与认证型协议

- PROFINET、EtherCAT、EtherNet/IP Scanner/Adapter、POWERLINK、Sercos III、CC-Link IE。
- 此类协议通常涉及实时调度、网卡驱动、认证或会员规范，建议插件独立，先做“网关侧数据采集”和“配置解析”，再做完整主站/从站。

## Phase 4：无规则/私有协议平台

- 自定义协议 DSL。
- 可视化协议帧分析器。
- PCAP/串口日志回放。
- 自动推断帧边界、校验算法候选、大小端、寄存器映射。
- Fuzz 测试与健壮性测试。

## Phase 5：生态

- 设备模板库：厂商、型号、地址表、单位、倍率、读写限制。
- 模拟器：Modbus Server、S7 Mock、FINS Mock、BACnet Mock、OPC UA Mock。
- 网关：协议采集 -> MQTT/HTTP/Kafka/OPC UA PubSub。
- Web 控制台：连接测试、地址浏览、读写调试。
