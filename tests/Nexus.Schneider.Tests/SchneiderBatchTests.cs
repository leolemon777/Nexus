using System;
using System.Threading;
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
        public void BatchReadAsync_SameResultAsSync()
        {
            var (server, client) = StartServerAndConnect();
            server.SetHoldingRegister(200, 1111);
            server.SetHoldingRegister(201, 2222);

            var syncResult = client.BatchRead(new[] { "%MW200", "%MW201" });
            var asyncResult = client.BatchReadAsync(new[] { "%MW200", "%MW201" }).Result;

            Assert.Equal(syncResult.IsSuccess, asyncResult.IsSuccess);
        }
    }
}
