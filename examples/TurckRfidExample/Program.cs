// Nexus Turck BLident RFID 快速上手示例
using Nexus;
using Nexus.Turck;

Console.WriteLine("=== Nexus Turck RFID 示例 ===\n");

string ip = "192.168.1.200";

using var reader = new TurckReaderClient(ip, 10000, 5000);

Console.WriteLine($"正在连接 {ip}:10000 ...");
var connect = reader.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine($"连接失败: {connect.Message}");
    return;
}
Console.WriteLine("连接成功!\n");

// 读 RFID 标签 UID
Console.WriteLine("--- 读标签 UID ---");
var uid = reader.ReadUid();
if (uid.IsSuccess)
    Console.WriteLine($"  标签 UID: {uid.Content}");
else
    Console.WriteLine($"  读 UID 失败: {uid.Message}");

// 读数据块
Console.WriteLine("\n--- 读数据块 0 ---");
var data = reader.ReadBlocks(0, 1);
if (data.IsSuccess)
    Console.WriteLine($"  块 0 数据: {DataConverter.ToHexString(data.Content)}");
else
    Console.WriteLine($"  读数据失败: {data.Message}");

reader.Disconnect();
Console.WriteLine("\n示例完成!");
