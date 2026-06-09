using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

/// <summary>
/// AutoReconnectGuard / HeartbeatGuard 集成测试 — Modbus TCP 客户端 + 重连/心跳守护。
/// </summary>
public sealed class ModbusReconnectGuardTests
{
    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void AutoReconnectGuard_ReconnectsAfterServerRestart()
    {
        int port = GetAvailablePort();

        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1, timeout: 2000);
        Assert.True(client.Connect().IsSuccess);

        Assert.True(client.Write("40001", (short)42).IsSuccess);
        Assert.Equal((short)42, client.ReadInt16("40001").Content);

        using var guard = new AutoReconnectGuard(client)
        {
            MaxRetries = 10,
            BaseDelayMs = 300,
            MaxDelayMs = 3000,
            BackoffMultiplier = 2.0
        };

        bool reconnected = false;
        guard.OnReconnected += () => Volatile.Write(ref reconnected, true);
        guard.Start();

        // Kill server, restart on same port
        server.Dispose();
        Thread.Sleep(500);

        using var server2 = new ModbusTcpServer(port);
        server2.Start();

        // Trigger disconnect detection by read attempt
        client.ReadInt16("40001");
        Thread.Sleep(6000);

        var read2 = client.ReadInt16("40001");
        Assert.True(read2.IsSuccess, $"Read after reconnect should succeed: {read2.Message}");
    }

    [Fact]
    public void AutoReconnectGuard_Events_SubscribeWithoutError()
    {
        int port = GetAvailablePort();

        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1, timeout: 1000);
        client.Connect();

        using var guard = new AutoReconnectGuard(client)
        {
            MaxRetries = 2,
            BaseDelayMs = 100,
            MaxDelayMs = 500
        };

        int reconnecting = 0;
        bool reconnected = false;
        bool failed = false;

        guard.OnReconnecting += (a) => Interlocked.Exchange(ref reconnecting, a);
        guard.OnReconnected += () => Volatile.Write(ref reconnected, true);
        guard.OnReconnectFailed += (err) => Volatile.Write(ref failed, true);

        guard.Start();
        guard.Stop();

        // Verify no exceptions during subscribe/unsubscribe
        Assert.False(Volatile.Read(ref reconnected));
        Assert.False(Volatile.Read(ref failed));
    }

    [Fact]
    public void HeartbeatGuard_DetectsDeadConnection()
    {
        int port = GetAvailablePort();

        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1, timeout: 1000);
        Assert.True(client.Connect().IsSuccess);

        int failCount = 0;

        using var heartbeat = new HeartbeatGuard(
            client,
            () => Task.Run<OperateResult>(() =>
            {
                var r = client.ReadInt16("40001");
                return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message);
            }),
            NullLogger.Instance)
        {
            IntervalMs = 500,
            MaxConsecutiveFailures = 2,
            TimeoutMs = 500
        };

        heartbeat.OnHeartbeatFailed += (count, err) =>
            Interlocked.Exchange(ref failCount, count);

        heartbeat.Start();
        Thread.Sleep(800);

        server.Dispose();
        Thread.Sleep(4000);

        Assert.True(Volatile.Read(ref failCount) >= 2, $"Expected at least 2 failures, got {failCount}");
    }

    [Fact]
    public void HeartbeatGuard_KeepsRunningOnSuccess()
    {
        int port = GetAvailablePort();

        using var server = new ModbusTcpServer(port);
        server.Start();

        using var client = new ModbusTcpClient("127.0.0.1", port, station: 1, timeout: 1000);
        client.Connect();

        int okCount = 0;
        using var heartbeat = new HeartbeatGuard(
            client,
            () => Task.Run<OperateResult>(() =>
            {
                var r = client.ReadInt16("40001");
                return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message);
            }),
            NullLogger.Instance)
        {
            IntervalMs = 300,
            MaxConsecutiveFailures = 3
        };

        heartbeat.OnHeartbeatOk += () => Interlocked.Increment(ref okCount);
        heartbeat.Start();
        Thread.Sleep(2000);

        Assert.True(Volatile.Read(ref okCount) >= 3, $"Expected at least 3 heartbeats, got {okCount}");
        Assert.True(heartbeat.IsRunning);
    }
}
