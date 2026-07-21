// Nexus Modbus TCP 快速上手示例
// 本示例演示如何连接 Modbus TCP 设备、读写寄存器
using Nexus;
using Nexus.Modbus;

Console.WriteLine("=== Nexus Modbus TCP 示例 ===\n");

// 1. 创建客户端（替换为你的 PLC IP 地址）
string ip = "192.168.1.100";
int port = 502;
byte station = 1;

using var client = new ModbusTcpClient(ip, port, station, timeout: 3000);

// 2. 连接
Console.WriteLine($"正在连接 {ip}:{port} ...");
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine($"❌ 连接失败: {connect.Message}");
    Console.WriteLine("\n提示: 确保设备已开机、IP 正确、防火墙允许 502 端口。");
    return;
}
Console.WriteLine("✅ 连接成功!");

// 3. 读单个寄存器
Console.WriteLine("\n--- 读保持寄存器 ---");
var value = client.ReadUInt16("40001");  // 40001 = 第一个保持寄存器
if (value.IsSuccess)
    Console.WriteLine($"  寄存器 40001 = {value.Content}");
else
    Console.WriteLine($"  读取失败: {value.Message}");

// 4. 读多个寄存器
Console.WriteLine("\n--- 读多个寄存器 ---");
var data = client.ReadBytes("40001", 10);  // 读 10 个寄存器 = 20 字节
if (data.IsSuccess)
    Console.WriteLine($"  读取 {data.Content.Length} 字节: {DataConverter.ToHexString(data.Content)}");
else
    Console.WriteLine($"  读取失败: {data.Message}");

// 5. 写寄存器
Console.WriteLine("\n--- 写保持寄存器 ---");
var write = client.Write("40001", (short)1234);
if (write.IsSuccess)
    Console.WriteLine("  ✅ 写入成功 (40001 ← 1234)");
else
    Console.WriteLine($"  ❌ 写入失败: {write.Message}");

// 6. 读线圈(布尔)
Console.WriteLine("\n--- 读线圈 ---");
var coil = client.ReadBool("00001");
if (coil.IsSuccess)
    Console.WriteLine($"  线圈 00001 = {(coil.Content ? "ON" : "OFF")}");
else
    Console.WriteLine($"  读取失败: {coil.Message}");

// 7. 断开
client.Disconnect();
Console.WriteLine("\n✅ 已断开连接。示例完成!");
