using System;
using Nexus.Inovance;

// 汇川 Easy 协议通信示例
// 连接汇川 H3U/H5U 系列 PLC

var client = new InovanceEasyClient("192.168.1.10", 502, 3000);

Console.WriteLine("=== 汇川 Easy 示例 ===");

// 1. 连接
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine($"连接失败: {connect.Message}");
    return;
}
Console.WriteLine("已连接");

// 2. 读取 D100
var readResult = client.ReadInt16("D100");
if (readResult.IsSuccess)
    Console.WriteLine($"D100 = {readResult.Content}");
else
    Console.WriteLine($"读取失败: {readResult.Message}");

// 3. 写入 D200
var writeResult = client.Write("D200", (short)5678);
if (writeResult.IsSuccess)
    Console.WriteLine("写入 D200 成功");

// 4. 读取布尔量 M0
var boolResult = client.ReadBool("M0");
if (boolResult.IsSuccess)
    Console.WriteLine($"M0 = {boolResult.Content}");

// 5. 断开
client.Disconnect();
Console.WriteLine("已断开");
