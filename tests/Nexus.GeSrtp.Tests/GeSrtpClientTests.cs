using Xunit;
using Nexus.GeSrtp;

namespace Nexus.GeSrtp.Tests
{
    public class GeSrtpClientTests : IDisposable
    {
        private readonly GeSrtpVirtualServer _server;
        private const int TestPort = 18250;

        public GeSrtpClientTests()
        {
            _server = new GeSrtpVirtualServer(TestPort);
        }

        public void Dispose()
        {
            _server?.Dispose();
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new GeSrtpClient("192.168.1.20");
            Assert.Equal("192.168.1.20", client.IpAddress);
            Assert.Equal(18245, client.Port);
            Assert.False(client.IsConnected);
            client.Dispose();
        }

        [Fact]
        public void Constructor_CustomPort_SetsCorrectly()
        {
            using var client = new GeSrtpClient("10.0.0.1", TestPort, timeout: 3000);
            Assert.Equal("10.0.0.1", client.IpAddress);
            Assert.Equal(TestPort, client.Port);
            Assert.Equal(3000, client.Timeout);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var client = new GeSrtpClient("127.0.0.1", TestPort);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var client = new GeSrtpClient("127.0.0.1", TestPort);
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Connect_InvalidHost_Fails()
        {
            using var client = new GeSrtpClient("127.0.0.1", 19999, timeout: 500);
            var result = client.Connect();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Connect_ToVirtualServer_Succeeds()
        {
            _server.Start();
            using var client = new GeSrtpClient("127.0.0.1", TestPort);
            var result = client.Connect();
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void ReadInt16_FromVirtualServer_ReturnsValue()
        {
            _server.Start();
            _server.SetRWord(100, 12345);
            using var client = new GeSrtpClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadInt16("R100");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((short)12345, result.Content);
        }

        [Fact]
        public void WriteInt16_ToVirtualServer_Succeeds()
        {
            _server.Start();
            using var client = new GeSrtpClient("127.0.0.1", TestPort);
            client.Connect();

            var writeResult = client.Write("R200", (short)-5000);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadInt16("R200");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((short)-5000, readResult.Content);
        }

        [Fact]
        public void ReadUInt16_FromVirtualServer_ReturnsValue()
        {
            _server.Start();
            _server.SetRWord(50, 60000);
            using var client = new GeSrtpClient("127.0.0.1", TestPort);
            client.Connect();

            var result = client.ReadUInt16("R50");
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)60000, result.Content);
        }

        [Fact]
        public void WriteUInt16_ToVirtualServer_Succeeds()
        {
            _server.Start();
            using var client = new GeSrtpClient("127.0.0.1", TestPort);
            client.Connect();

            var writeResult = client.Write("R300", (ushort)54321);
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            var readResult = client.ReadUInt16("R300");
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal((ushort)54321, readResult.Content);
        }

        [Fact]
        public void Disconnect_AfterConnect_Succeeds()
        {
            _server.Start();
            using var client = new GeSrtpClient("127.0.0.1", TestPort);
            client.Connect();
            Assert.True(client.IsConnected);

            client.Disconnect();
            Assert.False(client.IsConnected);
        }
    }
}
