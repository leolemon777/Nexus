using System;
using Nexus;
using Nexus.Siemens;

namespace SiemensS7Example;

/// <summary>
/// Siemens S7 TCP 读写示例。
/// 用法: dotnet run -- [ip] [rack] [slot]
/// 默认: 192.168.1.100 rack=0 slot=1
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        string ip = args.Length > 0 ? args[0] : "192.168.1.100";
        int rack = args.Length > 1 ? int.Parse(args[1]) : 0;
        int slot = args.Length > 2 ? int.Parse(args[2]) : 1;

        Console.WriteLine($"=== Siemens S7 示例 — 连接 {ip}:102 ===");

        using var client = new SiemensS7Client(SiemensPLCS.S7_1200, ip);

        // 1. 连接
        var connect = client.Connect();
        if (!connect.IsSuccess)
        {
            Console.WriteLine($"连接失败: {connect.Message}");
            return;
        }
        Console.WriteLine("✓ 已连接");

        // 2. 读取 DB1.DBW0 (Int16)
        var read16 = client.ReadInt16("DB1.DBW0");
        if (read16.IsSuccess)
            Console.WriteLine($"读取 DB1.DBW0 (Int16): {read16.Content}");
        else
            Console.WriteLine($"读取失败: {read16.Message}");

        // 3. 写入 DB1.DBW2 (Int16)
        var write16 = client.Write("DB1.DBW2", (short)100);
        Console.WriteLine(write16.IsSuccess ? "✓ 写入 DB1.DBW2 = 100" : $"写入失败: {write16.Message}");

        // 4. 读取 DB1.DBD4 (Float)
        var readFloat = client.ReadFloat("DB1.DBD4");
        if (readFloat.IsSuccess)
            Console.WriteLine($"读取 DB1.DBD4 (Float): {readFloat.Content:F2}");

        // 5. 写入 Float
        var writeFloat = client.Write("DB1.DBD8", 25.5f);
        Console.WriteLine(writeFloat.IsSuccess ? "✓ 写入 DB1.DBD8 = 25.5" : $"写入失败: {writeFloat.Message}");

        // 6. 读取 PLC 时钟
        var clock = client.ReadPlcClock();
        if (clock.IsSuccess)
            Console.WriteLine($"PLC 时钟: {clock.Content:yyyy-MM-dd HH:mm:ss}");

        // 7. 断开
        client.Disconnect();
        Console.WriteLine("✓ 已断开");
    }
}
