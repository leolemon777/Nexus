using Nexus.Schneider.Modicon;

Console.WriteLine("=== Schneider Modicon 示例 ===\n");

using var client = new SchneiderModiconClient("192.168.1.100", port: 502, station: 1);

Console.WriteLine("正在连接 ...");
var result = client.Connect();
if (!result.IsSuccess) { Console.WriteLine($"连接失败: {result.Message}"); return; }
Console.WriteLine("连接成功!\n");

// Unity Pro 地址格式
Console.WriteLine("--- Unity Pro 地址格式 ---");

// %MW = 保持寄存器（字）
client.Write("%MW100", (short)2500);
Console.WriteLine($"%MW100 = {client.ReadInt16("%MW100").Content}");

// %MD = 保持寄存器（双字，浮点数）
client.Write("%MD200", (float)3.14f);
Console.WriteLine($"%MD200 = {client.ReadFloat("%MD200").Content:F4}");

// %MX = 线圈（位）
client.Write("%MX100.5", true);
Console.WriteLine($"%MX100.5 = {client.ReadBool("%MX100.5").Content}");

// %IW = 输入寄存器（只读）
Console.WriteLine($"%IW0 = {client.ReadInt16("%IW0").Content}");

// %QW = 输出寄存器
Console.WriteLine($"%QW0 = {client.ReadInt16("%QW0").Content}");

// 批量读取
Console.WriteLine("\n--- 批量读取 ---");
var batch = client.BatchRead(new[] { "%MW100", "%MW101", "%MW102" });
if (batch.IsSuccess)
    foreach (var kv in batch.Content)
        Console.WriteLine($"  {kv.Key} = {kv.Value}");

// 诊断读取
Console.WriteLine("\n--- 模块诊断 ---");
var diag = client.ReadModuleDiagnostics(0, 10);
if (diag.IsSuccess)
    Console.WriteLine($"诊断数据: {BitConverter.ToString(diag.Content)}");

client.Disconnect();
Console.WriteLine("\n已断开连接");
