using Nexus.Siemens;

Console.WriteLine("=== Siemens S7 读取 DB 块示例 ===\n");

// 1. 创建客户端（S7-1200/1500）
using var client = new SiemensS7Client(SiemensPLCS.S7_1200, "192.168.0.1");

// 2. 连接（自动执行 COTP + S7 握手）
Console.WriteLine("正在连接 S7 PLC ...");
var result = client.Connect();
if (!result.IsSuccess)
{
    Console.WriteLine($"连接失败: {result.Message}");
    return;
}
Console.WriteLine("连接成功!\n");

// 3. 读取 DB 块
Console.WriteLine("--- 读取 DB1 ---");

// 读取 DB1 中的 Int16（DBW = DB Word）
var temp = client.ReadInt16("DB1.DBW0");
if (temp.IsSuccess)
    Console.WriteLine($"DB1.DBW0 (温度) = {temp.Content}°C");

// 读取 DB1 中的 Float（DBD = DB Double word）
var pressure = client.ReadFloat("DB1.DBD2");
if (pressure.IsSuccess)
    Console.WriteLine($"DB1.DBD2 (压力) = {pressure.Content:F2} bar");

// 读取 DB1 中的 Bool（DBX = DB Bit）
var running = client.ReadBool("DB1.DBX4.0");
if (running.IsSuccess)
    Console.WriteLine($"DB1.DBX4.0 (运行) = {running.Content}");

// 读取 DB1 中的 String
var name = client.ReadString("DB1.DBW10", 20);
if (name.IsSuccess)
    Console.WriteLine($"DB1.DBW10 (名称) = '{name.Content}'");

// 4. 写入 DB 块
Console.WriteLine("\n--- 写入 DB1 ---");
client.Write("DB1.DBW0", (short)2500);       // 写入温度设定值
client.Write("DB1.DBD2", (float)1.5f);       // 写入压力设定值
client.Write("DB1.DBX4.0", true);            // 写入运行命令

// 5. 批量读取多个 DB 地址
Console.WriteLine("\n--- 批量读取 ---");
var batch = client.BatchRead(new[] { "DB1.DBW0", "DB1.DBW2", "DB1.DBW4" });
if (batch.IsSuccess)
    foreach (var kv in batch.Content)
        Console.WriteLine($"  {kv.Key} = {kv.Value}");

// 6. 读取输入/输出/标志位
Console.WriteLine("\n--- I/O 读取 ---");
var i0 = client.ReadInt16("I0");
var q0 = client.ReadInt16("Q0");
var m0 = client.ReadInt16("M0");
Console.WriteLine($"I0 = {i0.Content}");
Console.WriteLine($"Q0 = {q0.Content}");
Console.WriteLine($"M0 = {m0.Content}");

client.Disconnect();
Console.WriteLine("\n已断开连接");
