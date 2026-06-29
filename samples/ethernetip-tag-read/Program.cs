using Nexus.EtherNetIp;

Console.WriteLine("=== EtherNet/IP 标签读写示例 ===\n");

using var client = new EtherNetIpClient("192.168.1.100", port: 44818);

Console.WriteLine("正在连接 ...");
var result = client.Connect();
if (!result.IsSuccess) { Console.WriteLine($"连接失败: {result.Message}"); return; }
Console.WriteLine("连接成功!\n");

// 读写标量标签
Console.WriteLine("--- 标量标签 ---");
var temp = client.ReadInt16("Temperature");
Console.WriteLine($"Temperature = {temp.Content}");

client.Write("Temperature", (short)2500);
Console.WriteLine($"写入 Temperature = 2500, 回读 = {client.ReadInt16("Temperature").Content}");

// 读写浮点标签
Console.WriteLine("\n--- 浮点标签 ---");
var pressure = client.ReadFloat("Pressure");
Console.WriteLine($"Pressure = {pressure.Content:F2}");

client.Write("Pressure", (float)3.14f);
Console.WriteLine($"写入 Pressure = 3.14, 回读 = {client.ReadFloat("Pressure").Content:F4}");

// 读写布尔标签
Console.WriteLine("\n--- 布尔标签 ---");
var running = client.ReadBool("MotorRunning");
Console.WriteLine($"MotorRunning = {running.Content}");

client.Write("MotorRunning", true);
Console.WriteLine($"写入 MotorRunning = true, 回读 = {client.ReadBool("MotorRunning").Content}");

// 读写数组
Console.WriteLine("\n--- 数组标签 ---");
var arrVal = client.ReadInt16("DataArray[0]");
Console.WriteLine($"DataArray[0] = {arrVal.Content}");

client.Write("DataArray[0]", (short)999);
Console.WriteLine($"写入 DataArray[0] = 999, 回读 = {client.ReadInt16("DataArray[0]").Content}");

// 批量读取
Console.WriteLine("\n--- 批量读取 ---");
var batch = client.BatchRead(new[] { "Temperature", "Pressure", "MotorRunning" });
if (batch.IsSuccess)
    foreach (var kv in batch.Content)
        Console.WriteLine($"  {kv.Key} = {kv.Value}");

client.Disconnect();
Console.WriteLine("\n已断开连接");
