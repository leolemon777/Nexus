using System;
using Nexus.LsElectric;

// LS Electric XGT 通信示例
// 连接 LS XGB/XBC 系列 PLC

var client = new LsXgtTcpClient("192.168.1.20", 2004);

Console.WriteLine("=== LS XGT 示例 ===");

// 1. 连接
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine($"连接失败: {connect.Message}");
    return;
}
Console.WriteLine("已连接");

// 2. 读取 D100
var readResult = client.ReadInt16("D0000");
if (readResult.IsSuccess)
    Console.WriteLine($"D0000 = {readResult.Content}");
else
    Console.WriteLine($"读取失败: {readResult.Message}");

// 3. 写入 D100
var writeResult = client.Write("D0100", (short)999);
if (writeResult.IsSuccess)
    Console.WriteLine("写入 D0100 成功");

// 4. 读取浮点数
var floatResult = client.ReadFloat("D0200");
if (floatResult.IsSuccess)
    Console.WriteLine($"D0200(Float) = {floatResult.Content}");

// 5. 断开
client.Disconnect();
Console.WriteLine("已断开");
