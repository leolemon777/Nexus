using System;
using Nexus.Yokogawa;

// Yokogawa Centum 通信示例
// 连接横河 PLC

var client = new YokogawaClient("192.168.1.30", 8000, 5000);

Console.WriteLine("=== Yokogawa 示例 ===");

// 1. 连接
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine($"连接失败: {connect.Message}");
    return;
}
Console.WriteLine("已连接");

// 2. 读取 HR0（Holding Register）
var readResult = client.ReadInt16("HR0");
if (readResult.IsSuccess)
    Console.WriteLine($"HR0 = {readResult.Content}");
else
    Console.WriteLine($"读取失败: {readResult.Message}");

// 3. 写入 HR10
var writeResult = client.Write("HR10", (short)100);
if (writeResult.IsSuccess)
    Console.WriteLine("写入 HR10 成功");

// 4. 断开
client.Disconnect();
Console.WriteLine("已断开");
