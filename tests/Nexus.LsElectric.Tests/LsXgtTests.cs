using Xunit;
using Nexus.LsElectric;

namespace Nexus.LsElectric.Tests
{
    public class LsXgtTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new LsXgtClient("192.168.1.1");
            Assert.Equal("192.168.1.1", client.IpAddress);
            Assert.Equal(2004, client.Port);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new LsXgtClient("192.168.1.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new LsXgtClient("192.168.1.1");
            client.Dispose();
        }
    }
}
