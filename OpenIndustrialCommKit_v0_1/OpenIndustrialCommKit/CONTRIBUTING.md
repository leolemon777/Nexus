# Contributing

## 新增协议插件步骤

1. 新建 `src/OpenIndustrialComm.Protocols.<Name>`。
2. 提供 `ProtocolDescriptor`。
3. 实现至少一个 Client：`IReadWriteDeviceClient`、`ISubscribeDeviceClient` 或 `IRawFrameClient`。
4. 添加地址解析器。
5. 添加 PDU/ADU 编解码单元测试。
6. 添加设备模拟器或 golden frame 测试。
7. 更新 `docs/protocol-matrix.csv`。

## 提交要求

- 不提交厂商商业文档。
- 不提交真实工厂 IP、账号、密码、PLC 程序。
- 现场测试结果脱敏。
- 写入类 API 必须有测试和安全说明。
