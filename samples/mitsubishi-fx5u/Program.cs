using Nexus.Mitsubishi.Fx5u;

Console.WriteLine("=== Mitsubishi FX5U 示例 ===\n");

// 1. 创建客户端（FX5U 默认端口 4999）
using var client = new Fx5uClient("192.168.1.100", port: 4999, timeout: 5000);

// 2. 连接
Console.WriteLine("正在连接 FX5U ...");
var result = client.Connect();
if (!result.IsSuccess)
{
    Console.WriteLine($"连接失败: {result.Message}");
    return;
}
Console.WriteLine("连接成功!\n");

// 3. 读写数据寄存器 D
Console.WriteLine("--- 数据寄存器 D ---");
var d100 = client.ReadInt16("D100");
Console.WriteLine($"D100 = {d100.Content}");

client.Write("D100", (short)1234);
Console.WriteLine($"写入 D100 = 1234, 回读 = {client.ReadInt16("D100").Content}");

// 4. 读写浮点数
Console.WriteLine("\n--- 浮点数 ---");
client.Write("D200", (float)36.5f);
Console.WriteLine($"D200 = {client.ReadFloat("D200").Content:F2}");

// 5. 读写 32 位整数
Console.WriteLine("\n--- 32 位整数 ---");
client.Write("D300", 100000);
Console.WriteLine($"D300 = {client.ReadInt32("D300").Content}");

// 6. 读写位 M
Console.WriteLine("\n--- 位操作 ---");
client.Write("M0", true);
client.Write("M1", false);
Console.WriteLine($"M0 = {client.ReadBool("M0").Content}");
Console.WriteLine($"M1 = {client.ReadBool("M1").Content}");

// 7. 读写输入/输出
Console.WriteLine("\n--- I/O ---");
Console.WriteLine($"X0 = {client.ReadBool("X0").Content}");
Console.WriteLine($"Y0 = {client.ReadBool("Y0").Content}");

// 8. 读写定时器/计数器
Console.WriteLine("\n--- 定时器/计数器 ---");
Console.WriteLine($"T0 = {client.ReadInt16("T0").Content}");
Console.WriteLine($"C0 = {client.ReadInt16("C0").Content}");

// 9. 数据订阅
Console.WriteLine("\n--- 数据订阅 ---");
client.Subscribe("D100", intervalMs: 500, dataType: "Int16");
client.OnDataChanged += (s, e) =>
    Console.WriteLine($"  [{e.Timestamp:HH:mm:ss}] {e.Address}: {e.OldValue} → {e.NewValue}");

client.StartSubscriptions();
Console.WriteLine("已启动订阅，等待数据变化...");
await Task.Delay(3000);
client.StopSubscriptions();

client.Disconnect();
Console.WriteLine("\n已断开连接");
