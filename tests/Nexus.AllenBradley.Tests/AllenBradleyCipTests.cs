using Xunit;
using Nexus.AllenBradley;

namespace Nexus.AllenBradley.Tests
{
    public class AllenBradleyCipTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new AllenBradleyCipClient("192.168.1.1");
            Assert.Equal("192.168.1.1", client.IpAddress);
            Assert.Equal(44818, client.Port);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Constructor_WithPort_SetsPort()
        {
            var client = new AllenBradleyCipClient("192.168.1.1", 5000);
            Assert.Equal(5000, client.Port);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new AllenBradleyCipClient("192.168.1.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new AllenBradleyCipClient("192.168.1.1");
            client.Dispose();
        }
    }
}
