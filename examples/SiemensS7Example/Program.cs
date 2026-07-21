// Nexus Siemens S7 快速上手示例
using Nexus;
using Nexus.Siemens;

Console.WriteLine("=== Nexus Siemens S7 示例 ===\n");

string ip = "192.168.1.1";

using var plc = new SiemensS7Net(ip, SiemensPLCS.S1200);

Console.WriteLine($"正在连接 {ip} ...");
var connect = plc.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine($"连接失败: {connect.Message}");
    return;
}
Console.WriteLine("连接成功!\n");

// 读 DB 块数据
Console.WriteLine("--- 读 DB1 数据 ---");
var dbw0 = plc.ReadInt16("DB1.DBW0");
Console.WriteLine($"  DB1.DBW0 (Int16) = {dbw0.IsSuccess ? dbw0.Content.ToString() : dbw0.Message}");

var dbd0 = plc.ReadFloat("DB1.DBD0");
Console.WriteLine($"  DB1.DBD0 (Float) = {dbd0.IsSuccess ? dbd0.Content.ToString("F2") : dbd0.Message}");

// 读 M 区
Console.WriteLine("\n--- 读 M 区 ---");
var mw100 = plc.ReadUInt16("MW100");
Console.WriteLine($"  MW100 = {mw100.IsSuccess ? mw100.Content.ToString() : mw100.Message}");

// 写数据
Console.WriteLine("\n--- 写数据 ---");
var write = plc.Write("DB1.DBW0", (short)42);
Console.WriteLine($"  写 DB1.DBW0 ← 42: {write.IsSuccess ? "成功" : write.Message}");

plc.Disconnect();
Console.WriteLine("\n示例完成!");
