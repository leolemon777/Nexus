using System;
using System.Threading;
using Xunit;
using Nexus.Rkc;

namespace Nexus.Rkc.Tests
{
    public class RkcTemperatureIntegrationTests : IDisposable
    {
        private readonly RkcTemperatureVirtualServer _server;
        private readonly RkcTemperatureClient _client;

        public RkcTemperatureIntegrationTests()
        {
            _server = new RkcTemperatureVirtualServer(0);
            _server.Start();
            Thread.Sleep(100);
            _client = new RkcTemperatureClient("127.0.0.1", _server.Port);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void ReadDouble_DefaultValue()
        {
            _server.SetValue("M1", 0.0);
            var result = _client.ReadDouble("M1");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(0.0, result.Content, 1);
        }

        [Fact]
        public void ReadDouble_ReturnsSetValue()
        {
            _server.SetValue("M1", 123.4);
            var result = _client.ReadDouble("M1");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(123.4, result.Content, 1);
        }

        [Fact]
        public void ReadDouble_AnotherAddress()
        {
            _server.SetValue("M2", 456.7);
            var result = _client.ReadDouble("M2");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(456.7, result.Content, 1);
        }

        [Fact]
        public void Write_Succeeds()
        {
            var result = _client.Write("M1", 100.0);
            Assert.True(result.IsSuccess, result.Message ?? "Write failed");
        }

        [Fact]
        public void ReadWithStation_Succeeds()
        {
            _server.SetValue("M1", 25.5);
            var result = _client.ReadDouble("s=1;M1");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(25.5, result.Content, 1);
        }

        [Fact]
        public void MultipleReads_Succeed()
        {
            _server.SetValue("M1", 50.0);
            for (int i = 0; i < 3; i++)
            {
                var result = _client.ReadDouble("M1");
                Assert.True(result.IsSuccess, $"Read #{i} failed: {result.Message}");
            }
        }

        [Fact]
        public void ConnectionPool_ReadDouble_ReusesPersistentConnection()
        {
            _server.SetValue("M1", 12.3);
            using var pool = new RkcTemperatureConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var read = pool.ReadDouble("M1");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal(12.3, read.Content, 1);

            var secondRead = pool.ReadDouble("M1");
            Assert.True(secondRead.IsSuccess, secondRead.Message);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            _server.SetValue("M1", 23.4);
            using var pool = new RkcTemperatureConnectionPool("127.0.0.1", _server.Port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, _) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, _) => Interlocked.Increment(ref received);

            var read = pool.ReadDouble("M1");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal(23.4, read.Content, 1);
            Assert.True(sent > 0);
            Assert.True(received > 0);
        }

        [Fact]
        public void ConnectionPool_Write_Succeeds()
        {
            using var pool = new RkcTemperatureConnectionPool("127.0.0.1", _server.Port);

            var write = pool.Write("M1", 100.0);

            Assert.True(write.IsSuccess, write.Message);
        }

        [Fact]
        public void ConnectionPool_StationPrefixOverridesDefault()
        {
            _server.SetValue("M2", 45.6);
            using var pool = new RkcTemperatureConnectionPool("127.0.0.1", _server.Port, station: 1);

            var read = pool.ReadDouble("s=2;M2");

            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal(45.6, read.Content, 1);
        }
    }
}
