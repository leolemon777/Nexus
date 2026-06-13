using System;
using Nexus.Beckhoff;

// Beckhoff ADS 通信示例
// 连接 Beckhoff TwinCAT PLC

var client = new BeckhoffAdsClient("192.168.1.100");
client.Timeout = 5000;

Console.WriteLine("=== Beckhoff ADS 示例 ===");

// 1. 连接
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine($"连接失败: {connect.Message}");
    return;
}
Console.WriteLine("已连接");

// 2. 读取 PLC 状态
var state = client.ReadState();
if (state.IsSuccess)
    Console.WriteLine($"PLC 状态: {state.Content.AdsStateValue}");

// 3. 读取符号变量
var readResult = client.ReadInt16("MAIN.Counter");
if (readResult.IsSuccess)
    Console.WriteLine($"MAIN.Counter = {readResult.Content}");
else
    Console.WriteLine($"读取失败: {readResult.Message}");

// 4. 写入变量
var writeResult = client.Write("MAIN.SetValue", (short)42);
if (writeResult.IsSuccess)
    Console.WriteLine("写入成功");

// 5. 断开
client.Disconnect();
Console.WriteLine("已断开");
