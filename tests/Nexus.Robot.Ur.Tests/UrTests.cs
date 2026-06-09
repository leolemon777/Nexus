using Xunit;
using Nexus.Robot.Ur;

namespace Nexus.Robot.Ur.Tests
{
    public class UrModelTests
    {
        [Theory]
        [InlineData(UrModel.UR3e)]
        [InlineData(UrModel.UR5e)]
        [InlineData(UrModel.UR10e)]
        [InlineData(UrModel.UR16e)]
        [InlineData(UrModel.UR3)]
        [InlineData(UrModel.UR5)]
        [InlineData(UrModel.UR10)]
        public void UrModel_AllDefined(UrModel model)
        {
            Assert.True(Enum.IsDefined(typeof(UrModel), model));
        }

        [Fact]
        public void UrConstants_Ports()
        {
            Assert.Equal(30001, UrConstants.PrimaryPort);
            Assert.Equal(30002, UrConstants.SecondaryPort);
            Assert.Equal(30003, UrConstants.RealTimePort);
            Assert.Equal(29999, UrConstants.DashboardPort);
        }

        [Fact]
        public void UrConstants_JointCount()
        {
            Assert.Equal(6, UrConstants.JointCount);
        }

        [Fact]
        public void UrConstants_DashboardCommands()
        {
            Assert.Equal("play\n", UrConstants.CmdPlay);
            Assert.Equal("pause\n", UrConstants.CmdPause);
            Assert.Equal("stop\n", UrConstants.CmdStop);
            Assert.Equal("running\n", UrConstants.CmdRunning);
            Assert.Equal("robotmode\n", UrConstants.CmdRobotMode);
            Assert.Equal("brake release\n", UrConstants.CmdBrakeRelease);
            Assert.Equal("shutdown\n", UrConstants.CmdShutdown);
        }

        [Theory]
        [InlineData(UrCoordinateSystem.Base)]
        [InlineData(UrCoordinateSystem.Tool)]
        [InlineData(UrCoordinateSystem.Custom)]
        public void CoordinateSystem_AllDefined(UrCoordinateSystem cs)
        {
            Assert.True(Enum.IsDefined(typeof(UrCoordinateSystem), cs));
        }

        [Theory]
        [InlineData(UrRunMode.Stopped)]
        [InlineData(UrRunMode.Running)]
        [InlineData(UrRunMode.Paused)]
        public void RunMode_AllDefined(UrRunMode mode)
        {
            Assert.True(Enum.IsDefined(typeof(UrRunMode), mode));
        }

        [Fact]
        public void UrErrorCodes_CommonResponses()
        {
            Assert.Contains("程序正在运行", UrErrorCodes.GetDescription("Program running"));
            Assert.Contains("程序未运行", UrErrorCodes.GetDescription("Program not running"));
            Assert.Contains("保护停机", UrErrorCodes.GetDescription("Protective stopped"));
            Assert.Contains("急停", UrErrorCodes.GetDescription("Emergency stopped"));
        }
    }

    public class UrClientTests
    {
        [Fact]
        public void UrClient_DefaultPorts()
        {
            var client = new UrClient("192.168.1.100");
            Assert.Equal("192.168.1.100", client.IpAddress);
            Assert.Equal(30002, client.ScriptPort);
            Assert.Equal(29999, client.DashboardPort);
            Assert.Equal(30003, client.RealTimePort);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void UrClient_CustomPorts()
        {
            var client = new UrClient("10.0.0.1", scriptPort: 30004, dashboardPort: 29998);
            Assert.Equal(30004, client.ScriptPort);
            Assert.Equal(29998, client.DashboardPort);
        }

        [Fact]
        public void UrClient_NullIpThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new UrClient(null!));
        }

        [Fact]
        public void UrClient_SendScriptNotConnected()
        {
            var client = new UrClient("127.0.0.1");
            var result = client.SendScript("movej([0,0,0,0,0,0])");
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void UrClient_SendScriptEmptyFails()
        {
            var client = new UrClient("127.0.0.1");
            var result = client.SendScript("");
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void UrClient_MoveL_InvalidPose()
        {
            var client = new UrClient("127.0.0.1");
            var result = client.MoveL(new double[] { 1, 2, 3 }); // only 3 elements
            Assert.False(result.IsSuccess);
            Assert.Contains("6", result.Message);
        }

        [Fact]
        public void UrClient_MoveJ_InvalidPose()
        {
            var client = new UrClient("127.0.0.1");
            var result = client.MoveJ(null!);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void UrClient_ReadBytesNotSupported()
        {
            var client = new UrClient("127.0.0.1");
            var result = client.ReadBytes("D100", 1);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void UrClient_DisposeDoesNotThrow()
        {
            var client = new UrClient("127.0.0.1");
            client.Dispose();
        }

        [Fact]
        public void UrClient_DashboardNotConnected()
        {
            var client = new UrClient("127.0.0.1");
            var result = client.SendDashboardCommand("running\n");
            Assert.False(result.IsSuccess);
        }
    }
}
