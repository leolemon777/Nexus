using Xunit;
using Nexus.GeSrtp;

namespace Nexus.GeSrtp.Tests
{
    public class GeSrtpAddressTests
    {
        // ═══════════════════════════════════════════
        //  寄存器区域解析
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_R100()
        {
            var addr = GeSrtpAddress.TryParse("R100");
            Assert.NotNull(addr);
            Assert.Equal((byte)0x08, addr.MemoryType);
            Assert.Equal(GeSrtpArea.Register, addr.Area);
            Assert.Equal(100, addr.Offset);
        }

        [Fact]
        public void TryParse_PercentR100()
        {
            var addr = GeSrtpAddress.TryParse("%R100");
            Assert.NotNull(addr);
            Assert.Equal((byte)0x08, addr.MemoryType);
            Assert.Equal(100, addr.Offset);
        }

        [Fact]
        public void TryParse_R0()
        {
            var addr = GeSrtpAddress.TryParse("R0");
            Assert.NotNull(addr);
            Assert.Equal(GeSrtpArea.Register, addr.Area);
            Assert.Equal(0, addr.Offset);
        }

        [Fact]
        public void TryParse_R32767()
        {
            var addr = GeSrtpAddress.TryParse("R32767");
            Assert.NotNull(addr);
            Assert.Equal(GeSrtpArea.Register, addr.Area);
            Assert.Equal(32767, addr.Offset);
        }

        // ═══════════════════════════════════════════
        //  AI/AQ 双字符前缀
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_AI10()
        {
            var addr = GeSrtpAddress.TryParse("AI10");
            Assert.NotNull(addr);
            Assert.Equal((byte)0x0A, addr.MemoryType);
            Assert.Equal(GeSrtpArea.AnalogInput, addr.Area);
            Assert.Equal(10, addr.Offset);
        }

        [Fact]
        public void TryParse_PercentAI20()
        {
            var addr = GeSrtpAddress.TryParse("%AI20");
            Assert.NotNull(addr);
            Assert.Equal(GeSrtpArea.AnalogInput, addr.Area);
            Assert.Equal(20, addr.Offset);
        }

        [Fact]
        public void TryParse_AQ5()
        {
            var addr = GeSrtpAddress.TryParse("AQ5");
            Assert.NotNull(addr);
            Assert.Equal((byte)0x0C, addr.MemoryType);
            Assert.Equal(GeSrtpArea.AnalogOutput, addr.Area);
            Assert.Equal(5, addr.Offset);
        }

        [Fact]
        public void TryParse_PercentAQ100()
        {
            var addr = GeSrtpAddress.TryParse("%AQ100");
            Assert.NotNull(addr);
            Assert.Equal(GeSrtpArea.AnalogOutput, addr.Area);
            Assert.Equal(100, addr.Offset);
        }

        // ═══════════════════════════════════════════
        //  离散量区域
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_I50()
        {
            var addr = GeSrtpAddress.TryParse("I50");
            Assert.NotNull(addr);
            Assert.Equal((byte)0x10, addr.MemoryType);
            Assert.Equal(GeSrtpArea.DiscreteInput, addr.Area);
            Assert.Equal(50, addr.Offset);
        }

        [Fact]
        public void TryParse_Q30()
        {
            var addr = GeSrtpAddress.TryParse("Q30");
            Assert.NotNull(addr);
            Assert.Equal((byte)0x12, addr.MemoryType);
            Assert.Equal(GeSrtpArea.DiscreteOutput, addr.Area);
            Assert.Equal(30, addr.Offset);
        }

        [Fact]
        public void TryParse_PercentI100()
        {
            var addr = GeSrtpAddress.TryParse("%I100");
            Assert.NotNull(addr);
            Assert.Equal(GeSrtpArea.DiscreteInput, addr.Area);
            Assert.Equal(100, addr.Offset);
        }

        [Fact]
        public void TryParse_PercentQ200()
        {
            var addr = GeSrtpAddress.TryParse("%Q200");
            Assert.NotNull(addr);
            Assert.Equal(GeSrtpArea.DiscreteOutput, addr.Area);
            Assert.Equal(200, addr.Offset);
        }

        // ═══════════════════════════════════════════
        //  系统内存和定时器
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_M1000()
        {
            var addr = GeSrtpAddress.TryParse("M1000");
            Assert.NotNull(addr);
            Assert.Equal((byte)0x14, addr.MemoryType);
            Assert.Equal(GeSrtpArea.SystemMemory, addr.Area);
            Assert.Equal(1000, addr.Offset);
        }

