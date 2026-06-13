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

        await Task.Delay(3000);
        Assert.True(client.IsConnected);

        newServer.Dispose();
    }

    [Fact]
    public void Heartbeat_KeepsConnectionAlive()
    {
        var port = GetFreePort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port);
        client.SetPersistentConnection();
        client.HeartbeatEnabled = true;
        client.HeartbeatInterval = 1000;
        client.Connect();

        Thread.Sleep(5000);
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
