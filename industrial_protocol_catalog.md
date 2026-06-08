# 工业与物联网通讯协议目录

此目录用于规划插件，不代表首版全部完成。优先级：P0 最先做，P1 常用，P2 行业扩展，P3 认证/实时/复杂协议。

## P0/P1 核心

| 类别 | 协议 | 主要用途 | 首版策略 |
|---|---|---|---|
| PLC/仪表 | Modbus TCP/RTU/ASCII | PLC、仪表、变频器、网关 | 完整实现 |
| PLC | Siemens S7 ISO-on-TCP | 西门子 PLC 数据块/存储区读写 | Client 优先 |
| PLC | Mitsubishi MC 1E/3E/4E | 三菱/兼容 PLC | Client 优先 |
| PLC | Omron FINS TCP/UDP/HostLink | 欧姆龙 PLC | Client 优先 |
| PLC | Allen-Bradley EtherNet/IP CIP | Logix Tag 读写 | Client/Scanner 优先 |
| OT 中台 | OPC UA Client/Server/PubSub | 跨厂商工业数据模型 | Client 优先 |
| IoT | MQTT 3.1.1/5、Sparkplug B | 边缘网关上云 | Client 与数据模型 |
| Web | HTTP/HTTPS REST、WebSocket | API、网关、网页实时数据 | 适配层 |
| 自定义 | Binary/ASCII DSL | 私有协议、无规则协议 | DSL 引擎 |

## P2 行业扩展

| 行业 | 协议 |
|---|---|
| 电力/能源 | IEC 60870-5-101/104、DNP3、IEC 61850、DL/T 645、DLMS/COSEM、SunSpec Modbus、OpenADR |
| 楼宇 | BACnet/IP、BACnet MS/TP、KNX/IP、KNX TP、M-Bus、Wireless M-Bus、DALI、LonWorks、EnOcean |
| 机器人/CNC | FANUC FOCAS、MTConnect、OPC UA Machine Tools、Universal Robots RTDE、ABB/KUKA/Yaskawa 接口 |
| 传感器/执行器 | IO-Link、CANopen、DeviceNet、J1939、HART、WirelessHART、ISA100.11a |

## P3 实时以太网/认证型

| 协议 | 注意点 |
|---|---|
| PROFINET | 实时通信、GSDML、认证、DCP/LLDP/RT Class |
| EtherCAT | 主站实时调度、ESC、ESI、分布式时钟 |
| EtherNet/IP Adapter/Scanner | CIP 对象模型、EDS、ODVA 认证 |
| POWERLINK | 实时调度、主从栈 |
| Sercos III | 实时以太网与驱动场景 |
| CC-Link IE/Field Basic | 规范与认证生态 |
