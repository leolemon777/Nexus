using System;
using Nexus;
using Nexus.Mitsubishi;

namespace MitsubishiMc3EExample;

/// <summary>
/// 三菱 MC3E Binary 读写示例。
/// 用法: dotnet run -- [ip] [port]
/// 默认: 192.168.1.10:5007
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        string ip = args.Length > 0 ? args[0] : "192.168.1.10";
        int port = args.Length > 1 ? int.Parse(args[1]) : 5007;

        Console.WriteLine($"=== 三菱 MC3E Binary 示例 — 连接 {ip}:{port} ===");

        using var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, ip, port);

        // 1. 连接
        var connect = client.Connect();
        if (!connect.IsSuccess)
        {
            Console.WriteLine($"连接失败: {connect.Message}");
            return;
        }
        Console.WriteLine("✓ 已连接");

        // 2. 读取 D100 (Int16)
        var read16 = client.ReadInt16("D100");
        if (read16.IsSuccess)
            Console.WriteLine($"读取 D100 (Int16): {read16.Content}");
        else
            Console.WriteLine($"读取失败: {read16.Message}");

        // 3. 写入 D200 (Int16)
        var write16 = client.Write("D200", (short)1234);
        Console.WriteLine(write16.IsSuccess ? "✓ 写入 D200 = 1234" : $"写入失败: {write16.Message}");

        // 4. 读取 D300 (Float)
        var readFloat = client.ReadFloat("D300");
        if (readFloat.IsSuccess)
            Console.WriteLine($"读取 D300 (Float): {readFloat.Content:F2}");

        // 5. 写入 Float
        var writeFloat = client.Write("D300", 6.28f);
        Console.WriteLine(writeFloat.IsSuccess ? "✓ 写入 D300 = 6.28" : $"写入失败: {writeFloat.Message}");

        // 6. 批量读取 D100-D109
        var batch = client.ReadBytes("D100", 10);
        if (batch.IsSuccess)
            Console.WriteLine($"批量读取 D100-D109: {BitConverter.ToString(batch.Content)}");

        // 7. 断开
        client.Disconnect();
        Console.WriteLine("✓ 已断开");
    }
}
