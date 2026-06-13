using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nexus.Iec61850;

namespace Nexus.Iec61850.Tests
{
    public class Iec61850IntegrationTests : IDisposable
    {
        private readonly Iec61850VirtualServer _server;
        private readonly Iec61850Client _client;

        public Iec61850IntegrationTests()
        {
            _server = new Iec61850VirtualServer(0);
            _server.Start();
            Thread.Sleep(100);
            _client = new Iec61850Client("127.0.0.1", _server.Port);
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
            _server.SetDataMemory(0, BitConverter.GetBytes(42.5f));
            var result = _client.ReadFloat("LD0/LLN0.GGIO1.AnIn1");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(42.5f, result.Content, 1);
        }

        [Fact]
        public void ReadBool_ReturnsValue()
        {
            _server.SetDataMemory(0, new byte[] { 0x01 });
            var result = _client.ReadBool("LD0/LLN0.GGIO1.Ind1");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.True(result.Content);
        }

        [Fact]
        public void WriteFloat_Succeeds()
        {
            var result = _client.Write("LD0/LLN0.GGIO1.AnOut1", 100.0f);
            Assert.True(result.IsSuccess, result.Message ?? "Write failed");

            var read = _client.ReadFloat("LD0/LLN0.GGIO1.AnOut1");
            Assert.True(read.IsSuccess, read.Message ?? "Read after write failed");
            Assert.Equal(100.0f, read.Content, 1);
        }

        [Fact]
        public void WriteBool_Succeeds()
        {
            var result = _client.Write("LD0/LLN0.GGIO1.Cmd1", true);
            Assert.True(result.IsSuccess, result.Message ?? "Write failed");
        }

        [Fact]
        public void ReadInt16_Succeeds()
        {
            _server.SetDataMemory(0, BitConverter.GetBytes((short)1234));
            var result = _client.ReadInt16("LD0/LLN0.GGIO1.AnIn2");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal((short)1234, result.Content);
        }

        [Fact]
        public void ReadDouble_ReturnsValue()
        {
            _server.SetDataMemory(0, BitConverter.GetBytes(123.456));
            var result = _client.ReadDouble("LD0/LLN0.GGIO1.AnIn3");
            Assert.True(result.IsSuccess, result.Message ?? "Read failed");
            Assert.Equal(123.456, result.Content, 3);
        }

        [Fact]
        public void MultipleReads_Succeed()
        {
            for (int i = 0; i < 3; i++)
            {
                var result = _client.ReadInt16("LD0/LLN0.GGIO1.AnIn1");
                Assert.True(result.IsSuccess, $"Read #{i} failed: {result.Message}");
            }
        }

        [Fact]
        public void ConnectionPool_ReadFloat_ReusesPersistentConnection()
        {
            _server.SetDataMemory(0, BitConverter.GetBytes(42.5f));
            using var pool = new Iec61850ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            for (int i = 0; i < 3; i++)
            {
                var result = pool.ReadFloat("LD0/LLN0.GGIO1.AnIn1");
                Assert.True(result.IsSuccess, result.Message ?? "Pool read failed");
                Assert.Equal(42.5f, result.Content, 1);
            }

            WaitForConnections(1);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_WriteFloat_Succeeds()
        {
            using var pool = new Iec61850ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var write = pool.Write("LD0/LLN0.GGIO1.AnOut1", 100.0f);
            Assert.True(write.IsSuccess, write.Message ?? "Pool write failed");

            var read = pool.ReadFloat("LD0/LLN0.GGIO1.AnOut1");
            Assert.True(read.IsSuccess, read.Message ?? "Pool read after write failed");
            Assert.Equal(100.0f, read.Content, 1);

            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_BatchRead_ReturnsValues()
        {
            _server.SetDataMemory(0, BitConverter.GetBytes((short)1234));
            using var pool = new Iec61850ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = pool.BatchRead(new[]
            {
                "LD0/LLN0.GGIO1.AnIn1",
                "LD0/LLN0.GGIO1.AnIn2"
            });

            Assert.True(result.IsSuccess, result.Message ?? "Pool batch read failed");
            Assert.Equal(2, result.Content.Count);
            Assert.Equal((short)1234, Assert.IsType<short>(result.Content["LD0/LLN0.GGIO1.AnIn1"]));
            Assert.Equal((short)1234, Assert.IsType<short>(result.Content["LD0/LLN0.GGIO1.AnIn2"]));

            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public async Task ConnectionPool_ExecuteAsync_ReadFloat_ReusesPersistentConnection()
        {
            _server.SetDataMemory(0, BitConverter.GetBytes(12.25f));
            using var pool = new Iec61850ConnectionPool("127.0.0.1", _server.Port, maxPoolSize: 1);

            var result = await pool.ExecuteAsync(c => c.ReadFloatAsync("LD0/LLN0.GGIO1.AnIn1"));

            Assert.True(result.IsSuccess, result.Message ?? "Pool async read failed");
            Assert.Equal(12.25f, result.Content, 2);

            WaitForConnections(1);
            Assert.Equal(1, _server.ConnectionCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsMessageEvents()
        {
            _server.SetDataMemory(0, new byte[] { 0x01 });
            using var pool = new Iec61850ConnectionPool("127.0.0.1", _server.Port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, __) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, __) => Interlocked.Increment(ref received);

            var result = pool.ReadBool("LD0/LLN0.GGIO1.Ind1");

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
