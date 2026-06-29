using BenchmarkDotNet.Running;
using Nexus.Benchmarks;

// 运行所有基准测试
// 用法: dotnet run -c Release -- --filter "*Modbus*"
// 用法: dotnet run -c Release -- --filter "*DataConverter*"
// 用法: dotnet run -c Release -- --filter "*Batch*"
// 用法: dotnet run -c Release -- --filter "*Concurrent*"

BenchmarkSwitcher.FromTypes(new[]
{
    typeof(ModbusBenchmarks),
    typeof(ModbusBatchBenchmarks),
    typeof(ModbusConcurrentBenchmarks),
    typeof(DataConverterBenchmarks),
}).RunAll();
