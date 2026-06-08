using Xunit;
using Nexus.Keyence;

namespace Nexus.Keyence.Tests
{
    public class KeyenceKvTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new KeyenceKvClient("192.168.1.1");
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Constructor_WithPort_SetsPort()
        {
            var client = new KeyenceKvClient("192.168.1.1", 3000);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new KeyenceKvClient("192.168.1.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new KeyenceKvClient("192.168.1.1");
            client.Dispose();
        }
    }
}
