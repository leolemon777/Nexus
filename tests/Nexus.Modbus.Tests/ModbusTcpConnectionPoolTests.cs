using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

public class ModbusTcpConnectionPoolTests
{
    [Fact]
    public void ReadWrite_ReusesPooledPersistentConnection()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusTcpServer(port);
        server.Start();

        using var pool = new ModbusTcpConnectionPool("127.0.0.1", port, station: 1, maxPoolSize: 1);

        var write = pool.Write("40001", (ushort)0x1234);
        var read = pool.ReadUInt16("40001");

        Assert.True(write.IsSuccess, write.Message);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((ushort)0x1234, read.Content);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(1, pool.IdleCount);
    }

    [Fact]
    public async Task Execute_RespectsMaxPoolSize()
    {
        int port = GetFreeTcpPort();
        using var server = new ModbusTcpServer(port);
        server.SetHoldingRegister(0, 0x5678);
        server.Start();

        using var pool = new ModbusTcpConnectionPool("127.0.0.1", port, station: 1, maxPoolSize: 1);
        using var releaseFirst = new ManualResetEventSlim(false);

        var first = Task.Run(() => pool.Execute(client =>
        {
            releaseFirst.Wait();
            return client.ReadUInt16("40001");
        }));

        await Task.Delay(100);
        var second = Task.Run(() => pool.ReadUInt16("40001"));

        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(100)));

        releaseFirst.Set();
        var firstResult = await first;
        var secondResult = await second;

        Assert.True(firstResult.IsSuccess, firstResult.Message);
        Assert.True(secondResult.IsSuccess, secondResult.Message);
        Assert.Equal((ushort)0x5678, secondResult.Content);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
