using System.Net;
using System.Net.Sockets;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Core.Tests;

[Trait("Category", "Stability")]
public class StabilityTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 轮询等待条件成立，替代固定 <c>Thread.Sleep</c>/<c>Task.Delay</c>。
    /// 用于异步重连/心跳等时序敏感断言：在 deadline 内每 100ms 检查一次条件，
    /// 条件成立立即返回 true；超时返回 false（由调用方断言）。
    /// 这把"等事件发生"从"等固定时长"解耦，消除 CI 负载抖动导致的 flaky。
    /// </summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan deadline)
    {
        var deadlineUtc = DateTime.UtcNow + deadline;
        while (DateTime.UtcNow < deadlineUtc)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    [Fact]
    public async Task ConcurrentReads_NoDeadlock_NoExceptions()
    {
        var port = GetFreePort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            using var client = new ModbusTcpClient("127.0.0.1", port);
            client.SetPersistentConnection();
            client.Connect();
            for (int i = 0; i < 100; i++)
            {
                var result = client.ReadInt16("0");
                Assert.True(result.IsSuccess, result.Message);
            }
        }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void ConnectDisconnect_NoResourceLeak()
    {
        var port = GetFreePort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        for (int i = 0; i < 50; i++)
        {
            using var client = new ModbusTcpClient("127.0.0.1", port);
            client.Connect();
            Assert.True(client.IsConnected);
            client.Disconnect();
        }
    }

    [Fact]
    public async Task AsyncReadWrite_NoDeadlock()
    {
        var port = GetFreePort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port);
        client.SetPersistentConnection();
        client.Connect();

        var tasks = Enumerable.Range(0, 50).Select(async _ =>
        {
            var result = await client.ReadInt16Async("0");
            Assert.True(result.IsSuccess, result.Message);
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task AutoReconnect_ReconnectsAfterServerRestart()
    {
        var port = GetFreePort();
        var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port);
        client.SetPersistentConnection();
        client.AutoReconnect = true;
        client.ReconnectInterval = 500;
        client.MaxReconnectAttempts = 10;
        client.Connect();
        Assert.True(client.IsConnected);

        server.Stop();
        server.Dispose();

        client.Disconnect();
        Assert.False(client.IsConnected);

        await Task.Delay(600);

        var newServer = new ModbusTcpServer(port);
        newServer.Start();

        // Poll for reconnection instead of a fixed delay: AutoReconnect fires on a timer
        // (ReconnectInterval) whose scheduling jitters under CI load. A fixed Task.Delay(3000)
        // could fire the assertion before the reconnect attempt landed — a known flake source.
        // Allow up to ~10s (well above 10 attempts × 500ms) for the reconnection to register.
        bool reconnected = await WaitForAsync(() => client.IsConnected, TimeSpan.FromSeconds(10));
        Assert.True(reconnected, $"Client did not reconnect within 10s after server restart (IsConnected={client.IsConnected})");

        newServer.Dispose();
    }

    [Fact]
    public async Task Heartbeat_KeepsConnectionAlive()
    {
        var port = GetFreePort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port);
        client.SetPersistentConnection();
        client.HeartbeatEnabled = true;
        client.HeartbeatInterval = 1000;
        client.Connect();

        // Heartbeat fires every HeartbeatInterval on a timer; under CI load the timer can
        // drift. Instead of a fixed Thread.Sleep(5000) + assert, poll that the connection
        // stays alive for the full window (sampling every 500ms over ~6s). The connection
        // is expected to stay connected throughout, so each sample must be true.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            Assert.True(client.IsConnected, "Connection dropped during heartbeat window");
            await Task.Delay(500);
        }
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void Dispose_NoMemoryLeak()
    {
        var port = GetFreePort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(true);

        for (int i = 0; i < 1000; i++)
        {
            var client = new ModbusTcpClient("127.0.0.1", port);
            client.Connect();
            client.ReadInt16("0");
            client.Disconnect();
            client.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GC.GetTotalMemory(true);

        Assert.True(after - before < 10 * 1024 * 1024, $"Memory grew {(after - before) / 1024}KB");
    }

    [Fact]
    public async Task MultiClientConcurrent_AllSucceed()
    {
        var port = GetFreePort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            using var client = new ModbusTcpClient("127.0.0.1", port);
            client.Connect();
            for (int i = 0; i < 10; i++)
            {
                var result = client.ReadInt16("0");
                Assert.True(result.IsSuccess, result.Message);
            }
        }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void LargeDataTransfer_Read100Registers_DataIntegrity()
    {
        var port = GetFreePort();
        using var server = new ModbusTcpServer(port);
        for (ushort i = 0; i < 100; i++)
            server.SetHoldingRegister(i, i);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port);
        client.Connect();

        var result = client.ReadBytes("40001", 200);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(200, result.Content.Length);

        for (int i = 0; i < 100; i++)
        {
            ushort expected = (ushort)i;
            ushort actual = (ushort)((result.Content[i * 2] << 8) | result.Content[i * 2 + 1]);
            Assert.Equal(expected, actual);
        }
    }
}
