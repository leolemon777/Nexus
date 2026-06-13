using System.Collections.Generic;
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

        // ── FOCAS Constants ──────────────────────────

        [Fact]
        public void FanucFocasConstants_DefaultPort()
        {
            Assert.Equal(8193, FanucFocasConstants.DefaultPort);
        }

        [Fact]
        public void FanucFocasConstants_MaxAxes()
        {
            Assert.Equal(32, FanucFocasConstants.MaxAxes);
        }

        [Fact]
        public void FanucFocasConstants_MaxSpindles()
        {
            Assert.Equal(8, FanucFocasConstants.MaxSpindles);
        }

        [Theory]
        [InlineData(0, "正常完成")]
        [InlineData(-1, "无效函数")]
        [InlineData(-2, "无效轴号")]
        [InlineData(-3, "无效连接句柄")]
        [InlineData(-11, "Socket 错误")]
        [InlineData(-12, "通讯超时")]
        [InlineData(-13, "连接失败")]
        [InlineData(-999, "未知错误: -999")]
        public void FanucFocasConstants_ToDescription(int code, string expected)
        {
            Assert.Equal(expected, FanucFocasConstants.ToDescription(code));
        }

        // ── Enum coverage ────────────────────────────

        [Fact]
        public void FanucCncModel_Values_Exist()
        {
            Assert.True(System.Enum.IsDefined(typeof(FanucCncModel), FanucCncModel.Series31i));
            Assert.True(System.Enum.IsDefined(typeof(FanucCncModel), FanucCncModel.Series0iD));
            Assert.True(System.Enum.IsDefined(typeof(FanucCncModel), FanucCncModel.Series35i));
        }

        [Fact]
        public void FanucRunMode_Values_Exist()
        {
            Assert.True(System.Enum.IsDefined(typeof(FanucRunMode), FanucRunMode.EmergencyStop));
            Assert.True(System.Enum.IsDefined(typeof(FanucRunMode), FanucRunMode.Running));
            Assert.True(System.Enum.IsDefined(typeof(FanucRunMode), FanucRunMode.Auto));
            Assert.True(System.Enum.IsDefined(typeof(FanucRunMode), FanucRunMode.Edit));
        }

        [Fact]
        public void FanucCoordinateSystem_Values_Exist()
        {
            Assert.Equal(0, (int)FanucCoordinateSystem.Machine);
            Assert.Equal(1, (int)FanucCoordinateSystem.Absolute);
            Assert.Equal(2, (int)FanucCoordinateSystem.Relative);
            Assert.Equal(3, (int)FanucCoordinateSystem.Distance);
        }

        [Fact]
        public void FanucOverrideSource_Values_Exist()
        {
            Assert.Equal(0, (int)FanucOverrideSource.FeedOverride);
            Assert.Equal(1, (int)FanucOverrideSource.RapidOverride);
            Assert.Equal(2, (int)FanucOverrideSource.SpindleOverride);
            Assert.Equal(3, (int)FanucOverrideSource.JogOverride);
        }

        // ── Model defaults ───────────────────────────

        [Fact]
        public void FanucCncInfo_Defaults()
        {
            var info = new FanucCncInfo();
            Assert.Equal(0, info.MaxAxis);
            Assert.Equal("", info.CncType);
            Assert.Equal("", info.MtType);
            Assert.Equal("", info.Series);
            Assert.Equal("", info.Version);
        }

        [Fact]
        public void FanucCncStatus_Defaults()
        {
            var status = new FanucCncStatus();
            Assert.Equal(0, status.Run);
            Assert.Equal(0, status.Motion);
            Assert.False(status.Emergency);
        }

        [Fact]
        public void FanucAlarm_Defaults()
        {
            var alarm = new FanucAlarm();
            Assert.Equal(0, alarm.Code);
            Assert.Equal(0, alarm.Axis);
            Assert.Equal(0, alarm.Type);
        }

        // ── Read/Write not connected ─────────────────

        [Fact]
        public void ReadOperations_NotConnected_ReturnError()
        {
            using var client = new FanucClient("127.0.0.1");
            Assert.False(client.ReadInt16("D100").IsSuccess);
            Assert.False(client.ReadInt32("D100").IsSuccess);
            Assert.False(client.ReadFloat("D100").IsSuccess);
            Assert.False(client.ReadBool("D100").IsSuccess);
            Assert.False(client.ReadString("D100", 10).IsSuccess);
        }

        [Fact]
        public void WriteOperations_NotConnected_ReturnError()
        {
            using var client = new FanucClient("127.0.0.1");
            Assert.False(client.Write("D100", (short)42).IsSuccess);
            Assert.False(client.Write("D100", true).IsSuccess);
        }

        // ── Batch/Subscribe ──────────────────────────

        [Fact]
        public void BatchOperations_EmptyInput_ReturnsError()
        {
            using var client = new FanucClient("127.0.0.1");
            Assert.False(client.BatchRead(new string[0]).IsSuccess);
            Assert.False(client.RandomRead(new string[0]).IsSuccess);
            Assert.False(client.BatchWrite(Array.Empty<KeyValuePair<string, object>>()).IsSuccess);
        }

        [Fact]
        public void Subscribe_Unsubscribe_DoesNotThrow()
        {
            using var client = new FanucClient("127.0.0.1");
            client.Subscribe("D100", 1000, "Int16");
            client.Unsubscribe("D100");
            client.StartSubscriptions();
            client.StopSubscriptions();
        }
    }
}
