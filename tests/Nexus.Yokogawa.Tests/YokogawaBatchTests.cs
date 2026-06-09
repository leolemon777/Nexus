using System;
using System.Threading;
using Xunit;
using Nexus.Yokogawa;

namespace Nexus.Yokogawa.Tests
{
    /// <summary>
    /// YokogawaClient IBatchReadWrite 接口测试 — 通过虚拟服务器验证批量读写。
    /// </summary>
    public class YokogawaBatchTests : IDisposable
    {
        private static int _portCounter = 28000;
        private readonly int _port;
        private YokogawaVirtualServer? _server;

        public YokogawaBatchTests()
        {
            _port = Interlocked.Increment(ref _portCounter);
        }

        public void Dispose()
        {
            _server?.Stop();
            _server?.Dispose();
        }

        private (YokogawaVirtualServer server, YokogawaClient client) StartServerAndConnect()
        {
            _server = new YokogawaVirtualServer(_port);
            _server.Start();
            var client = new YokogawaClient("127.0.0.1", _port);
            client.SetPersistentConnection();
            var connect = client.Connect();
            Assert.True(connect.IsSuccess, connect.Message);
            return (_server, client);
        }

        [Fact]
        public void Client_Implements_IBatchReadWrite()
        {
            var client = new YokogawaClient("127.0.0.1", 1);
            Assert.IsAssignableFrom<IBatchReadWrite>(client);
        }

        [Fact]
        public void BatchRead_ReturnsMultipleValues()
        {
            var (server, client) = StartServerAndConnect();

            // YokogawaVirtualServer 使用 SetWord(dataCode, address, value)
            // D100 → dataCode 根据地址解析确定
            server.SetWord(0x0001, 100, 100);
            server.SetWord(0x0001, 101, 200);

            var result = client.BatchRead(new[] { "D100", "D101" });
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
        public void RandomRead_ReturnsByteArrays()
        {
            var (server, client) = StartServerAndConnect();
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

            var items = new System.Collections.Generic.KeyValuePair<string, object>[]
            {
                new("D100", (short)42),
                new("D101", (short)99),
            };

            var result = client.BatchWrite(items);
            Assert.True(result.IsSuccess, result.Message);
        }

        [Fact]
        public void BatchWrite_EmptyList_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();
            var items = new System.Collections.Generic.KeyValuePair<string, object>[0];
            var result = client.BatchWrite(items);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void BatchWrite_SupportedTypes()
        {
            var (server, client) = StartServerAndConnect();

            var result = client.BatchWrite(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, object>("D100", (short)1),
                new System.Collections.Generic.KeyValuePair<string, object>("D101", (ushort)2),
                new System.Collections.Generic.KeyValuePair<string, object>("D102", true),
            });
            Assert.True(result.IsSuccess, result.Message);
        }
    }
}
