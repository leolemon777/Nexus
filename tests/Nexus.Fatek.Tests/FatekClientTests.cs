using Xunit;
using Nexus.Fatek;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Fatek.Tests
{
    public class FatekClientTests : IDisposable
    {
        private readonly FatekVirtualServer _server;
        private const int TestPort = 15001;

        public FatekClientTests()
        {
            _server = new FatekVirtualServer(TestPort);
        }

        public void Dispose()
        {
            _server?.Dispose();
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new FatekClient("127.0.0.1", TestPort);
            Assert.Equal("127.0.0.1", client.IpAddress);
            Assert.Equal(TestPort, client.Port);
            Assert.Equal((byte)1, client.Station);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new FatekClient("127.0.0.1", TestPort);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new FatekClient("127.0.0.1", TestPort);
            client.Dispose();
        }

        [Fact]
        public void Connect_ToVirtualServer_Succeeds()
        {
            _server.Start();
            using var client = new FatekClient("127.0.0.1", TestPort);
            var result = client.Connect();
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void Connect_InvalidPort_Fails()
        {
            using var client = new FatekClient("127.0.0.1", 19999, station: 1, timeout: 1000);
            var result = client.Connect();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void ReadInt16_FromVirtualServer_ReturnsValue()
        {
            _server.Start();
            _server.SetDWord(100, 12345);
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadInt16("D100");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)12345, result.Content);
        }

        [Fact]
        public void WriteInt16_ToVirtualServer_Succeeds()
        {
            _server.Start();
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var writeResult = client.Write("D200", (short)12345);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("D200");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)12345, readResult.Content);
        }

        [Fact]
        public void ReadUInt16_FromVirtualServer_ReturnsValue()
        {
            _server.Start();
            _server.SetDWord(50, 60000);
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadUInt16("D50");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)60000, result.Content);
        }

        [Fact]
        public void WriteUInt16_ToVirtualServer_Succeeds()
        {
            _server.Start();
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var writeResult = client.Write("D300", (ushort)54321);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadUInt16("D300");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((ushort)54321, readResult.Content);
        }

        [Fact]
        public void ReadBools_FromVirtualServer_ReturnsValues()
        {
            _server.Start();
            _server.SetBit('Y', 0, true);
            _server.SetBit('Y', 1, false);
            _server.SetBit('Y', 2, true);
            _server.SetBit('Y', 3, false);

            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadBools("Y0", 4);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(4, result.Content.Length);
            Assert.True(result.Content[0]);
            Assert.False(result.Content[1]);
            Assert.True(result.Content[2]);
            Assert.False(result.Content[3]);
        }

        [Fact]
        public void ReadPlcStatus_FromVirtualServer_ReturnsStopped()
        {
            _server.Start();
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadPlcStatus();
            Assert.True(result.IsSuccess, result.Message);
            // 默认状态为 STOP
            Assert.False(result.Content);
        }

        [Fact]
        public void BatchOperations_EmptyInput_ReturnsError()
        {
            using var client = new FatekClient("127.0.0.1", TestPort);

            Assert.False(client.BatchRead(new string[0]).IsSuccess);
            Assert.False(client.RandomRead(new string[0]).IsSuccess);
            Assert.False(client.BatchWrite(System.Array.Empty<System.Collections.Generic.KeyValuePair<string, object>>()).IsSuccess);
        }

        [Fact]
        public void ConnectionPool_ReadWrite_ReusesPersistentConnection()
        {
            int port = GetFreeTcpPort();
            using var server = new FatekVirtualServer(port);
            server.Start();

            using var pool = new FatekConnectionPool("127.0.0.1", port, maxPoolSize: 1);

            var writeResult = pool.Write("D100", (short)1234);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = pool.ReadInt16("D100");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)1234, readResult.Content);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            int port = GetFreeTcpPort();
            using var server = new FatekVirtualServer(port);
            server.SetDWord(10, 0x1234);
            server.Start();

            using var pool = new FatekConnectionPool("127.0.0.1", port, maxPoolSize: 1);
            int sentCount = 0;
            int receivedCount = 0;
            pool.OnMessageSent += (_, _) => Interlocked.Increment(ref sentCount);
            pool.OnMessageReceived += (_, _) => Interlocked.Increment(ref receivedCount);

            var result = pool.ReadUInt16("D10");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0x1234, result.Content);
            Assert.True(sentCount > 0);
            Assert.True(receivedCount > 0);
        }

        [Fact]
        public void ConnectionPool_RunStop_StatusChanges()
        {
            int port = GetFreeTcpPort();
            using var server = new FatekVirtualServer(port);
            server.Start();

            using var pool = new FatekConnectionPool("127.0.0.1", port, maxPoolSize: 1);

            var initialStatus = pool.ReadPlcStatus();
            Assert.True(initialStatus.IsSuccess, initialStatus.Message);
            Assert.False(initialStatus.Content);

            var runResult = pool.Run();
            Assert.True(runResult.IsSuccess, runResult.Message);

            var runningStatus = pool.ReadPlcStatus();
            Assert.True(runningStatus.IsSuccess, runningStatus.Message);
            Assert.True(runningStatus.Content);

            var stopResult = pool.Stop();
            Assert.True(stopResult.IsSuccess, stopResult.Message);

            var stoppedStatus = pool.ReadPlcStatus();
            Assert.True(stoppedStatus.IsSuccess, stoppedStatus.Message);
            Assert.False(stoppedStatus.Content);
        }

        [Fact]
        public void ConnectionPool_BatchReadWrite()
        {
            int port = GetFreeTcpPort();
            using var server = new FatekVirtualServer(port);
            server.Start();

            using var pool = new FatekConnectionPool("127.0.0.1", port, maxPoolSize: 1);
            var items = new[]
            {
                new KeyValuePair<string, object>("D20", (short)111),
                new KeyValuePair<string, object>("D21", (ushort)222)
            };

            var writeResult = pool.BatchWrite(items);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = pool.BatchRead(new[] { "D20", "D21" });

            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)111, readResult.Content["D20"]);
            Assert.Equal((short)222, readResult.Content["D21"]);
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
