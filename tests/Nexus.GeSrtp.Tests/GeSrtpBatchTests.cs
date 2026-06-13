using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nexus.GeSrtp;

namespace Nexus.GeSrtp.Tests
{
    public class GeSrtpBatchTests : IDisposable
    {
        private static int _portCounter = 29000;
        private readonly int _port;
        private GeSrtpVirtualServer? _server;

        public GeSrtpBatchTests()
        {
            _port = Interlocked.Increment(ref _portCounter);
        }

        public void Dispose()
        {
            _server?.Stop();
            _server?.Dispose();
        }

        private (GeSrtpVirtualServer server, GeSrtpClient client) StartServerAndConnect()
        {
            _server = new GeSrtpVirtualServer(_port);
            _server.Start();
            var client = new GeSrtpClient("127.0.0.1", _port);
            var connect = client.Connect();
            Assert.True(connect.IsSuccess, connect.Message);
            return (_server, client);
        }

        [Fact]
        public void Client_Implements_IBatchReadWrite()
        {
            var client = new GeSrtpClient("127.0.0.1", 1);
            Assert.IsAssignableFrom<IBatchReadWrite>(client);
        }

        [Fact]
        public void BatchRead_EmptyList_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();
            var result = client.BatchRead(new string[0]);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void BatchRead_SingleAddress()
        {
            var (server, client) = StartServerAndConnect();
            server.SetRWord(100, 1234);

            var result = client.BatchRead(new[] { "R100" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Single(result.Content);
        }

        [Fact]
        public void RandomRead_EmptyList_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();
            var result = client.RandomRead(new string[0]);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void RandomRead_SingleAddress()
        {
            var (server, client) = StartServerAndConnect();
            server.SetRWord(200, 5678);

            var result = client.RandomRead(new[] { "R200" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Single(result.Content);
        }

        [Fact]
        public void BatchWrite_EmptyList_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();
            var result = client.BatchWrite(new System.Collections.Generic.KeyValuePair<string, object>[0]);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void BatchWrite_UnsupportedType_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();
            var result = client.BatchWrite(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, object>("R100", new object()),
            });
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void BatchWrite_SupportedTypes()
        {
            var (server, client) = StartServerAndConnect();
            var result = client.BatchWrite(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, object>("R100", (short)42),
                new System.Collections.Generic.KeyValuePair<string, object>("R101", true),
            });
            Assert.True(result.IsSuccess, result.Message);
        }

        [Fact]
        public async Task BatchReadAsync_SameResultAsSync()
        {
            var (server, client) = StartServerAndConnect();
            server.SetRWord(300, 1111);
            server.SetRWord(301, 2222);

            var syncResult = client.BatchRead(new[] { "R300", "R301" });
            var asyncResult = await client.BatchReadAsync(new[] { "R300", "R301" });
            Assert.Equal(syncResult.IsSuccess, asyncResult.IsSuccess);
        }

        [Fact]
        public void ConnectionPool_ReadWrite_ReusesPersistentConnection()
        {
            int port = GetFreeTcpPort();
            using var server = new GeSrtpVirtualServer(port);
            server.Start();

            using var pool = new GeSrtpConnectionPool("127.0.0.1", port, maxPoolSize: 1);
            var write = pool.Write("R100", (short)1234);
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadInt16("R100");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((short)1234, read.Content);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            int port = GetFreeTcpPort();
            using var server = new GeSrtpVirtualServer(port);
            server.SetRWord(10, 0x1234);
            server.Start();

            using var pool = new GeSrtpConnectionPool("127.0.0.1", port, maxPoolSize: 1);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, message) =>
            {
                if (!string.IsNullOrWhiteSpace(message)) Interlocked.Increment(ref sent);
            };
            pool.OnMessageReceived += (_, message) =>
            {
                if (!string.IsNullOrWhiteSpace(message)) Interlocked.Increment(ref received);
            };

            var read = pool.ReadUInt16("R10");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((ushort)0x1234, read.Content);
            Assert.True(sent > 0);
            Assert.True(received > 0);
        }

        [Fact]
        public void ConnectionPool_BatchReadWrite()
        {
            int port = GetFreeTcpPort();
            using var server = new GeSrtpVirtualServer(port);
            server.Start();

            using var pool = new GeSrtpConnectionPool("127.0.0.1", port, maxPoolSize: 1);
            var write = pool.BatchWrite(new[]
            {
                new KeyValuePair<string, object>("R20", (short)111),
                new KeyValuePair<string, object>("R21", (short)222)
            });
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.BatchRead(new[] { "R20", "R21" });
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((short)111, read.Content["R20"]);
            Assert.Equal((short)222, read.Content["R21"]);
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
