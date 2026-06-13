using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nexus.Robot.Yaskawa;

namespace Nexus.Robot.Yaskawa.Tests
{
    public class Yrc1000IntegrationTests : IDisposable
    {
        private readonly Yrc1000VirtualServer _server;
        private readonly Yrc1000Client _client;

        public Yrc1000IntegrationTests()
        {
            _server = new Yrc1000VirtualServer(0);
            _server.Start();
            Thread.Sleep(100);
            _client = new Yrc1000Client("127.0.0.1", _server.Port);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void ReadInput_ReturnsDefaultFalse()
        {
            var result = _client.ReadInput(0);
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.False(result.Content);
        }

        [Fact]
        public void ReadInput_ReturnsTrue()
        {
            _server.SetInput(5, true);
            var result = _client.ReadInput(5);
            Assert.True(result.IsSuccess);
            Assert.True(result.Content);
        }

        [Fact]
        public void ReadInputs_BatchRead()
        {
            _server.SetInput(0, true);
            _server.SetInput(2, true);
            var result = _client.ReadInputs(0, 4);
            Assert.True(result.IsSuccess);
            Assert.Equal(4, result.Content.Length);
            Assert.True(result.Content[0]);
            Assert.False(result.Content[1]);
            Assert.True(result.Content[2]);
            Assert.False(result.Content[3]);
        }

        [Fact]
        public void ReadOutput_ReturnsValue()
        {
            _server.SetOutput(10, true);
            var result = _client.ReadOutput(10);
            Assert.True(result.IsSuccess);
            Assert.True(result.Content);
        }

        [Fact]
        public void ReadRegister_ReturnsValue()
        {
            _server.SetRegister(0, 12345);
            var result = _client.ReadRegister(0);
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(12345, result.Content);
        }

        [Fact]
        public void WriteOutput_Succeeds()
        {
            var result = _client.WriteOutput(0, true);
            Assert.True(result.IsSuccess, result.Message ?? "Write failed");
        }

        [Fact]
        public void WriteRegister_Succeeds()
        {
            var result = _client.WriteRegister(0, 999);
            Assert.True(result.IsSuccess, result.Message ?? "Write failed");
        }

        [Fact]
        public void WriteAndReadRegister_RoundTrip()
        {
            _client.WriteRegister(5, 4242);
            var result = _client.ReadRegister(5);
            Assert.True(result.IsSuccess);
            Assert.Equal(4242, result.Content);
        }

        [Fact]
        public void ReadStatus_ReturnsSuccess()
        {
            var result = _client.ReadRobotStatus();
            Assert.True(result.IsSuccess, result.Message ?? "ReadStatus failed");
        }

        [Fact]
        public void MultipleReads_Succeed()
        {
            for (int i = 0; i < 5; i++)
            {
                var result = _client.ReadInput(i);
                Assert.True(result.IsSuccess);
            }
        }

        [Fact]
        public void ConnectionPool_ReadInput_ReusesPersistentConnection()
        {
            _server.SetInput(5, true);
            using var pool = new Yrc1000ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            for (int i = 0; i < 3; i++)
            {
                var result = pool.ReadInput(5);
                Assert.True(result.IsSuccess, result.Message ?? "Pool read failed");
                Assert.True(result.Content);
            }

            WaitForConnections(1);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_WriteAndReadRegister_RoundTrip()
        {
            using var pool = new Yrc1000ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var write = pool.WriteRegister(7, 2026);
            Assert.True(write.IsSuccess, write.Message ?? "Pool write failed");

            var read = pool.ReadRegister(7);
            Assert.True(read.IsSuccess, read.Message ?? "Pool read after write failed");
            Assert.Equal(2026, read.Content);

            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ReadInputs_ReturnsValues()
        {
            _server.SetInput(0, true);
            _server.SetInput(2, true);
            using var pool = new Yrc1000ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = pool.ReadInputs(0, 4);

            Assert.True(result.IsSuccess, result.Message ?? "Pool batch IO read failed");
            Assert.Equal(4, result.Content.Length);
            Assert.True(result.Content[0]);
            Assert.False(result.Content[1]);
            Assert.True(result.Content[2]);
            Assert.False(result.Content[3]);
            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public async Task ConnectionPool_ExecuteAsync_ReadStatus_ReusesPersistentConnection()
        {
            using var pool = new Yrc1000ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = await pool.ExecuteAsync(c => Task.FromResult(c.ReadRobotStatus()));

            Assert.True(result.IsSuccess, result.Message ?? "Pool async status read failed");
            Assert.Equal((byte)0, result.Content.ServoState);
            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsMessageEvents()
        {
            using var pool = new Yrc1000ConnectionPool("127.0.0.1", _server.Port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, __) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, __) => Interlocked.Increment(ref received);

            var result = pool.ReadInput(0);

            Assert.True(result.IsSuccess, result.Message ?? "Pool read failed");
            Assert.True(sent > 0);
            Assert.True(received > 0);
        }

        private void WaitForConnections(int expected)
        {
            for (int i = 0; i < 20 && _server.ConnectionCount < expected; i++)
                Thread.Sleep(25);
        }
    }
}
