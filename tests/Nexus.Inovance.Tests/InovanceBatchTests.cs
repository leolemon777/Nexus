using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using Nexus.Inovance;

namespace Nexus.Inovance.Tests
{
    public class InovanceBatchTests : IDisposable
    {
        private static int _portCounter = 26000;
        private readonly int _port;
        private InovanceEasyVirtualServer? _server;

        public InovanceBatchTests()
        {
            _port = Interlocked.Increment(ref _portCounter);
        }

        public void Dispose()
        {
            _server?.Stop();
            _server?.Dispose();
        }

        private (InovanceEasyVirtualServer server, InovanceEasyClient client) StartServerAndConnect()
        {
            _server = new InovanceEasyVirtualServer(_port);
            _server.Start();
            var client = new InovanceEasyClient("127.0.0.1", _port);
            client.SetPersistentConnection();
            var connect = client.Connect();
            Assert.True(connect.IsSuccess, connect.Message);
            return (_server, client);
        }

        [Fact]
        public void Client_Implements_IBatchReadWrite()
        {
            var client = new InovanceEasyClient("127.0.0.1", 1);
            Assert.IsAssignableFrom<IBatchReadWrite>(client);
        }

        [Fact]
        public void BatchRead_ReturnsMultipleValues()
        {
            var (server, client) = StartServerAndConnect();
            server.SetDWord(100, 42);
            server.SetDWord(101, 99);

            var result = client.BatchRead(new[] { "D100", "D101" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(2, result.Content.Count);
            Assert.Equal((short)42, result.Content["D100"]);
            Assert.Equal((short)99, result.Content["D101"]);
        }

        [Fact]
        public void BatchRead_EmptyList_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();
            var result = client.BatchRead(new string[0]);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void RandomRead_ReturnsRawBytes()
        {
            var (server, client) = StartServerAndConnect();
            server.SetDWord(100, 0x1234);

            var result = client.RandomRead(new[] { "D100" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Single(result.Content);
            Assert.Equal(2, result.Content["D100"].Length);
        }

        [Fact]
        public void RandomRead_EmptyList_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();
            var result = client.RandomRead(new string[0]);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void BatchWrite_SendsData()
        {
            var (server, client) = StartServerAndConnect();

            var result = client.BatchWrite(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, object>("D100", (short)42),
                new System.Collections.Generic.KeyValuePair<string, object>("D101", (short)99),
            });
            Assert.True(result.IsSuccess, result.Message);
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
                new System.Collections.Generic.KeyValuePair<string, object>("D100", new object()),
            });
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void BatchWrite_MultipleTypes()
        {
            var (server, client) = StartServerAndConnect();

            var result = client.BatchWrite(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, object>("D100", (short)1),
                new System.Collections.Generic.KeyValuePair<string, object>("D101", (ushort)2),
                new System.Collections.Generic.KeyValuePair<string, object>("D102", true),
                new System.Collections.Generic.KeyValuePair<string, object>("D103", 3.14f),
            });
            Assert.True(result.IsSuccess, result.Message);
        }

        [Fact]
        public void ConnectionPool_ReadWrite_ReusesPersistentConnection()
        {
            _server = new InovanceEasyVirtualServer(_port);
            _server.Start();

            using var pool = new InovanceEasyConnectionPool("127.0.0.1", _port, maxPoolSize: 1);

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
            _server = new InovanceEasyVirtualServer(_port);
            _server.SetDWord(10, 0x1234);
            _server.Start();

            using var pool = new InovanceEasyConnectionPool("127.0.0.1", _port, maxPoolSize: 1);
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
        public void ConnectionPool_BatchReadWrite()
        {
            _server = new InovanceEasyVirtualServer(_port);
            _server.Start();

            using var pool = new InovanceEasyConnectionPool("127.0.0.1", _port, maxPoolSize: 1);
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
    }
}
