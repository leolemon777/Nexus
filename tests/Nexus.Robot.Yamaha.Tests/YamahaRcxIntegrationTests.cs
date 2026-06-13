using System;
using System.Threading;
using Xunit;
using Nexus.Robot.Yamaha;

namespace Nexus.Robot.Yamaha.Tests
{
    public class YamahaRcxIntegrationTests : IDisposable
    {
        private readonly YamahaRcxVirtualServer _server;
        private readonly YamahaRcxClient _client;

        public YamahaRcxIntegrationTests()
        {
            _server = new YamahaRcxVirtualServer(0);
            _server.Start();
            Thread.Sleep(100);
            _client = new YamahaRcxClient("127.0.0.1", _server.Port);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void ReadMotorStatus_DefaultZero()
        {
            var result = _client.ReadMotorStatus();
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(0, result.Content);
        }

        [Fact]
        public void ReadMotorStatus_AfterSet()
        {
            _server.SetMotorStatus(2);
            var result = _client.ReadMotorStatus();
            Assert.True(result.IsSuccess);
            Assert.Equal(2, result.Content);
        }

        [Fact]
        public void ReadModeStatus_ReturnsValue()
        {
            _server.SetModeStatus(3);
            var result = _client.ReadModeStatus();
            Assert.True(result.IsSuccess);
            Assert.Equal(3, result.Content);
        }

        [Fact]
        public void ReadEmergencyStatus_DefaultZero()
        {
            var result = _client.ReadEmergencyStatus();
            Assert.True(result.IsSuccess);
            Assert.Equal(0, result.Content);
        }

        [Fact]
        public void ReadJoints_ReturnsSixAxes()
        {
            _server.SetJoint(0, 10.5f);
            _server.SetJoint(3, -45.0f);
            var result = _client.ReadJoints();
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.True(result.Content.Length >= 4);
            Assert.Equal(10.5f, result.Content[0], 1);
            Assert.Equal(-45.0f, result.Content[3], 1);
        }

        [Fact]
        public void ReadDI_ReturnsValue()
        {
            _server.SetDigitalInput(0, 255);
            var result = _client.ReadDI(0);
            Assert.True(result.IsSuccess);
            Assert.Equal(8, result.Content.Length);
            // 所有 8 位都应为 true（值 255）
            Assert.True(result.Content[0]);
            Assert.True(result.Content[7]);
        }

        [Fact]
        public void ReadDO_ReturnsValue()
        {
            _server.SetDigitalOutput(1, 5);
            var result = _client.ReadDO(1);
            Assert.True(result.IsSuccess);
            Assert.Equal(8, result.Content.Length);
            Assert.True(result.Content[0]);   // bit 0
            Assert.False(result.Content[1]);  // bit 1 = 0
            Assert.True(result.Content[2]);   // bit 2
        }

        [Fact]
        public void Reset_Succeeds()
        {
            var result = _client.Reset();
            Assert.True(result.IsSuccess, result.Message ?? "Reset failed");
        }

        [Fact]
        public void Run_Succeeds()
        {
            var result = _client.Run();
            Assert.True(result.IsSuccess, result.Message ?? "Run failed");
        }

        [Fact]
        public void Stop_Succeeds()
        {
            var result = _client.Stop();
            Assert.True(result.IsSuccess, result.Message ?? "Stop failed");
        }

        [Fact]
        public void Load_Succeeds()
        {
            var result = _client.Load("MAIN", 1);
            Assert.True(result.IsSuccess, result.Message ?? "Load failed");
        }

        [Fact]
        public void UnknownCommand_ReturnsError()
        {
            var result = _client.ReadCommand("@ UNKNOWN");
            Assert.False(result.IsSuccess);
            Assert.Contains("NG=1", result.Message);
        }

        [Fact]
        public void MultipleReads_Succeed()
        {
            for (int i = 0; i < 3; i++)
            {
                var result = _client.ReadMotorStatus();
                Assert.True(result.IsSuccess);
            }
        }

        [Fact]
        public void ConnectionPool_ReadMotorStatus_ReusesPersistentConnection()
        {
            _server.SetMotorStatus(2);
            using var pool = new YamahaRcxConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var first = pool.ReadMotorStatus();
            var second = pool.ReadMotorStatus();

            Assert.True(first.IsSuccess, first.Message);
            Assert.True(second.IsSuccess, second.Message);
            Assert.Equal(2, first.Content);
            Assert.Equal(2, second.Content);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            using var pool = new YamahaRcxConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);
            string? sent = null;
            string? received = null;
            pool.OnMessageSent += (_, message) => sent = message;
            pool.OnMessageReceived += (_, message) => received = message;

            var result = pool.ReadModeStatus();

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal("@?MODE ", sent);
            Assert.Contains("OK", received);
        }

        [Fact]
        public void ConnectionPool_ReadIo_ReturnsBits()
        {
            _server.SetDigitalInput(0, 5);
            using var pool = new YamahaRcxConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = pool.ReadDI(0);

            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Content[0]);
            Assert.False(result.Content[1]);
            Assert.True(result.Content[2]);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ProgramControl_Succeeds()
        {
            using var pool = new YamahaRcxConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var reset = pool.Reset();
            var run = pool.Run();
            var stop = pool.Stop();
            var load = pool.Load("MAIN", 1);

            Assert.True(reset.IsSuccess, reset.Message);
            Assert.True(run.IsSuccess, run.Message);
            Assert.True(stop.IsSuccess, stop.Message);
            Assert.True(load.IsSuccess, load.Message);
            Assert.Equal(1, _server.ConnectionCount);
        }
    }
}
