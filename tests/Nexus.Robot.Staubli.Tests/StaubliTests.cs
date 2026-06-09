using Xunit;
using Nexus.Robot.Staubli;

namespace Nexus.Robot.Staubli.Tests
{
    public class StaubliModelTests
    {
        [Theory]
        [InlineData(StaubliModel.TX2_40)]
        [InlineData(StaubliModel.TX2_60)]
        [InlineData(StaubliModel.TX2_90)]
        [InlineData(StaubliModel.TX2_160)]
        [InlineData(StaubliModel.TS2_40)]
        [InlineData(StaubliModel.TS2_100)]
        [InlineData(StaubliModel.CS8)]
        [InlineData(StaubliModel.CS9)]
        public void StaubliModel_AllDefined(StaubliModel model)
        {
            Assert.True(Enum.IsDefined(typeof(StaubliModel), model));
        }

        [Fact]
        public void StaubliConstants_Ports()
        {
            Assert.Equal(59000, StaubliConstants.CommandPort);
            Assert.Equal(8080, StaubliConstants.UniValPort);
            Assert.Equal(6, StaubliConstants.JointCount);
        }

        [Fact]
        public void StaubliConstants_Commands()
        {
            Assert.Equal("movej", StaubliConstants.CmdMoveJ);
            Assert.Equal("set", StaubliConstants.CmdSetDio);
            Assert.Equal("get", StaubliConstants.CmdGetDio);
            Assert.Equal("stop", StaubliConstants.CmdStop);
            Assert.Equal("delay", StaubliConstants.CmdDelay);
        }

        [Fact]
        public void StaubliConstants_Responses()
        {
            Assert.Equal("OK", StaubliConstants.ResponseOk);
            Assert.Equal("ERROR", StaubliConstants.ResponseError);
        }

        [Theory]
        [InlineData(StaubliMotionMode.Joint)]
        [InlineData(StaubliMotionMode.Linear)]
        [InlineData(StaubliMotionMode.Circular)]
        public void MotionMode_AllDefined(StaubliMotionMode mode)
        {
            Assert.True(Enum.IsDefined(typeof(StaubliMotionMode), mode));
        }

        [Theory]
        [InlineData(StaubliIoType.DigitalInput)]
        [InlineData(StaubliIoType.DigitalOutput)]
        [InlineData(StaubliIoType.AnalogInput)]
        [InlineData(StaubliIoType.AnalogOutput)]
        public void IoType_AllDefined(StaubliIoType type)
        {
            Assert.True(Enum.IsDefined(typeof(StaubliIoType), type));
        }

        [Fact]
        public void ErrorCodes_Common()
        {
            Assert.Equal("正常完成", StaubliErrorCodes.GetDescription("0"));
            Assert.Contains("语法错误", StaubliErrorCodes.GetDescription("syntax error"));
            Assert.Contains("未定义", StaubliErrorCodes.GetDescription("undefined variable"));
            Assert.Contains("急停", StaubliErrorCodes.GetDescription("emergency stop"));
            Assert.Contains("碰撞", StaubliErrorCodes.GetDescription("collision detected"));
            Assert.Contains("超限", StaubliErrorCodes.GetDescription("limit exceeded"));
        }
    }

    public class StaubliClientTests
    {
        [Fact]
        public void Client_DefaultPort()
        {
            var client = new StaubliClient("192.168.1.50");
            Assert.Equal("192.168.1.50", client.IpAddress);
            Assert.Equal(59000, client.Port);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Client_CustomPort()
        {
            var client = new StaubliClient("10.0.0.1", 8080);
            Assert.Equal(8080, client.Port);
        }

        [Fact]
        public void Client_NullIpThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new StaubliClient(null!));
        }

        [Fact]
        public void Client_SendNotConnected()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.SendCommand("movej()");
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_SendEmptyFails()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.SendCommand("");
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_MoveJ_InvalidJoints()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.MoveJ(new double[] { 1, 2, 3 }); // only 3 elements
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_MoveL_InvalidPose()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.MoveL(null!);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_DisposeDoesNotThrow()
        {
            var client = new StaubliClient("127.0.0.1");
            client.Dispose();
        }
    }
}
