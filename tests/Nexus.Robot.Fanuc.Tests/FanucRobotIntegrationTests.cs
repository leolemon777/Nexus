using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nexus.Robot.Fanuc;

namespace Nexus.Robot.Fanuc.Tests
{
    public class FanucRobotIntegrationTests : IDisposable
    {
        private readonly FanucRobotVirtualServer _server;
        private readonly FanucRobotClient _client;

        public FanucRobotIntegrationTests()
        {
            _server = new FanucRobotVirtualServer(0);
            _server.Start();
            Thread.Sleep(100);
            _client = new FanucRobotClient("127.0.0.1", _server.Port);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void ReadNumericRegister_DefaultZero()
        {
            var result = _client.ReadNumericRegister(0);
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(0, result.Content);
        }

        [Fact]
        public void ReadNumericRegister_AfterSet()
        {
            _server.SetNumericRegister(5, 12345);
            var result = _client.ReadNumericRegister(5);
            Assert.True(result.IsSuccess);
            Assert.Equal(12345, result.Content);
        }

        [Fact]
        public void WriteNumericRegister_RoundTrip()
        {
            var writeResult = _client.WriteNumericRegister(10, 999);
            Assert.True(writeResult.IsSuccess, writeResult.Message ?? "Write failed");

            var readResult = _client.ReadNumericRegister(10);
            Assert.True(readResult.IsSuccess);
            Assert.Equal(999, readResult.Content);
        }

        [Fact]
        public void WritePositionRegister_RoundTrip()
        {
            double[] expected = { 1.1, 2.2, 3.3, 4.4, 5.5, 6.6 };
            var writeResult = _client.WritePositionRegister(2, expected);
            Assert.True(writeResult.IsSuccess, writeResult.Message ?? "Write failed");

            var readResult = _client.ReadPositionRegister(2);
            Assert.True(readResult.IsSuccess, readResult.Message ?? "Read failed");
            Assert.Equal(expected.Length, readResult.Content.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], readResult.Content[i], 2);
        }

        [Fact]
        public void WriteStringRegister_RoundTrip()
        {
            var writeResult = _client.WriteStringRegister(3, "HELLO");
            Assert.True(writeResult.IsSuccess, writeResult.Message ?? "Write failed");

            var readResult = _client.ReadStringRegister(3);
            Assert.True(readResult.IsSuccess, readResult.Message ?? "Read failed");
            Assert.Equal("HELLO", readResult.Content);
        }

        [Fact]
        public void ReadDigitalInput_ReturnsValue()
        {
            _server.SetDigitalInput(3, true);
            var result = _client.ReadDigitalInput(3);
            Assert.True(result.IsSuccess);
            Assert.True(result.Content);
        }

        [Fact]
        public void ReadDigitalOutput_ReturnsValue()
        {
            _server.SetDigitalOutput(7, true);
            var result = _client.ReadDigitalOutput(7);
            Assert.True(result.IsSuccess);
            Assert.True(result.Content);
        }

        [Fact]
        public void WriteDigitalOutput_RoundTrip()
        {
            var writeResult = _client.WriteDigitalOutput(0, true);
            Assert.True(writeResult.IsSuccess, writeResult.Message ?? "Write failed");

            var readResult = _client.ReadDigitalOutput(0);
            Assert.True(readResult.IsSuccess);
            Assert.True(readResult.Content);
        }

        [Fact]
        public void ReadRobotPosition_ReturnsSixAxes()
        {
            _server.SetRobotPosition(0, 10.5);
            _server.SetRobotPosition(5, -90.0);
            var result = _client.ReadRobotPosition();
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(6, result.Content.Length);
            Assert.Equal(10.5, result.Content[0], 2);
            Assert.Equal(-90.0, result.Content[5], 2);
        }

