using System;
using System.Threading;
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
        public void BatchReadAsync_SameResultAsSync()
        {
            var (server, client) = StartServerAndConnect();
            server.SetRWord(300, 1111);
            server.SetRWord(301, 2222);

            var syncResult = client.BatchRead(new[] { "R300", "R301" });
            var asyncResult = client.BatchReadAsync(new[] { "R300", "R301" }).Result;
            Assert.Equal(syncResult.IsSuccess, asyncResult.IsSuccess);
        }
    }
}
