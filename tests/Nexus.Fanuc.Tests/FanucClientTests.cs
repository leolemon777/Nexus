using Xunit;
using Nexus.Fanuc;

namespace Nexus.Fanuc.Tests
{
    public class FanucClientTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new FanucClient("192.168.1.30");
            Assert.Equal("192.168.1.30", client.IpAddress);
            Assert.Equal(8193, client.Port);
            Assert.Equal(5000, client.Timeout);
            Assert.False(client.IsConnected);
            client.Dispose();
        }

        [Fact]
        public void Constructor_CustomPort_SetsCorrectly()
        {
            using var client = new FanucClient("10.0.0.1", 8194, timeout: 3000);
            Assert.Equal("10.0.0.1", client.IpAddress);
            Assert.Equal(8194, client.Port);
            Assert.Equal(3000, client.Timeout);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            using var client = new FanucClient("127.0.0.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var client = new FanucClient("127.0.0.1");
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Connect_InvalidHost_Fails()
        {
            using var client = new FanucClient("127.0.0.1", 19999, timeout: 500);
            var result = client.Connect();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public async Task ConnectAsync_InvalidHost_Fails()
        {
            using var client = new FanucClient("127.0.0.1", 19999, timeout: 500);
            var result = await client.ConnectAsync();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Disconnect_WhenNotConnected_DoesNotThrow()
        {
            using var client = new FanucClient("127.0.0.1");
            client.Disconnect();
        }

        [Fact]
        public void IsConnected_BeforeConnect_ReturnsFalse()
        {
            using var client = new FanucClient("127.0.0.1");
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Timeout_CanBeSet()
        {
            using var client = new FanucClient("127.0.0.1");
            client.Timeout = 10000;
            Assert.Equal(10000, client.Timeout);
        }

        [Fact]
        public void FanucCncInfo_ToString_ContainsFields()
        {
            var info = new FanucCncInfo { MaxAxis = 3, CncType = "31i", MtType = "M", Series = "A", Version = "04.01" };
            var s = info.ToString();
            Assert.Contains("31i", s);
            Assert.Contains("3", s);
        }

        [Fact]
        public void FanucCncStatus_RunDescription_MapsCorrectly()
        {
            var status = new FanucCncStatus { Run = 3, Motion = 1, Mstb = 0, Emergency = false };
            Assert.Equal("START", status.RunDescription);
            var s = status.ToString();
            Assert.Contains("START", s);
            Assert.Contains("Emergency=False", s);
        }

        [Fact]
        public void FanucCncStatus_RunReset_ReturnsReset()
        {
            var status = new FanucCncStatus { Run = 0 };
            Assert.Equal("RESET", status.RunDescription);
        }

        [Fact]
        public void FanucAlarm_ToString_ContainsCode()
        {
            var alarm = new FanucAlarm { Code = 1001, Axis = 2, Type = 1 };
            var s = alarm.ToString();
            Assert.Contains("1001", s);
            Assert.Contains("Axis=2", s);
            Assert.Contains("Type=1", s);
        }
    }
}
