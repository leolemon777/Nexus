using Xunit;
using Nexus.Delta;

namespace Nexus.Delta.Tests
{
    public class DeltaDvpTests
    {
        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Constructor_SetsDefaults()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            Assert.True(client.IsConnected); // MemoryStream is always readable+writeable
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Dispose();
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Constructor_NullStream_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new DeltaDvpClient(null!));
        }

        [Fact]
        public void Constructor_WithStation_SetsStation()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms, station: 5);
            Assert.Equal((byte)5, client.Station);
        }

        [Fact]
        public void Station_CanBeSet()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Station = 3;
            Assert.Equal((byte)3, client.Station);
        }

        [Fact]
        public void Timeout_CanBeSet()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Timeout = 10000;
            Assert.Equal(10000, client.Timeout);
        }

        [Fact]
        public void IsConnected_WithOpenStream_ReturnsTrue()
        {
            using var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void IsConnected_AfterDispose_ReturnsFalse()
        {
            var ms = new System.IO.MemoryStream();
            var client = new DeltaDvpClient(ms);
            client.Dispose();
            Assert.False(client.IsConnected);
        }
    }
}
