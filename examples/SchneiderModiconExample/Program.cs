using System;
using Nexus.Schneider;

// Schneider Modicon 通信示例
// 连接施耐德 M340/M580 系列 PLC

var client = new SchneiderModiconClient("192.168.1.40", 502);
Console.WriteLine("=== Schneider Modicon 示例 ===");

// 1. 连接
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine($"连接失败: {connect.Message}");
    return;
}
Console.WriteLine("已连接");

// 2. 读取 %MW0
var readResult = client.ReadInt16("%MW0");
if (readResult.IsSuccess)
    Console.WriteLine($"%MW0 = {readResult.Content}");
else
    Console.WriteLine($"读取失败: {readResult.Message}");

// 3. 写入 %MW100
var writeResult = client.Write("%MW100", (short)1234);
if (writeResult.IsSuccess)
    Console.WriteLine("写入 %MW100 成功");

// 4. 读取布尔 %M0
var boolResult = client.ReadBool("%M0");
if (boolResult.IsSuccess)
    Console.WriteLine($"%M0 = {boolResult.Content}");

// 5. 断开
client.Disconnect();
Console.WriteLine("已断开");
