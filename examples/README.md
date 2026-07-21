# Nexus 快速上手示例

每个示例都是独立的控制台程序，可以直接运行。

## 示例列表

| 示例 | 说明 |
|------|------|
| [ModbusTcpExample](ModbusTcpExample/) | 连接 Modbus TCP 设备，读写寄存器/线圈 |
| [SiemensS7Example](SiemensS7Example/) | 连接西门子 S7-1200/1500 PLC，读写 DB 块/M 区 |
| [TurckRfidExample](TurckRfidExample/) | 连接 Turck BLident RFID 读卡器，读 UID/数据块 |

## 运行方法

```bash
# 进入示例目录
cd examples/ModbusTcpExample

# 运行（需要先 dotnet build 整个解决方案）
dotnet run
```

> ⚠️ 示例中的 IP 地址是占位符，请替换为你实际设备的 IP。
> 没有真实设备？可以用虚拟服务器（如 `ModbusTcpServer`）在本地测试。

## 没有硬件怎么办？

Nexus 内置虚拟服务器，可以模拟设备响应。参考各协议测试项目中的 `*VirtualServer.cs`。
