# Build

本骨架按 .NET 8 项目结构生成。当前包未包含 `.sln` 文件，可在本地执行：

```bash
dotnet new sln -n OpenIndustrialCommKit
dotnet sln add src/OpenIndustrialComm.Core/OpenIndustrialComm.Core.csproj
dotnet sln add src/OpenIndustrialComm.Transports/OpenIndustrialComm.Transports.csproj
dotnet sln add src/OpenIndustrialComm.Modbus/OpenIndustrialComm.Modbus.csproj
dotnet sln add tests/OpenIndustrialComm.Tests/OpenIndustrialComm.Tests.csproj
dotnet test
```

如果要兼容老项目，可新增 `netstandard2.0` target，但 TCP/Serial API 需要条件编译。
