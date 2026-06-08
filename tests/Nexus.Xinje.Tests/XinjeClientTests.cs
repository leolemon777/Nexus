using Xunit;
using Nexus.Xinje;

namespace Nexus.Xinje.Tests
{
    public class XinjeClientTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new XinjeClient("192.168.1.10");
            Assert.Equal("192.168.1.10", client.IpAddress);
            Assert.Equal(502, client.Port);
            Assert.Equal((byte)1, client.Station);
            Assert.False(client.IsConnected);
            client.Dispose();
        }

        [Fact]
        public void Constructor_CustomPort_SetsCorrectly()
        {
            using var client = new XinjeClient("10.0.0.1", 503, station: 2, timeout: 3000);
            Assert.Equal("10.0.0.1", client.IpAddress);
            Assert.Equal(503, client.Port);
            Assert.Equal((byte)2, client.Station);
            Assert.Equal(3000, client.Timeout);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var client = new XinjeClient("127.0.0.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var client = new XinjeClient("127.0.0.1");
            client.Dispose();
            client.Dispose(); // second call should be safe
        }

        [Fact]
        public void Connect_InvalidHost_Fails()
        {
            using var client = new XinjeClient("127.0.0.1", 19999, station: 1, timeout: 500);
            var result = client.Connect();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public async Task ConnectAsync_InvalidHost_Fails()
        {
            using var client = new XinjeClient("127.0.0.1", 19999, station: 1, timeout: 500);
            var result = await client.ConnectAsync();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Disconnect_WhenNotConnected_DoesNotThrow()
        {
            using var client = new XinjeClient("127.0.0.1");
            client.Disconnect(); // should not throw
        }

        [Fact]
        public void IsConnected_BeforeConnect_ReturnsFalse()
        {
            using var client = new XinjeClient("127.0.0.1");
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Station_CanBeSet()
        {
            using var client = new XinjeClient("127.0.0.1");
            client.Station = 5;
            Assert.Equal((byte)5, client.Station);
        }

        [Fact]
        public void Timeout_CanBeSet()
        {
            using var client = new XinjeClient("127.0.0.1");
            client.Timeout = 10000;
            Assert.Equal(10000, client.Timeout);
        }
    }
}
