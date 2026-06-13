using System;
using System.Threading;
using Xunit;
using Nexus.Toledo;

namespace Nexus.Toledo.Tests
{
    public class ToledoIntegrationTests : IDisposable
    {
        private readonly ToledoVirtualServer _server;
        private readonly ToledoClient _client;

        public ToledoIntegrationTests()
        {
            _server = new ToledoVirtualServer(0);
            _server.Start();
            Thread.Sleep(100);
            _client = new ToledoClient("127.0.0.1", _server.Port);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void ReadWeight_ReturnsSuccess()
        {
            var result = _client.ReadWeight();
            Assert.True(result.IsSuccess, result.Message ?? "ReadWeight failed");
        }

        [Fact]
        public void ReadWeight_ReturnsData()
        {
            var result = _client.ReadWeight();
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Content);
        }

        [Fact]
        public void ReadRaw_ReturnsData()
        {
            var result = _client.ReadRaw();
            Assert.True(result.IsSuccess, result.Message ?? "ReadRaw failed");
            Assert.True(result.Content.Length >= 16);
        }

        [Fact]
        public void ReadWeight_DefaultPositive()
        {
            _server.SetPositive(true);
            var result = _client.ReadWeight();
            Assert.True(result.IsSuccess);
            Assert.True(result.Content.Positive);
        }

        [Fact]
        public void MultipleReads_Succeed()
        {
            for (int i = 0; i < 3; i++)
            {
                var result = _client.ReadRaw();
                Assert.True(result.IsSuccess, $"Read #{i} failed: {result.Message}");
            }
        }

        [Fact]
        public void ConnectionPool_ReadWeight_ReusesPersistentConnection()
        {
            using var pool = new ToledoConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var first = pool.ReadWeight();
            var second = pool.ReadWeight();

            Assert.True(first.IsSuccess, first.Message);
            Assert.True(second.IsSuccess, second.Message);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ReadRaw_ForwardsReceivedEvent()
        {
            using var pool = new ToledoConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);
            string? received = null;
            pool.OnMessageReceived += (_, message) => received = message;

            var result = pool.ReadRaw();

            Assert.True(result.IsSuccess, result.Message);
            Assert.NotNull(received);
            Assert.Contains("0D", received);
        }

        [Fact]
        public async Task ConnectionPool_ReadWeightAsync_ReturnsWeight()
        {
            _server.SetWeight(2.345f);
            _server.SetDecimalPlaces(2);
            using var pool = new ToledoConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = await pool.ReadWeightAsync();

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(2.345f, result.Content.Weight, 3);
            Assert.Equal(1, _server.ConnectionCount);
        }
    }
}
