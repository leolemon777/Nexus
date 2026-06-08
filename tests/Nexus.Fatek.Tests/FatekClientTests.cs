using Xunit;
using Nexus.Fatek;

namespace Nexus.Fatek.Tests
{
    public class FatekClientTests : IDisposable
    {
        private readonly FatekVirtualServer _server;
        private const int TestPort = 15001;

        public FatekClientTests()
        {
            _server = new FatekVirtualServer(TestPort);
        }

        public void Dispose()
        {
            _server?.Dispose();
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new FatekClient("127.0.0.1", TestPort);
            Assert.Equal("127.0.0.1", client.IpAddress);
            Assert.Equal(TestPort, client.Port);
            Assert.Equal((byte)1, client.Station);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new FatekClient("127.0.0.1", TestPort);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new FatekClient("127.0.0.1", TestPort);
            client.Dispose();
        }

        [Fact]
        public void Connect_ToVirtualServer_Succeeds()
        {
            _server.Start();
            using var client = new FatekClient("127.0.0.1", TestPort);
            var result = client.Connect();
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void Connect_InvalidPort_Fails()
        {
            using var client = new FatekClient("127.0.0.1", 19999, station: 1, timeout: 1000);
            var result = client.Connect();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void ReadInt16_FromVirtualServer_ReturnsValue()
        {
            _server.Start();
            _server.SetDWord(100, 12345);
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadInt16("D100");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)12345, result.Content);
        }

        [Fact]
        public void WriteInt16_ToVirtualServer_Succeeds()
        {
            _server.Start();
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var writeResult = client.Write("D200", (short)12345);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("D200");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)12345, readResult.Content);
        }

        [Fact]
        public void ReadUInt16_FromVirtualServer_ReturnsValue()
        {
            _server.Start();
            _server.SetDWord(50, 60000);
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadUInt16("D50");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)60000, result.Content);
        }

        [Fact]
        public void WriteUInt16_ToVirtualServer_Succeeds()
        {
            _server.Start();
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var writeResult = client.Write("D300", (ushort)54321);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadUInt16("D300");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((ushort)54321, readResult.Content);
        }

        [Fact]
        public void ReadBools_FromVirtualServer_ReturnsValues()
        {
            _server.Start();
            _server.SetBit('Y', 0, true);
            _server.SetBit('Y', 1, false);
            _server.SetBit('Y', 2, true);
            _server.SetBit('Y', 3, false);

            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadBools("Y0", 4);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(4, result.Content.Length);
            Assert.True(result.Content[0]);
            Assert.False(result.Content[1]);
            Assert.True(result.Content[2]);
            Assert.False(result.Content[3]);
        }

        [Fact]
        public void ReadPlcStatus_FromVirtualServer_ReturnsStopped()
        {
            _server.Start();
            using var client = new FatekClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadPlcStatus();
            Assert.True(result.IsSuccess, result.Message);
            // 默认状态为 STOP
            Assert.False(result.Content);
        }
    }
}
