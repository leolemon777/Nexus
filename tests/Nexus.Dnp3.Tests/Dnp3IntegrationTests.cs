using System;
using System.Threading;
using Xunit;
using Nexus.Dnp3;

namespace Nexus.Dnp3.Tests
{
    public class Dnp3IntegrationTests : IDisposable
    {
        private readonly Dnp3VirtualServer _server;
        private readonly Dnp3Client _client;

        public Dnp3IntegrationTests()
        {
            _server = new Dnp3VirtualServer(0);
            _server.Start();
            // Wait for the accept thread to be ready instead of a fixed Thread.Sleep(100).
            // Under CI load the 100ms could elapse before AcceptLoop reached AcceptTcpClient(),
            // so the first client read failed with a connection refused — a known flake source.
            // Poll IsRunning + a short bounded spin give the accept loop time to enter Accept().
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!_server.IsRunning && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            _client = new Dnp3Client("127.0.0.1", _server.Port);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }

        [Fact]
        public void ReadFloat_ReturnsValue()
        {
            _server.SetAnalogInput(0, 123.45f);
            var result = _client.ReadFloat("AI0");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(123.45f, result.Content, 2);
        }

        [Fact]
        public void ReadInt16_ReturnsValue()
        {
            _server.SetAnalogInput(0, 100);
            var result = _client.ReadInt16("AI0");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
        }

        [Fact]
        public void ReadDouble_ReturnsValue()
        {
            _server.SetAnalogInput(0, 42.5f);
            var result = _client.ReadDouble("AI0");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
        }

        [Fact]
        public void WriteFloat_Succeeds()
        {
            var result = _client.Write("AO0", 100.0f);
            Assert.True(result.IsSuccess, result.Message ?? "Write failed");
        }

        [Fact]
        public void WriteInt32_Succeeds()
        {
            var result = _client.Write("AO0", 42);
            Assert.True(result.IsSuccess, result.Message ?? "Write failed");
        }

        [Fact]
        public void ReadAnalogInputs_ReturnsValues()
        {
            _server.SetAnalogInput(0, 10.0f);
            _server.SetAnalogInput(1, 20.0f);
            var result = _client.ReadAnalogInputs(0, 4);
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(4, result.Content.Length);
            Assert.Equal(10.0f, result.Content[0], 2);
            Assert.Equal(20.0f, result.Content[1], 2);
        }

        [Fact]
        public void ReadBinaryInputs_ReturnsPackedValues()
        {
            _server.SetBinaryInput(0, true);
            _server.SetBinaryInput(2, true);

            var result = _client.ReadBinaryInputs(0, 4);

            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(4, result.Content.Length);
            Assert.True(result.Content[0]);
            Assert.False(result.Content[1]);
            Assert.True(result.Content[2]);
            Assert.False(result.Content[3]);
        }

        [Fact]
        public void ReadBool_ReturnsBinaryInput()
        {
            _server.SetBinaryInput(0, true);

            var result = _client.ReadBool("BI0");

            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.True(result.Content);
        }

        [Fact]
        public void MultipleReads_Succeed()
        {
            for (int i = 0; i < 3; i++)
            {
                var result = _client.ReadInt16("AI0");
                Assert.True(result.IsSuccess, $"Read #{i} failed: {result.Message}");
            }
        }

        [Fact]
        public void ConnectionPool_ReadFloat_ReusesPersistentConnection()
        {
            _server.SetAnalogInput(0, 123.45f);
            using var pool = new Dnp3ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var first = pool.ReadFloat("AI0");
            var second = pool.ReadFloat("AI0");

            Assert.True(first.IsSuccess, first.Message);
            Assert.True(second.IsSuccess, second.Message);
            Assert.Equal(123.45f, first.Content, 2);
            Assert.Equal(123.45f, second.Content, 2);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ReadBinaryInputs_ReturnsPackedValues()
        {
            _server.SetBinaryInput(0, true);
            _server.SetBinaryInput(2, true);
            using var pool = new Dnp3ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = pool.ReadBinaryInputs(0, 4);

            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Content[0]);
            Assert.False(result.Content[1]);
            Assert.True(result.Content[2]);
            Assert.False(result.Content[3]);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_WriteFloat_Succeeds()
        {
            using var pool = new Dnp3ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = pool.Write("AO0", 100.0f);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            _server.SetAnalogInput(0, 1.5f);
            using var pool = new Dnp3ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);
            string? sent = null;
            string? received = null;
            pool.OnMessageSent += (_, message) => sent = message;
            pool.OnMessageReceived += (_, message) => received = message;

            var result = pool.ReadFloat("AI0");

            Assert.True(result.IsSuccess, result.Message);
            Assert.NotNull(sent);
            Assert.NotNull(received);
        }
    }
}
