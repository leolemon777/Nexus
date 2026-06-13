using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nexus.Robot.Efort;

namespace Nexus.Robot.Efort.Tests
{
    public class EfortIntegrationTests : IDisposable
    {
        private readonly EfortVirtualServer _server;
        private readonly EfortClient _client;

        public EfortIntegrationTests()
        {
            _server = new EfortVirtualServer(0);
            _server.Start();
            Thread.Sleep(100);
            _client = new EfortClient("127.0.0.1", _server.Port);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void ReadRobotData_ReturnsSuccess()
        {
            var result = _client.ReadRobotData();
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
        }

        [Fact]
        public void ReadRobotData_DefaultServoOff()
        {
            var result = _client.ReadRobotData();
            Assert.True(result.IsSuccess);
            Assert.Equal((byte)0, result.Content.ServoStatus);
        }

        [Fact]
        public void ReadRobotData_ServoOn()
        {
            _server.SetServoStatus(1);
            var result = _client.ReadRobotData();
            Assert.True(result.IsSuccess);
            Assert.Equal((byte)1, result.Content.ServoStatus);
        }

        [Fact]
        public void ReadRobotData_AxisPositions()
        {
            _server.SetAxisPosition(0, 10.5f);
            _server.SetAxisPosition(3, -45.0f);
            var result = _client.ReadRobotData();
            Assert.True(result.IsSuccess);
            Assert.Equal(10.5f, result.Content.AxisPositions[0], 2);
            Assert.Equal(-45.0f, result.Content.AxisPositions[3], 2);
        }

        [Fact]
        public void ReadRobotData_CartesianPositions()
        {
            _server.SetCartesianPosition(0, 100.0f);
            _server.SetCartesianPosition(1, 200.0f);
            _server.SetCartesianPosition(2, 300.0f);
            var result = _client.ReadRobotData();
            Assert.True(result.IsSuccess);
            Assert.Equal(100.0f, result.Content.CartesianPositions[0], 2);
            Assert.Equal(200.0f, result.Content.CartesianPositions[1], 2);
            Assert.Equal(300.0f, result.Content.CartesianPositions[2], 2);
        }

        [Fact]
        public void ReadRobotData_ModeAndSpeed()
        {
            _server.SetModeStatus(3);
            _server.SetSpeedStatus(75);
            var result = _client.ReadRobotData();
            Assert.True(result.IsSuccess);
            Assert.Equal((ushort)3, result.Content.ModeStatus);
            Assert.Equal((ushort)75, result.Content.SpeedStatus);
        }

        [Fact]
        public void ReadRobotData_ErrorStatus()
        {
            _server.SetErrorStatus(1);
            var result = _client.ReadRobotData();
            Assert.True(result.IsSuccess);
            Assert.Equal((byte)1, result.Content.ErrorStatus);
        }

        [Fact]
        public void ReadRobotData_DigitalInputs()
        {
            _server.SetDigitalInput(0, 0xAA);
            _server.SetDigitalInput(31, 0x55);

            var result = _client.ReadRobotData();

            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal((byte)0xAA, result.Content.DigitalInputs[0]);
            Assert.Equal((byte)0x55, result.Content.DigitalInputs[31]);
        }

        [Fact]
        public void ReadRobotData_NoEmergencyStop()
        {
            var result = _client.ReadRobotData();
            Assert.True(result.IsSuccess);
            Assert.Equal((byte)1, result.Content.EmergencyStopStatus);
        }

        [Fact]
        public void ReadRaw_Returns788Bytes()
        {
            var result = _client.ReadRaw();
            Assert.True(result.IsSuccess);
            Assert.Equal(788, result.Content.Length);
        }

        [Fact]
        public void ReadRobotData_MultipleReads()
        {
            var r1 = _client.ReadRobotData();
            Assert.True(r1.IsSuccess);

            _server.SetServoStatus(1);
            var r2 = _client.ReadRobotData();
            Assert.True(r2.IsSuccess);
            Assert.Equal((byte)1, r2.Content.ServoStatus);
        }

        [Fact]
        public void ConnectionPool_ReadRobotData_ReusesPersistentConnection()
        {
            _server.SetServoStatus(1);
            _server.SetModeStatus(3);
            using var pool = new EfortConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            for (int i = 0; i < 3; i++)
            {
                var result = pool.ReadRobotData();
                Assert.True(result.IsSuccess, result.Message ?? "Pool read failed");
                Assert.Equal((byte)1, result.Content.ServoStatus);
                Assert.Equal((ushort)3, result.Content.ModeStatus);
            }

            WaitForConnections(1);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ReadRaw_Returns788Bytes()
        {
            using var pool = new EfortConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = pool.ReadRaw();

            Assert.True(result.IsSuccess, result.Message ?? "Pool raw read failed");
            Assert.Equal(788, result.Content.Length);
            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public async Task ConnectionPool_ExecuteAsync_ReadRobotData_ReusesPersistentConnection()
        {
            _server.SetSpeedStatus(75);
            using var pool = new EfortConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = await pool.ExecuteAsync(c => Task.FromResult(c.ReadRobotData()));

            Assert.True(result.IsSuccess, result.Message ?? "Pool async read failed");
            Assert.Equal((ushort)75, result.Content.SpeedStatus);
            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsMessageEvents()
        {
            using var pool = new EfortConnectionPool("127.0.0.1", _server.Port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, __) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, __) => Interlocked.Increment(ref received);

            var result = pool.ReadRobotData();

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
