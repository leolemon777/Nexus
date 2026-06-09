using Xunit;
using Nexus.Panasonic;

namespace Nexus.Panasonic.Tests
{
    public class PanasonicMewtocolModelTests
    {
        [Fact]
        public void Area_EnumValues()
        {
            Assert.True(Enum.IsDefined(typeof(PanasonicArea), PanasonicArea.DataRegister));
            Assert.True(Enum.IsDefined(typeof(PanasonicArea), PanasonicArea.OutputCoil));
            Assert.True(Enum.IsDefined(typeof(PanasonicArea), PanasonicArea.InputDiscrete));
            Assert.True(Enum.IsDefined(typeof(PanasonicArea), PanasonicArea.TimerCoil));
            Assert.True(Enum.IsDefined(typeof(PanasonicArea), PanasonicArea.CounterCoil));
            Assert.True(Enum.IsDefined(typeof(PanasonicArea), PanasonicArea.TimerValue));
            Assert.True(Enum.IsDefined(typeof(PanasonicArea), PanasonicArea.CounterValue));
        }

        [Fact]
        public void Constants_ControlChars()
        {
            Assert.Equal(0x02, PanasonicMewtocolConstants.STX);
            Assert.Equal(0x03, PanasonicMewtocolConstants.ETX);
            Assert.Equal(0x05, PanasonicMewtocolConstants.ENQ);
            Assert.Equal(0x06, PanasonicMewtocolConstants.ACK);
            Assert.Equal(0x15, PanasonicMewtocolConstants.NAK);
            Assert.Equal(0x04, PanasonicMewtocolConstants.EOT);
        }

        [Fact]
        public void Constants_CommandStrings()
        {
            Assert.Equal("RCS", PanasonicMewtocolConstants.CmdRead);
            Assert.Equal("RCC", PanasonicMewtocolConstants.CmdReadMulti);
            Assert.Equal("RD", PanasonicMewtocolConstants.CmdReadWord);
            Assert.Equal("RDW", PanasonicMewtocolConstants.CmdReadWordMulti);
            Assert.Equal("WCS", PanasonicMewtocolConstants.CmdWrite);
            Assert.Equal("WCC", PanasonicMewtocolConstants.CmdWriteMulti);
            Assert.Equal("WD", PanasonicMewtocolConstants.CmdWriteWord);
            Assert.Equal("WDW", PanasonicMewtocolConstants.CmdWriteWordMulti);
        }

        [Fact]
        public void Constants_Ports()
        {
            Assert.Equal(9094, PanasonicMewtocolConstants.DefaultTcpPort);
            Assert.Equal(9600, PanasonicMewtocolConstants.DefaultBaudRate);
        }

        [Theory]
        [InlineData("", "正常完成")]
        [InlineData("!", "未定义命令")]
        [InlineData("\"", "不支持的命令")]
        [InlineData("#", "PLC 忙（运行模式）")]
        [InlineData(")", "地址错误 — 超出范围")]
        [InlineData("(", "写入不允许（PLC 处于运行状态）")]
        [InlineData("?", "未知错误: ?")]
        public void ErrorCodes_Description(string code, string expected)
        {
            Assert.Equal(expected, PanasonicErrorCodes.ToDescription(code));
        }

        [Theory]
        [InlineData(PanasonicFpModel.Fp0)]
        [InlineData(PanasonicFpModel.FpSigma)]
        [InlineData(PanasonicFpModel.Fp2)]
        [InlineData(PanasonicFpModel.Fp5)]
        [InlineData(PanasonicFpModel.FpX0)]
        public void FpModel_EnumValues(PanasonicFpModel model)
        {
            Assert.True(Enum.IsDefined(typeof(PanasonicFpModel), model));
        }
    }
}
