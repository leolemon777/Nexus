using Nexus;
using Nexus.Modbus;

Console.WriteLine("=== 连接池示例 ===\n");

// 1. 创建连接池（最多 5 个连接）
using var pool = new ModbusTcpConnectionPool(
    ip: "127.0.0.1",
    port: 502,
    station: 1,
    timeout: 3000,
    maxPoolSize: 5);

Console.WriteLine("连接池已创建 (maxPoolSize=5)\n");

// 2. 单次读写（自动从池中获取连接，用完归还）
Console.WriteLine("--- 单次读写 ---");
var result = pool.ReadUInt16("40001");
if (result.IsSuccess)
    Console.WriteLine($"40001 = {result.Content}");
else
    Console.WriteLine($"读取失败: {result.Message}");

// 3. Execute 模式（自定义操作）
Console.WriteLine("\n--- Execute 模式 ---");
var execResult = pool.Execute(client =>
{
    // 在这里可以对同一个连接做多次操作
    client.Write("40001", (short)100);
    var v1 = client.ReadInt16("40001");
    client.Write("40002", (short)200);
    var v2 = client.ReadInt16("40002");
    return OperateResult<short>.Success((short)(v1.Content + v2.Content));
});
if (execResult.IsSuccess)
    Console.WriteLine($"D100 + D101 = {execResult.Content}");

// 4. 并发读写（连接池自动管理连接复用）
Console.WriteLine("\n--- 并发读写 (8线程 x 10次) ---");
var tasks = new Task[8];
var errors = new List<string>();
var errorLock = new object();

for (int t = 0; t < 8; t++)
{
    int tid = t;
    tasks[t] = Task.Run(() =>
    {
        for (int i = 0; i < 10; i++)
        {
            var r = pool.ReadUInt16("40001");
            if (!r.IsSuccess)
                lock (errorLock) errors.Add($"t{tid} i{i}: {r.Message}");
        }
    });
}
await Task.WhenAll(tasks);
Console.WriteLine($"完成! 错误数: {errors.Count}");

// 5. 连接池状态
Console.WriteLine($"\n--- 连接池状态 ---");
Console.WriteLine($"Active: {pool.ActiveCount}");
Console.WriteLine($"Idle: {pool.IdleCount}");

pool.Dispose();
Console.WriteLine("\n连接池已释放");
