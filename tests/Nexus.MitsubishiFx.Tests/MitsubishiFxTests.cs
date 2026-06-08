using Xunit;
using Nexus.MitsubishiFx;

namespace Nexus.MitsubishiFx.Tests
{
    public class MitsubishiFxTests
    {
        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new MitsubishiFxSerialClient(ms);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new MitsubishiFxSerialClient(ms);
            Assert.True(client.IsConnected); // MemoryStream is always readable+writeable
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new MitsubishiFxSerialClient(ms);
            client.Dispose();
        }
    }
}
