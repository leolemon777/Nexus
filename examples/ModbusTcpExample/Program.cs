using System;
using Nexus;
using Nexus.Modbus;

namespace ModbusTcpExample;

/// <summary>
/// Modbus TCP 读写示例。
/// 用法: dotnet run -- [ip] [port]
/// 默认: 127.0.0.1:502
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        string ip = args.Length > 0 ? args[0] : "127.0.0.1";
        int port = args.Length > 1 ? int.Parse(args[1]) : 502;

        Console.WriteLine($"=== Modbus TCP 示例 — 连接 {ip}:{port} ===");

        using var client = new ModbusTcpClient(ip, port, station: 1);

        // 1. 连接
        var connect = client.Connect();
        if (!connect.IsSuccess)
        {
            Console.WriteLine($"连接失败: {connect.Message}");
            return;
        }
        Console.WriteLine("✓ 已连接");

        // 2. 读取 Int16 (Holding Register D100)
        var read16 = client.ReadInt16("100");
        if (read16.IsSuccess)
            Console.WriteLine($"读取 D100 (Int16): {read16.Content}");
        else
            Console.WriteLine($"读取失败: {read16.Message}");

        // 3. 写入 Int16
        var write16 = client.Write("200", (short)42);
        Console.WriteLine(write16.IsSuccess ? "✓ 写入 D200 = 42" : $"写入失败: {write16.Message}");

        // 4. 读取 Float
        var readFloat = client.ReadFloat("300");
        if (readFloat.IsSuccess)
            Console.WriteLine($"读取 D300 (Float): {readFloat.Content:F2}");

        // 5. 写入 Float
        var writeFloat = client.Write("300", 3.14f);
        Console.WriteLine(writeFloat.IsSuccess ? "✓ 写入 D300 = 3.14" : $"写入失败: {writeFloat.Message}");

        // 6. 批量读取
        var batch = client.ReadRegistersBatch(100, 5);
        if (batch.IsSuccess)
            Console.WriteLine($"批量读取 D100-D104: {BitConverter.ToString(batch.Content)}");

        // 7. 断开
        client.Disconnect();
        Console.WriteLine("✓ 已断开");
    }
}
