using Xunit;
using Nexus.Panasonic;

namespace Nexus.Panasonic.Tests
{
    public class PanasonicMewtocolTests
    {
        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new PanasonicMewtocolClient(ms);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new PanasonicMewtocolClient(ms);
            Assert.True(client.IsConnected); // MemoryStream is always readable+writeable
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new PanasonicMewtocolClient(ms);
            client.Dispose();
        }
    }
}
