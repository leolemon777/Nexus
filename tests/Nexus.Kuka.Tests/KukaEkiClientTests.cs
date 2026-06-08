using Xunit;
using Nexus.Kuka;

namespace Nexus.Kuka.Tests
{
    public class KukaEkiClientTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new KukaEkiClient("192.168.1.40");
            Assert.Equal("192.168.1.40", client.IpAddress);
            Assert.Equal(54600, client.Port);
            Assert.Equal(5000, client.Timeout);
            Assert.False(client.IsConnected);
            client.Dispose();
        }

        [Fact]
        public void Constructor_CustomPort_SetsCorrectly()
        {
            using var client = new KukaEkiClient("10.0.0.1", 54601, timeout: 3000);
            Assert.Equal("10.0.0.1", client.IpAddress);
            Assert.Equal(54601, client.Port);
            Assert.Equal(3000, client.Timeout);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var client = new KukaEkiClient("127.0.0.1");
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Connect_InvalidHost_Fails()
        {
            using var client = new KukaEkiClient("127.0.0.1", 19999, timeout: 500);
            var result = client.Connect();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public async Task ConnectAsync_InvalidHost_Fails()
        {
            using var client = new KukaEkiClient("127.0.0.1", 19999, timeout: 500);
            var result = await client.ConnectAsync();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Disconnect_WhenNotConnected_DoesNotThrow()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            client.Disconnect();
        }

        [Fact]
        public void IsConnected_BeforeConnect_ReturnsFalse()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Timeout_CanBeSet()
        {
            using var client = new KukaEkiClient("127.0.0.1");
            client.Timeout = 10000;
            Assert.Equal(10000, client.Timeout);
        }

        [Fact]
        public void KukaCartesianPosition_Defaults_AreZero()
        {
            var pos = new KukaCartesianPosition();
            Assert.Equal(0.0, pos.X);
            Assert.Equal(0.0, pos.Y);
            Assert.Equal(0.0, pos.Z);
            Assert.Equal(0.0, pos.A);
            Assert.Equal(0.0, pos.B);
            Assert.Equal(0.0, pos.C);
        }

        [Fact]
        public void KukaCartesianPosition_ToString_ContainsValues()
        {
            var pos = new KukaCartesianPosition { X = 100.5, Y = -200.3, Z = 300.0 };
            var s = pos.ToString();
            Assert.Contains("100.5", s);
            Assert.Contains("-200.3", s);
            Assert.Contains("300", s);
        }

        [Fact]
        public void KukaProgramState_Default_IsNotRunning()
        {
            var state = new KukaProgramState();
            Assert.False(state.IsRunning);
            Assert.False(state.IsPaused);
            Assert.Equal("", state.State);
        }

        [Fact]
        public void KukaProgramState_ToString_WhenRunning()
        {
            var state = new KukaProgramState { IsRunning = true };
            Assert.Equal("RUNNING", state.ToString());
        }

        [Fact]
        public void KukaProgramState_ToString_WhenPaused()
        {
            var state = new KukaProgramState { IsPaused = true };
            Assert.Equal("PAUSED", state.ToString());
        }
    }
}
