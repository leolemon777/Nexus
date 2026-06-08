# Nexus.Modbus.Tests

此目录保留为 unit test 命名空间占位。

单元测试策略：Modbus 的所有读写逻辑已通过 `tests/Nexus.Modbus.IntegrationTest`
端到端验证（启动真实 TcpListener + ModbusTcpServer + ModbusTcpClient），
不再单独建空壳 unit test 项目以避免 slnx 中出现 0 测试的尴尬。

待 Modbus RTU/ASCII/UCP/Over-TCP 等变体实现后，
再为各变体单独建 `Nexus.Modbus.Rtu.Tests` / `Nexus.Modbus.Ascii.Tests` 等。
