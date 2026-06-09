using Xunit;
using Nexus.Fanuc;

namespace Nexus.Fanuc.Tests
{
    public class FanucFocasModelTests
    {
        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(8193, FanucFocasConstants.DefaultPort);
            Assert.Equal(32, FanucFocasConstants.MaxAxes);
            Assert.Equal(0, FanucFocasConstants.EwOk);
        }

        [Theory]
        [InlineData(0, "正常完成")]
        [InlineData(-1, "无效函数")]
        [InlineData(-2, "无效轴号")]
        [InlineData(-3, "无效连接句柄")]
        [InlineData(-12, "通讯超时")]
        [InlineData(-13, "连接失败")]
        [InlineData(-99, "未知错误: -99")]
        public void ErrorCodes_Description(int code, string expected)
        {
            Assert.Equal(expected, FanucFocasConstants.ToDescription(code));
        }

        [Fact]
        public void Models_EnumValues()
        {
            Assert.True(Enum.IsDefined(typeof(FanucCncModel), FanucCncModel.Series30i));
            Assert.True(Enum.IsDefined(typeof(FanucCncModel), FanucCncModel.Series0iD));
            Assert.True(Enum.IsDefined(typeof(FanucCncModel), FanucCncModel.Series0iF));
        }

        [Fact]
        public void CoordinateSystem_Values()
        {
            Assert.Equal(0, (int)FanucCoordinateSystem.Machine);
            Assert.Equal(1, (int)FanucCoordinateSystem.Absolute);
            Assert.Equal(2, (int)FanucCoordinateSystem.Relative);
        }
    }

    public class FanucFocasVirtualServerTests
    {
        [Fact]
        public void Server_StartsAndStops()
        {
            using var server = new FanucFocasVirtualServer(0);
            server.Start();
            Assert.True(server.IsRunning);
            server.Stop();
        }

        [Fact]
        public void SetGetRegister()
        {
            using var server = new FanucFocasVirtualServer(0);
            server.SetRegister(100, 0x1234);
            Assert.Equal(0x1234, server.GetRegister(100));
        }

        [Fact]
        public void AxisPositions_Settable()
        {
            using var server = new FanucFocasVirtualServer(0);
            server.AxisX = 100.5;
            server.AxisY = -50.3;
            server.AxisZ = 25.0;
            Assert.Equal(100.5, server.AxisX);
            Assert.Equal(-50.3, server.AxisY);
            Assert.Equal(25.0, server.AxisZ);
        }
    }
}
