using Xunit;
using Nexus.Beckhoff;

namespace Nexus.Beckhoff.Tests
{
    public class BeckhoffAdsTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            Assert.Equal("192.168.1.1", client.IpAddress);
            Assert.Equal(48898, client.Port);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Constructor_SetsNetIds()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            Assert.Equal("127.0.0.1.1.1", client.LocalNetId);
            Assert.Equal("192.168.1.1.1.1", client.TargetNetId);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            client.Dispose();
        }
    }
}
