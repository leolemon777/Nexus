using System.Collections.Generic;
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

        [Fact]
        public void ErrorCodes_Unknown_ReturnsPrefixed()
        {
            var desc = StaubliErrorCodes.GetDescription("some random error");
            Assert.Contains("未知错误", desc);
            Assert.Contains("some random error", desc);
        }

        [Fact]
        public void ErrorCodes_Empty_ReturnsEmptyResponse()
        {
            Assert.Equal("空响应", StaubliErrorCodes.GetDescription(""));
            Assert.Equal("空响应", StaubliErrorCodes.GetDescription(null!));
        }

        [Fact]
        public void ErrorCodes_Protection()
        {
            Assert.Contains("保护停机", StaubliErrorCodes.GetDescription("protection error"));
        }

        [Fact]
        public void ErrorCodes_Busy()
        {
            Assert.Contains("机器人忙", StaubliErrorCodes.GetDescription("robot busy"));
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
            var result = client.MoveJ(new double[] { 1, 2, 3 });
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
        public void Client_MoveJ_NullJoints()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.MoveJ(null!);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_MoveJ_EmptyJoints()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.MoveJ(new double[0]);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_SetDio_NotConnected()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.SetDigitalOutput(1, true);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_GetDio_NotConnected()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.GetDigitalInput(1);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_StopMotion_NotConnected()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.StopMotion();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_DisposeDoesNotThrow()
        {
            var client = new StaubliClient("127.0.0.1");
            client.Dispose();
        }

        [Fact]
        public void Client_ReadOperations_NotConnected()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.ReadInt16("var1").IsSuccess);
            Assert.False(client.ReadInt32("var2").IsSuccess);
            Assert.False(client.ReadFloat("var3").IsSuccess);
            Assert.False(client.ReadBool("var4").IsSuccess);
        }

        [Fact]
        public void Client_WriteOperations_NotConnected()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.Write("var1", (short)42).IsSuccess);
            Assert.False(client.Write("var2", true).IsSuccess);
        }

        [Fact]
        public void Client_BatchOperations_EmptyInput()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.BatchRead(new string[0]).IsSuccess);
            Assert.False(client.RandomRead(new string[0]).IsSuccess);
            Assert.False(client.BatchWrite(Array.Empty<KeyValuePair<string, object>>()).IsSuccess);
        }

        [Fact]
        public void Client_Subscribe_Unsubscribe_DoesNotThrow()
        {
            using var client = new StaubliClient("127.0.0.1");
            client.Subscribe("jointPos", 1000, "Float");
            client.Unsubscribe("jointPos");
            client.StartSubscriptions();
            client.StopSubscriptions();
        }

        [Fact]
        public void Client_SetLogger_DoesNotThrow()
        {
            using var client = new StaubliClient("127.0.0.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Client_DoubleDispose_DoesNotThrow()
        {
            var client = new StaubliClient("127.0.0.1");
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Client_ReadUInt16_NotConnected()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.ReadUInt16("var1").IsSuccess);
        }

        [Fact]
        public void Client_ReadDouble_NotConnected()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.ReadDouble("var1").IsSuccess);
        }

        [Fact]
        public void Client_ReadString_NotConnected()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.ReadString("var1", 10).IsSuccess);
        }

        [Fact]
        public void Client_WriteInt32_NotConnected()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.Write("var1", 42).IsSuccess);
        }

        [Fact]
        public void Client_WriteFloat_NotConnected()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.Write("var1", 3.14f).IsSuccess);
        }

        [Fact]
        public void Client_WriteString_NotConnected()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.Write("var1", "hello").IsSuccess);
        }

        [Fact]
        public void Client_WriteBytes_NotConnected()
        {
            using var client = new StaubliClient("127.0.0.1");
            Assert.False(client.Write("var1", new byte[] { 1, 2 }).IsSuccess);
        }

        [Fact]
        public void Client_MoveL_EmptyPose()
        {
            var client = new StaubliClient("127.0.0.1");
            var result = client.MoveL(new double[0]);
            Assert.False(result.IsSuccess);
        }
    }
}
