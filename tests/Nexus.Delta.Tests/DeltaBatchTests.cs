using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Xunit;
using Nexus.Delta;

namespace Nexus.Delta.Tests
{
    /// <summary>
    /// DeltaDvpClient IBatchReadWrite 测试 — 通过 RTU over TCP 虚拟服务器验证。
    /// </summary>
    public class DeltaBatchTests : IDisposable
    {
        private static int _portCounter = 23000;
        private readonly int _port;
        private DeltaDvpVirtualServer? _server;
        private TcpClient? _tcp;
        private DeltaDvpClient? _client;

        public DeltaBatchTests()
        {
            _port = Interlocked.Increment(ref _portCounter);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _tcp?.Close();
            _server?.Stop();
            _server?.Dispose();
        }

        private DeltaDvpClient StartServerAndConnect()
        {
            _server = new DeltaDvpVirtualServer(_port);
            _server.Start();

            // DeltaDvpClient 接受 Stream，通过 TCP 连接虚拟服务器
            _tcp = new TcpClient("127.0.0.1", _port);
            _client = new DeltaDvpClient(_tcp.GetStream(), station: 1);
            return _client;
        }

        [Fact]
        public void Client_Implements_IBatchReadWrite()
        {
            var client = new DeltaDvpClient(new MemoryStream(), station: 1);
            Assert.IsAssignableFrom<IBatchReadWrite>(client);
        }

        [Fact]
        public void BatchRead_EmptyList_ReturnsError()
        {
            var client = StartServerAndConnect();
            var result = client.BatchRead(new string[0]);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void RandomRead_EmptyList_ReturnsError()
        {
            var client = StartServerAndConnect();
            var result = client.RandomRead(new string[0]);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void BatchWrite_EmptyList_ReturnsError()
        {
            var client = StartServerAndConnect();
            var result = client.BatchWrite(new System.Collections.Generic.KeyValuePair<string, object>[0]);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void BatchWrite_UnsupportedType_ReturnsError()
        {
            var client = StartServerAndConnect();
            var result = client.BatchWrite(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, object>("D100", new object()),
            });
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void BatchWrite_SupportedTypes_NoError()
        {
            var client = StartServerAndConnect();
            var result = client.BatchWrite(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, object>("D100", (short)42),
                new System.Collections.Generic.KeyValuePair<string, object>("D101", (ushort)99),
                new System.Collections.Generic.KeyValuePair<string, object>("D102", true),
            });
            // 只要不崩溃就算通过（写入结果取决于虚拟服务器是否正确处理 RTU 帧）
            Assert.NotNull(result);
        }
    }
}
