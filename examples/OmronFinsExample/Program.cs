using System;
using Nexus.Omron;

// Omron FINS TCP 示例 — 读写 PLC 数据
var client = new FinsTcpClient("192.168.1.40", 9600);

// 1. 连接
var conn = client.Connect();
if (!conn.IsSuccess) { Console.WriteLine($"连接失败: {conn.Message}"); return; }
Console.WriteLine("已连接 Omron PLC");

// 2. 读取 Int16
var d0 = client.ReadInt16("D0");
if (d0.IsSuccess)
    Console.WriteLine($"D0 = {d0.Content}");

// 3. 写入 Int16
var wr = client.Write("D100", (short)1234);
Console.WriteLine(wr.IsSuccess ? "D100 写入成功" : $"写入失败: {wr.Message}");

// 4. 读取 Float
var f0 = client.ReadFloat("D200");
if (f0.IsSuccess)
    Console.WriteLine($"D200 (Float) = {f0.Content:F2}");

// 5. 写入 Float
client.Write("D200", 25.5f);

// 6. 断开
client.Disconnect();
Console.WriteLine("已断开连接");
