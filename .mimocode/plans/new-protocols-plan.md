# 新增协议 + 深度提升 + WPF增强 + 测试补充 + NuGet发布 准备

## 新增协议（9个）

### 批次1 - 国内工厂必备（高优先级）
1. **Schneider Modicon** - Modbus TCP变体，增加Unity Pro地址解析
2. **Mitsubishi FX5U MC** - MC协议扩展，二进制/ASCII格式
3. **Inovance H5U** - 汇川新型号，Modbus TCP变体

### 批次2 - 西门子/欧姆龙新一代
4. **S7 Plus** - TIA Portal协议扩展
5. **Omron NX/NJ** - FINS over Ethernet扩展

### 批次3 - 工业以太网/行业协议
6. **EtherNet/IP** - CIP显式消息
7. **CC-Link IE** - 三菱工业以太网
8. **BACnet MSTP** - 楼宇自动化串口
9. **HART** - 过程控制仪表

## 协议深度提升
- FX Serial Bool读写实现
- Modbus FC23批量读写增强
- Siemens S7 PDU优化

## WPF应用增强
- ModbusTcpViewModel重构继承ProtocolViewModelBase
- 报文录制/回放
- 曲线监控增强

## 测试补充
- 并发测试增强
- 新增协议测试
- 集成测试

## NuGet发布准备
- 打包配置
- CI/CD
- 文档站
