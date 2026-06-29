using Nexus.Modbus;

Console.WriteLine("=== Modbus TCP 基础读写示例 ===\n");

// 1. 创建客户端
using var client = new ModbusTcpClient("127.0.0.1", port: 502, station: 1, timeout: 3000);

// 2. 连接
Console.WriteLine("正在连接 127.0.0.1:502 ...");
var connectResult = client.Connect();
if (!connectResult.IsSuccess)
{
    Console.WriteLine($"连接失败: {connectResult.Message}");
    Console.WriteLine("提示: 请确保 Modbus TCP 服务端已启动在 502 端口");
    return;
}
Console.WriteLine("连接成功!\n");

// 3. 读取保持寄存器
Console.WriteLine("--- 读取保持寄存器 ---");
var readResult = client.ReadInt16("40001");
if (readResult.IsSuccess)
    Console.WriteLine($"40001 = {readResult.Content}");
else
    Console.WriteLine($"读取失败: {readResult.Message}");

// 4. 写入并回读
Console.WriteLine("\n--- 写入并回读 ---");
var writeResult = client.Write("40001", (short)12345);
if (writeResult.IsSuccess)
{
    Console.WriteLine("写入 12345 成功");
    var verifyResult = client.ReadInt16("40001");
    if (verifyResult.IsSuccess)
        Console.WriteLine($"回读 40001 = {verifyResult.Content}");
}

// 5. 读写多种数据类型
Console.WriteLine("\n--- 多数据类型读写 ---");
client.Write("40001", (short)100);           // Int16
client.Write("40002", (ushort)200);          // UInt16
client.Write("40003", 12345678);             // Int32 (占用 40003-40004)
client.Write("40005", (float)3.14f);         // Float (占用 40005-40006)
client.Write("40007", 3.14159265358979);     // Double (占用 40007-40010)

Console.WriteLine($"Int16  40001 = {client.ReadInt16("40001").Content}");
Console.WriteLine($"UInt16 40002 = {client.ReadUInt16("40002").Content}");
Console.WriteLine($"Int32  40003 = {client.ReadInt32("40003").Content}");
Console.WriteLine($"Float  40005 = {client.ReadFloat("40005").Content:F4}");
Console.WriteLine($"Double 40007 = {client.ReadDouble("40007").Content:F8}");

// 6. 读写线圈 (位操作)
Console.WriteLine("\n--- 线圈读写 ---");
client.Write("00001", true);
client.Write("00002", false);
Console.WriteLine($"线圈 00001 = {client.ReadBool("00001").Content}");
Console.WriteLine($"线圈 00002 = {client.ReadBool("00002").Content}");

// 7. 批量读取
Console.WriteLine("\n--- 批量读取 ---");
var batchResult = client.BatchRead(new[] { "40001", "40002", "40003" });
if (batchResult.IsSuccess)
    foreach (var kv in batchResult.Content)
        Console.WriteLine($"  {kv.Key} = {kv.Value}");

// 8. 字符串读写
Console.WriteLine("\n--- 字符串读写 ---");
client.Write("40001", "Hello");
var strResult = client.ReadString("40001", 10);
if (strResult.IsSuccess)
    Console.WriteLine($"字符串 = '{strResult.Content}'");

// 9. 断开
client.Disconnect();
Console.WriteLine("\n已断开连接");