        [Fact]
        public void TryParse_T20()
        {
            var addr = GeSrtpAddress.TryParse("T20");
            Assert.NotNull(addr);
            Assert.Equal((byte)0x16, addr.MemoryType);
            Assert.Equal(GeSrtpArea.Timer, addr.Area);
            Assert.Equal(20, addr.Offset);
        }

        // ═══════════════════════════════════════════
        //  无效输入
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_Null()
        {
            Assert.Null(GeSrtpAddress.TryParse(null!));
        }

        [Fact]
        public void TryParse_Empty()
        {
            Assert.Null(GeSrtpAddress.TryParse(""));
            Assert.Null(GeSrtpAddress.TryParse("   "));
        }

        [Fact]
        public void TryParse_SingleChar()
        {
            Assert.Null(GeSrtpAddress.TryParse("R"));
        }

        [Fact]
        public void TryParse_UnknownPrefix()
        {
            Assert.Null(GeSrtpAddress.TryParse("Z100"));
        }

        [Fact]
        public void TryParse_InvalidNumber()
        {
            Assert.Null(GeSrtpAddress.TryParse("RABC"));
        }

        [Fact]
        public void TryParse_RawAddressPreserved()
        {
            var addr = GeSrtpAddress.TryParse("%R100");
            Assert.NotNull(addr);
            Assert.Equal("%R100", addr.RawAddress);
        }
    }

    public class GeSrtpModelTests
    {
        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(18245, GeSrtpConstants.DefaultPort);
            Assert.Equal(8, GeSrtpConstants.FrameHeaderLength);
            Assert.Equal(128, GeSrtpConstants.MaxReadRegisters);
            Assert.Equal(128, GeSrtpConstants.MaxWriteRegisters);
            Assert.Equal(2048, GeSrtpConstants.MaxReadDiscrete);
        }

        [Fact]
        public void Constants_ServiceTypes()
        {
            Assert.Equal(0x01, GeSrtpConstants.ServiceTypeRead);
            Assert.Equal(0x02, GeSrtpConstants.ServiceTypeWrite);
            Assert.Equal(37, GeSrtpConstants.SubCmdReadDateTime);
            Assert.Equal(1, GeSrtpConstants.SubCmdReadProgramName);
        }

        [Fact]
        public void Constants_MaxAddresses()
        {
            Assert.Equal(32767, GeSrtpConstants.MaxRegister);
            Assert.Equal(32767, GeSrtpConstants.MaxAnalogInput);
            Assert.Equal(32767, GeSrtpConstants.MaxDiscreteInput);
            Assert.Equal(32767, GeSrtpConstants.MaxTimer);
        }

        [Theory]
        [InlineData(0x00, "正常完成")]
        [InlineData(0x01, "无效的服务类型")]
        [InlineData(0x02, "无效的内存类型")]
        [InlineData(0x03, "无效的偏移地址")]
        [InlineData(0x04, "无效的数据长度")]
        [InlineData(0x05, "PLC 处于保护模式")]
        [InlineData(0x06, "通信超时")]
        [InlineData(0x0D, "地址越界")]
        [InlineData(0xFF, "未知错误 (FF)")]
        public void ErrorCodes_Description(byte code, string expected)
        {
            Assert.Contains(expected, GeSrtpErrorCodes.GetDescription(code));
        }

        [Theory]
        [InlineData(GePlcModel.Series90_30)]
        [InlineData(GePlcModel.Series90_70)]
        [InlineData(GePlcModel.PACSystemsRX3i)]
        [InlineData(GePlcModel.PACSystemsRX7i)]
        [InlineData(GePlcModel.VersaMax)]
        [InlineData(GePlcModel.VersaMaxNano)]
        public void PlcModel_EnumDefined(GePlcModel model)
        {
            Assert.True(Enum.IsDefined(typeof(GePlcModel), model));
        }

        [Theory]
        [InlineData(GeSrtpArea.Register)]
        [InlineData(GeSrtpArea.AnalogInput)]
        [InlineData(GeSrtpArea.AnalogOutput)]
        [InlineData(GeSrtpArea.DiscreteInput)]
        [InlineData(GeSrtpArea.DiscreteOutput)]
        [InlineData(GeSrtpArea.SystemMemory)]
        [InlineData(GeSrtpArea.Timer)]
        public void Area_EnumDefined(GeSrtpArea area)
        {
            Assert.True(Enum.IsDefined(typeof(GeSrtpArea), area));
        }
    }
}
