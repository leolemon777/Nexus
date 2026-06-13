using System;
using System.Threading;
using Xunit;
using Nexus.Secs;

namespace Nexus.Secs.Tests
{
    public class SecsHsmsIntegrationTests : IDisposable
    {
        private readonly SecsHsmsVirtualServer _server;
        private readonly SecsHsmsClient _client;

        public SecsHsmsIntegrationTests()
        {
            _server = new SecsHsmsVirtualServer(0);
            _server.Start();
            Thread.Sleep(100);
            _client = new SecsHsmsClient("127.0.0.1", _server.Port);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void Linktest_Succeeds()
        {
            var result = _client.Linktest();
            Assert.True(result.IsSuccess, result.Message ?? "Linktest failed");
        }

        [Fact]
        public void Select_Succeeds()
        {
            var result = _client.Select();
            Assert.True(result.IsSuccess, result.Message ?? "Select failed");
        }

        [Fact]
        public void AreYouThere_ReturnsReply()
        {
            var result = _client.AreYouThere();
            Assert.True(result.IsSuccess, result.Message ?? "S1F1 failed");
            Assert.NotNull(result.Content);
            // Reply is S1F2
            Assert.Equal(1, result.Content.Stream);
            Assert.Equal(2, result.Content.Function);
        }

        [Fact]
        public void EstablishCommunication_ReturnsReply()
        {
            var result = _client.EstablishCommunication();
            Assert.True(result.IsSuccess, result.Message ?? "S1F13 failed");
            Assert.Equal(1, result.Content.Stream);
            Assert.Equal(14, result.Content.Function);
        }

        [Fact]
        public void SendPrimaryMessage_WithData_ReturnsReply()
        {
            byte[] data = new byte[] { 0x01, 0x02, 0x03 };
            var result = _client.SendPrimaryMessage(2, 41, data);
            Assert.True(result.IsSuccess, result.Message ?? "S2F41 failed");
            Assert.Equal(2, result.Content.Stream);
            Assert.Equal(42, result.Content.Function);
            Assert.False(result.Content.ReplyExpected);
            Assert.Equal(data, result.Content.Data);
        }

        [Fact]
        public void SendPrimaryMessage_EchoesDeviceId()
        {
            _client.DeviceId = 7;

            var result = _client.AreYouThere();

            Assert.True(result.IsSuccess, result.Message ?? "S1F1 failed");
            Assert.Equal((ushort)7, result.Content.DeviceId);
        }

        [Fact]
        public void SendPrimaryMessage_UsesDistinctSystemBytes()
        {
            var first = _client.AreYouThere();
            var second = _client.OnlineRequest();

            Assert.True(first.IsSuccess, first.Message ?? "first failed");
            Assert.True(second.IsSuccess, second.Message ?? "second failed");
            Assert.NotEqual(first.Content.SystemBytes, second.Content.SystemBytes);
        }

        [Fact]
        public void OnlineRequest_ReturnsReply()
        {
            var result = _client.OnlineRequest();
            Assert.True(result.IsSuccess, result.Message ?? "S1F17 failed");
            Assert.Equal(1, result.Content.Stream);
            Assert.Equal(18, result.Content.Function);
        }

        [Fact]
        public void MultipleLinktests_Succeed()
        {
            for (int i = 0; i < 3; i++)
            {
                var result = _client.Linktest();
                Assert.True(result.IsSuccess, $"Linktest #{i} failed: {result.Message}");
            }
        }

        [Fact]
        public void Separate_Succeeds()
        {
            var result = _client.Separate();
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void LinktestAfterSelect_Succeeds()
        {
            var select = _client.Select();
            Assert.True(select.IsSuccess);
            var linktest = _client.Linktest();
            Assert.True(linktest.IsSuccess);
        }

        [Fact]
        public void ConnectionPool_Linktest_ReusesPersistentConnection()
        {
            using var pool = new SecsHsmsConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var first = pool.Linktest();
            var second = pool.Linktest();

            Assert.True(first.IsSuccess, first.Message);
            Assert.True(second.IsSuccess, second.Message);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_AreYouThere_EchoesDeviceId()
        {
            using var pool = new SecsHsmsConnectionPool("127.0.0.1", _server.Port, deviceId: 7, maxPoolSize: 1);

            var result = pool.AreYouThere();

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)7, result.Content.DeviceId);
            Assert.Equal(1, result.Content.Stream);
            Assert.Equal(2, result.Content.Function);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            using var pool = new SecsHsmsConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);
            string? sent = null;
            string? received = null;
            pool.OnMessageSent += (_, message) => sent = message;
            pool.OnMessageReceived += (_, message) => received = message;

            var result = pool.Linktest();

            Assert.True(result.IsSuccess, result.Message);
            Assert.NotNull(sent);
            Assert.NotNull(received);
        }

        [Fact]
        public void ConnectionPool_SendPrimaryMessage_WithData_ReturnsReply()
        {
            byte[] data = new byte[] { 0x01, 0x02, 0x03 };
            using var pool = new SecsHsmsConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = pool.SendPrimaryMessage(2, 41, data);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(2, result.Content.Stream);
            Assert.Equal(42, result.Content.Function);
            Assert.Equal(data, result.Content.Data);
            Assert.Equal(1, _server.ConnectionCount);
        }
    }
}
