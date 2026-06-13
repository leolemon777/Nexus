using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nexus.Schneider;

namespace Nexus.Schneider.Tests
{
    /// <summary>
    /// SchneiderModiconClient IBatchReadWrite 接口测试 — 通过虚拟服务器验证。
    /// </summary>
    public class SchneiderBatchTests : IDisposable
    {
        private static int _portCounter = 15200;
        private readonly int _port;
        private SchneiderVirtualServer? _server;

        public SchneiderBatchTests()
        {
            _port = Interlocked.Increment(ref _portCounter);
        }

        public void Dispose()
        {
            _server?.Stop();
            _server?.Dispose();
        }

        private (SchneiderVirtualServer server, SchneiderModiconClient client) StartServerAndConnect()
        {
            _server = new SchneiderVirtualServer(_port);
            _server.Start();
            var client = new SchneiderModiconClient("127.0.0.1", _port);
            client.SetPersistentConnection();
            var connect = client.Connect();
            Assert.True(connect.IsSuccess, connect.Message);
            return (_server, client);
        }

        [Fact]
        public void Client_Implements_IBatchReadWrite()
        {
            var client = new SchneiderModiconClient("127.0.0.1", 1);
            Assert.IsAssignableFrom<IBatchReadWrite>(client);
        }

        [Fact]
        public void BatchRead_WordAddresses_ReturnsValues()
        {
            var (server, client) = StartServerAndConnect();
            server.SetHoldingRegister(100, 1234);
            server.SetHoldingRegister(101, 5678);

            var result = client.BatchRead(new[] { "%MW100", "%MW101" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(2, result.Content.Count);
        }

        [Fact]
        public void BatchRead_CoilAddresses_ReturnsBools()
        {
            var (server, client) = StartServerAndConnect();
            server.SetCoil(50, true);
            server.SetCoil(51, false);

            var result = client.BatchRead(new[] { "%M50", "%M51" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(2, result.Content.Count);
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
            server.SetHoldingRegister(100, 0xABCD);

            var result = client.RandomRead(new[] { "%MW100" });
            Assert.True(result.IsSuccess, result.Message);
            Assert.Single(result.Content);
            Assert.Equal(2, result.Content["%MW100"].Length);
        }

        [Fact]
        public void BatchWrite_MixedTypes()
        {
            var (server, client) = StartServerAndConnect();

            var items = new System.Collections.Generic.KeyValuePair<string, object>[]
            {
                new("%MW100", (short)42),
                new("%MW101", (ushort)99),
                new("%M50", true),
            };

            var result = client.BatchWrite(items);
            Assert.True(result.IsSuccess, result.Message);
        }

        [Fact]
        public void WriteUInt64_WritesFourHoldingRegisters()
        {
            var (server, client) = StartServerAndConnect();

            var result = client.Write("%MW100", 0x1122334455667788UL);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x1122, server.GetHoldingRegister(100));
            Assert.Equal(0x3344, server.GetHoldingRegister(101));
            Assert.Equal(0x5566, server.GetHoldingRegister(102));
            Assert.Equal(0x7788, server.GetHoldingRegister(103));
        }

        [Fact]
        public void WriteDouble_WritesFourHoldingRegisters()
        {
            var (server, client) = StartServerAndConnect();

            var result = client.Write("%MW120", 1.5d);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x3FF8, server.GetHoldingRegister(120));
            Assert.Equal(0x0000, server.GetHoldingRegister(121));
            Assert.Equal(0x0000, server.GetHoldingRegister(122));
            Assert.Equal(0x0000, server.GetHoldingRegister(123));
        }

        [Fact]
        public void ReadUInt64_ReadsFourHoldingRegisters()
        {
            var (server, client) = StartServerAndConnect();
            server.SetHoldingRegister(140, 0x1122);
            server.SetHoldingRegister(141, 0x3344);
            server.SetHoldingRegister(142, 0x5566);
            server.SetHoldingRegister(143, 0x7788);

            var result = client.ReadUInt64("%MW140");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x1122334455667788UL, result.Content);
        }

        [Fact]
        public void BatchWrite_UnsupportedType_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();

            var items = new System.Collections.Generic.KeyValuePair<string, object>[]
            {
                new("%MW100", new object()), // 不支持的类型
            };

            var result = client.BatchWrite(items);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public async Task BatchReadAsync_SameResultAsSync()
        {
            var (server, client) = StartServerAndConnect();
            server.SetHoldingRegister(200, 1111);
            server.SetHoldingRegister(201, 2222);

            var syncResult = client.BatchRead(new[] { "%MW200", "%MW201" });
            var asyncResult = await client.BatchReadAsync(new[] { "%MW200", "%MW201" });

            Assert.Equal(syncResult.IsSuccess, asyncResult.IsSuccess);
        }

        [Fact]
        public void ConnectionPool_ReadWrite_ReusesPersistentConnection()
        {
            using var server = new SchneiderVirtualServer(0);
            server.Start();

            using var pool = new SchneiderConnectionPool("127.0.0.1", server.Port, maxPoolSize: 1);

            var write = pool.Write("%MW100", (short)1234);
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadInt16("%MW100");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((short)1234, read.Content);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            using var server = new SchneiderVirtualServer(0);
            server.SetHoldingRegister(110, 0x1234);
            server.Start();

            using var pool = new SchneiderConnectionPool("127.0.0.1", server.Port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, _) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, _) => Interlocked.Increment(ref received);

            var read = pool.ReadUInt16("%MW110");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((ushort)0x1234, read.Content);
            Assert.True(sent > 0);
            Assert.True(received > 0);
        }

        [Fact]
        public void ConnectionPool_BatchReadWrite()
        {
            using var server = new SchneiderVirtualServer(0);
            server.Start();

            using var pool = new SchneiderConnectionPool("127.0.0.1", server.Port);
            var items = new[]
            {
                new KeyValuePair<string, object>("%MW120", (short)111),
                new KeyValuePair<string, object>("%MW121", (short)222),
                new KeyValuePair<string, object>("%M60", true),
            };

            var write = pool.BatchWrite(items);
            Assert.True(write.IsSuccess, write.Message);

            var wordRead = pool.BatchRead(new[] { "%MW120", "%MW121" });
            Assert.True(wordRead.IsSuccess, wordRead.Message);
            Assert.Equal((short)111, wordRead.Content["%MW120"]);
            Assert.Equal((short)222, wordRead.Content["%MW121"]);

            var boolRead = pool.ReadBool("%M60");
            Assert.True(boolRead.IsSuccess, boolRead.Message);
            Assert.True(boolRead.Content);
        }
    }
}