        [Fact]
        public void ReadRobotStatus_ReturnsModeAndState()
        {
            _server.SetRobotStatus(2, 1); // 自动/运行
            var result = _client.ReadRobotStatus();
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(2, result.Content.Mode);
            Assert.Equal(1, result.Content.State);
        }

        [Fact]
        public void ReadGroupInput_ReturnsValue()
        {
            _server.SetGroupInput(0, 42);
            var result = _client.ReadGroupInput(0);
            Assert.True(result.IsSuccess);
            Assert.Equal(42, result.Content);
        }

        [Fact]
        public void WriteGroupOutput_RoundTrip()
        {
            var writeResult = _client.WriteGroupOutput(4, 123);
            Assert.True(writeResult.IsSuccess, writeResult.Message ?? "Write failed");

            var readResult = _client.ReadGroupOutput(4);
            Assert.True(readResult.IsSuccess, readResult.Message ?? "Read failed");
            Assert.Equal(123, readResult.Content);
        }

        [Fact]
        public void SendString_Succeeds()
        {
            var result = _client.SendString("PING");
            Assert.True(result.IsSuccess, result.Message ?? "SendString failed");
        }

        [Fact]
        public void MultipleReads_Succeed()
        {
            for (int i = 0; i < 5; i++)
            {
                var result = _client.ReadNumericRegister(i);
                Assert.True(result.IsSuccess);
            }
        }

        [Fact]
        public void ConnectionPool_ReadNumericRegister_ReusesPersistentConnection()
        {
            _server.SetNumericRegister(5, 12345);
            using var pool = new FanucRobotConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            for (int i = 0; i < 3; i++)
            {
                var result = pool.ReadNumericRegister(5);
                Assert.True(result.IsSuccess, result.Message ?? "Pool read failed");
                Assert.Equal(12345, result.Content);
            }

            WaitForConnections(1);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_WriteAndReadNumericRegister_RoundTrip()
        {
            using var pool = new FanucRobotConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var write = pool.WriteNumericRegister(10, 2026);
            Assert.True(write.IsSuccess, write.Message ?? "Pool write failed");

            var read = pool.ReadNumericRegister(10);
            Assert.True(read.IsSuccess, read.Message ?? "Pool read after write failed");
            Assert.Equal(2026, read.Content);

            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ReadRobotStatus_ReturnsModeAndState()
        {
            _server.SetRobotStatus(3, 1);
            using var pool = new FanucRobotConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = pool.ReadRobotStatus();

            Assert.True(result.IsSuccess, result.Message ?? "Pool status read failed");
            Assert.Equal(3, result.Content.Mode);
            Assert.Equal(1, result.Content.State);
            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ReadRobotPosition_ReturnsSixAxes()
        {
            _server.SetRobotPosition(0, 10.5);
            _server.SetRobotPosition(5, -90.0);
            using var pool = new FanucRobotConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = pool.ReadRobotPosition();

            Assert.True(result.IsSuccess, result.Message ?? "Pool position read failed");
            Assert.Equal(6, result.Content.Length);
            Assert.Equal(10.5, result.Content[0], 2);
            Assert.Equal(-90.0, result.Content[5], 2);
            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public async Task ConnectionPool_ExecuteAsync_ReadNumericRegister_ReusesPersistentConnection()
        {
            _server.SetNumericRegister(8, 77);
            using var pool = new FanucRobotConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = await pool.ExecuteAsync(c => Task.FromResult(c.ReadNumericRegister(8)));

            Assert.True(result.IsSuccess, result.Message ?? "Pool async read failed");
            Assert.Equal(77, result.Content);
            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsMessageEvents()
        {
            _server.SetDigitalInput(3, true);
            using var pool = new FanucRobotConnectionPool("127.0.0.1", _server.Port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, __) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, __) => Interlocked.Increment(ref received);

            var result = pool.ReadDigitalInput(3);

            Assert.True(result.IsSuccess, result.Message ?? "Pool read failed");
            Assert.True(result.Content);
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
