using System;
using System.Threading;
using Xunit;
using Nexus.Robot.Kuka;

namespace Nexus.Robot.Kuka.Tests
{
    public class KukaTcpIntegrationTests : IDisposable
    {
        private readonly KukaTcpVirtualServer _server;
        private readonly KukaTcpClient _client;

        public KukaTcpIntegrationTests()
        {
            _server = new KukaTcpVirtualServer(0);
            _server.Start();
            Thread.Sleep(100);
            _client = new KukaTcpClient("127.0.0.1", _server.Port);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void ReadVariable_ReturnsValue()
        {
            _server.SetVariable("$POS_ACT", "100.5");
            var result = _client.ReadString("$POS_ACT");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal("100.5", result.Content);
        }

        [Fact]
        public void ReadVariable_DefaultZero()
        {
            var result = _client.ReadString("MYVAR");
            Assert.True(result.IsSuccess);
            Assert.Equal("0", result.Content);
        }

        [Fact]
        public void WriteVariable_RoundTrip()
        {
            var writeResult = _client.Write("TEST_VAR", "42");
            Assert.True(writeResult.IsSuccess, writeResult.Message ?? "Write failed");

            var readResult = _client.ReadString("TEST_VAR");
            Assert.True(readResult.IsSuccess);
            Assert.Equal("42", readResult.Content);
        }

        [Fact]
        public void ReadRawBytes_ReturnsData()
        {
            _server.SetVariable("SENSOR", "3.14");
            var result = _client.Read("SENSOR");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.True(result.Content.Length > 0);
        }

        [Fact]
        public void WriteMultipleVariables_Succeeds()
        {
            var writeResult = _client.Write(
                new[] { "A", "B" },
                new[] { "10", "20" });
            Assert.True(writeResult.IsSuccess, writeResult.Message ?? "Write failed");

            var a = _client.ReadString("A");
            var b = _client.ReadString("B");
            Assert.True(a.IsSuccess);
            Assert.True(b.IsSuccess);
            Assert.Equal("10", a.Content);
            Assert.Equal("20", b.Content);
        }

        [Fact]
        public void StartProgram_Succeeds()
        {
            var result = _client.StartProgram("testprog");
            Assert.True(result.IsSuccess, result.Message ?? "StartProgram failed");
        }

        [Fact]
        public void ResetProgram_Succeeds()
        {
            var result = _client.ResetProgram();
            Assert.True(result.IsSuccess, result.Message ?? "ResetProgram failed");
        }

        [Fact]
        public void StopProgram_Succeeds()
        {
            var result = _client.StopProgram();
            Assert.True(result.IsSuccess, result.Message ?? "StopProgram failed");
        }

        [Fact]
        public void MultipleReads_Succeed()
        {
            _server.SetVariable("X", "1");
            _server.SetVariable("Y", "2");
            for (int i = 0; i < 3; i++)
            {
                var result = _client.ReadString("X");
                Assert.True(result.IsSuccess);
            }
        }

        [Fact]
        public void ConnectionPool_ReadString_ReusesPersistentConnection()
        {
            _server.SetVariable("$POS_ACT", "100.5");
            using var pool = new KukaTcpConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var first = pool.ReadString("$POS_ACT");
            var second = pool.ReadString("$POS_ACT");

            Assert.True(first.IsSuccess, first.Message);
            Assert.True(second.IsSuccess, second.Message);
            Assert.Equal("100.5", first.Content);
            Assert.Equal("100.5", second.Content);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_WriteVariable_RoundTrip()
        {
            using var pool = new KukaTcpConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var writeResult = pool.Write("TEST_VAR", "42");
            var readResult = pool.ReadString("TEST_VAR");

            Assert.True(writeResult.IsSuccess, writeResult.Message);
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal("42", readResult.Content);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            _server.SetVariable("SENSOR", "3.14");
            using var pool = new KukaTcpConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);
            string? sent = null;
            string? received = null;
            pool.OnMessageSent += (_, message) => sent = message;
            pool.OnMessageReceived += (_, message) => received = message;

            var result = pool.ReadString("SENSOR");

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal("00SENSOR", sent);
            Assert.Equal("3.14", received);
        }

        [Fact]
        public void ConnectionPool_ProgramControl_Succeeds()
        {
            using var pool = new KukaTcpConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var start = pool.StartProgram("testprog");
            var reset = pool.ResetProgram();
            var stop = pool.StopProgram();

            Assert.True(start.IsSuccess, start.Message);
            Assert.True(reset.IsSuccess, reset.Message);
            Assert.True(stop.IsSuccess, stop.Message);
            Assert.Equal(1, _server.ConnectionCount);
        }
    }
}
