using System;
using Nexus.AllenBradley;

// Allen-Bradley CIP 示例 — 读写 ControlLogix/CompactLogix Tag
var client = new AllenBradleyCipClient("192.168.1.50", 44819);

// 1. 连接
var conn = client.Connect();
if (!conn.IsSuccess) { Console.WriteLine($"连接失败: {conn.Message}"); return; }
Console.WriteLine("已连接 Allen-Bradley PLC");

// 2. 读取 Tag (DINT)
var val = client.ReadInt32("MyTag");
if (val.IsSuccess)
    Console.WriteLine($"MyTag = {val.Content}");

// 3. 写入 Tag
var wr = client.Write("MyTag", 42);
Console.WriteLine(wr.IsSuccess ? "MyTag 写入成功" : $"写入失败: {wr.Message}");

// 4. 读取 REAL (Float)
var temp = client.ReadFloat("Temperature");
if (temp.IsSuccess)
    Console.WriteLine($"Temperature = {temp.Content:F2}");

// 5. 读取 BOOL
var bit = client.ReadBool("Motor_Running");
if (bit.IsSuccess)
    Console.WriteLine($"Motor_Running = {bit.Content}");

// 6. 断开
client.Disconnect();
Console.WriteLine("已断开连接");
