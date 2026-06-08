using Xunit;
using Nexus.Fuji;

namespace Nexus.Fuji.Tests
{
    public class FujiSphTests
    {
        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new FujiSphClient(ms);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new FujiSphClient(ms);
            Assert.True(client.IsConnected); // MemoryStream is always readable+writeable
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new FujiSphClient(ms);
            client.Dispose();
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new FujiSphClient(ms);
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Constructor_NullStream_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new FujiSphClient(null!));
        }

        [Fact]
        public void Constructor_WithStation_SetsStation()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new FujiSphClient(ms, station: 3);
            Assert.Equal((byte)3, client.Station);
        }

        [Fact]
        public void Station_CanBeSet()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new FujiSphClient(ms);
            client.Station = 7;
            Assert.Equal((byte)7, client.Station);
        }

        [Fact]
        public void Timeout_CanBeSet()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new FujiSphClient(ms);
            client.Timeout = 10000;
            Assert.Equal(10000, client.Timeout);
        }

        [Fact]
        public void IsConnected_WithOpenStream_ReturnsTrue()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new FujiSphClient(ms);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void IsConnected_AfterDispose_ReturnsFalse()
        {
            var ms = new System.IO.MemoryStream();
            var client = new FujiSphClient(ms);
            client.Dispose();
            Assert.False(client.IsConnected);
        }
    }
}
