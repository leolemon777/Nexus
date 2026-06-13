# 贡献指南

感谢你对 Nexus 的贡献兴趣！

## 如何贡献

### 报告 Bug

1. 在 [Issues](../../issues) 中搜索是否已有相同问题
2. 如果没有，使用 [Bug Report 模板](../../issues/new?template=bug_report.md) 创建新 Issue
3. 包含：复现步骤、期望行为、实际行为、环境信息

### 提交功能请求

1. 使用 [Feature Request 模板](../../issues/new?template=feature_request.md) 创建新 Issue
2. 说明使用场景和期望的 API 设计

### 提交代码

1. Fork 本仓库
2. 创建特性分支：`git checkout -b feature/my-feature`
3. 提交更改：`git commit -m "feat: add my feature"`
4. 推送分支：`git push origin feature/my-feature`
5. 创建 Pull Request

### 提交规范

使用 [Conventional Commits](https://www.conventionalcommits.org/) 格式：

```
feat: 新增 Modbus FC23 支持
fix: 修复 S7 连接超时问题
docs: 更新 Modbus 地址格式文档
test: 添加 MC3E ASCII 集成测试
refactor: 重构 DataConverter 字节序处理
```

### 开发环境

```bash
# 克隆仓库
git clone https://github.com/your-org/Nexus2.0.git
cd Nexus2.0

# 还原依赖
dotnet restore Nexus.slnx

# 构建
dotnet build Nexus.slnx

# 运行测试
dotnet test Nexus.slnx

# 运行 WPF 调试工具
dotnet run --project src/Nexus.App
```

### 添加新协议

参见 [协议贡献指南](CONTRIBUTING_PROTOCOLS.md)。

基本步骤：
1. 创建 `src/Nexus.{Protocol}/` 项目（netstandard2.0）
2. 实现客户端类（继承 TcpDeviceBase/SerialDeviceBase/UdpDeviceBase）
3. 创建 `tests/Nexus.{Protocol}.Tests/` 测试项目
4. 添加到 `Nexus.slnx`
5. 可选：添加 WPF 调试页面

### 代码规范

- 使用 C# 最新语法（Nullable enable, LangVersion latest）
- 不要添加注释（除非必要）
- 不要添加不必要的抽象
- 遵循现有代码风格
- 所有 `ReadXxx`/`Write` 方法返回 `OperateResult<T>`，不抛异常
- `OperateResult<T>.Content` 是值类型，不要使用 `?.`

### 测试规范

- 每个协议至少有地址解析测试、帧构建测试、响应解析测试
- 使用虚拟服务器进行集成测试
- 使用 FakeSerialPort 进行串口协议测试
- 运行 `dotnet test Nexus.slnx` 确保全部通过
