using Xunit;
using Nexus.Inovance;

namespace Nexus.Inovance.Tests
{
    public class InovanceAddressTests
    {
        // ═══════════════════════════════════════════
        //  数据寄存器 (D)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_D100()
        {
            var addr = InovanceAddress.TryParse("D100");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.DataRegister, addr.Area);
            Assert.Equal((byte)0x40, addr.TypeCode);
            Assert.False(addr.IsExtended);
        }

        [Fact]
        public void TryParse_D0()
        {
            var addr = InovanceAddress.TryParse("D0");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.DataRegister, addr.Area);
            Assert.Equal(0, addr.Value);
        }

        [Fact]
        public void TryParse_D100_Bit5()
        {
            var addr = InovanceAddress.TryParse("D100.5");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.DataRegister, addr.Area);
            // D100.5 → 100 * 16 + 5 = 1605
            Assert.Equal(1605, addr.Value);
        }

        // ═══════════════════════════════════════════
        //  链接寄存器 (W) 和系统寄存器 (R)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_W100()
        {
            var addr = InovanceAddress.TryParse("W100");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.LinkRegister, addr.Area);
            Assert.Equal((byte)0x60, addr.TypeCode);
            // W100 → 100 * 16 = 1600
            Assert.Equal(1600, addr.Value);
        }

        [Fact]
        public void TryParse_R10()
        {
            var addr = InovanceAddress.TryParse("R10");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.SystemRegister, addr.Area);
            Assert.Equal((byte)0x50, addr.TypeCode);
            Assert.Equal(160, addr.Value);
        }

        // ═══════════════════════════════════════════
        //  位区域 (X/Y/M/S/B)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_X0_Octal()
        {
            var addr = InovanceAddress.TryParse("X0");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.Input, addr.Area);
            Assert.Equal((byte)0x00, addr.TypeCode);
            Assert.Equal(0, addr.Value);
        }

        [Fact]
        public void TryParse_X7_Octal()
        {
            var addr = InovanceAddress.TryParse("X7");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.Input, addr.Area);
            Assert.Equal(7, addr.Value);
        }

        [Fact]
        public void TryParse_X10_Octal()
        {
            var addr = InovanceAddress.TryParse("X10");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.Input, addr.Area);
            // X10 八进制 = 8
            Assert.Equal(8, addr.Value);
        }

        [Fact]
        public void TryParse_Y0_Octal()
        {
            var addr = InovanceAddress.TryParse("Y0");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.Output, addr.Area);
            // Y0 = 0 + 0x80000
            Assert.Equal(0x80000, addr.Value);
        }

        [Fact]
        public void TryParse_Y7_Octal()
        {
            var addr = InovanceAddress.TryParse("Y7");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.Output, addr.Area);
            Assert.Equal(0x80000 + 7, addr.Value);
        }

        [Fact]
        public void TryParse_M100()
        {
            var addr = InovanceAddress.TryParse("M100");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.AuxiliaryRelay, addr.Area);
            Assert.Equal((byte)0x10, addr.TypeCode);
            Assert.Equal(100, addr.Value);
        }

        [Fact]
        public void TryParse_S50()
        {
            var addr = InovanceAddress.TryParse("S50");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.StepRelay, addr.Area);
            // S50 = 50 + 0x80000
            Assert.Equal(0x80000 + 50, addr.Value);
        }

        [Fact]
        public void TryParse_B3()
        {
            var addr = InovanceAddress.TryParse("B3");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.LinkRelay, addr.Area);
            Assert.Equal((byte)0x20, addr.TypeCode);
            Assert.Equal(3, addr.Value);
        }

        // ═══════════════════════════════════════════
        //  扩展区域 (UB/UW/U)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_UB_FF()
        {
            var addr = InovanceAddress.TryParse("UB0xFF");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.Extended, addr.Area);
            Assert.Equal((byte)0xF0, addr.TypeCode);
            Assert.True(addr.IsExtended);
            Assert.Equal(0xFF, addr.Value);
        }

        [Fact]
        public void TryParse_UW_10()
        {
            var addr = InovanceAddress.TryParse("UW0x10");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.Extended, addr.Area);
            Assert.Equal(0x10, addr.Value);
        }

        [Fact]
        public void TryParse_U_FF()
        {
            var addr = InovanceAddress.TryParse("U0xFF");
            Assert.NotNull(addr);
            Assert.Equal(InovanceArea.Extended, addr.Area);
            Assert.Equal(0xFF, addr.Value);
        }

        // ═══════════════════════════════════════════
        //  无效输入
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_Null()
        {
            Assert.Null(InovanceAddress.TryParse(null!));
        }

        [Fact]
        public void TryParse_Empty()
        {
            Assert.Null(InovanceAddress.TryParse(""));
        }

        [Fact]
        public void TryParse_UnknownPrefix()
        {
            Assert.Null(InovanceAddress.TryParse("Z100"));
        }

        [Fact]
        public void TryParse_RawAddressPreserved()
        {
            var addr = InovanceAddress.TryParse("D100");
            Assert.NotNull(addr);
            Assert.Equal("D100", addr.RawAddress);
        }
    }

    public class InovanceModelTests
    {
        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(502, InovanceConstants.DefaultTcpPort);
            Assert.Equal(22, InovanceConstants.FrameHeaderLength);
            Assert.Equal(64, InovanceConstants.MaxReadRegisters);
            Assert.Equal(64, InovanceConstants.MaxWriteRegisters);
        }

        [Fact]
        public void Constants_CommandCodes()
        {
            Assert.Equal(0x01, InovanceConstants.CmdRead);
            Assert.Equal(0x02, InovanceConstants.CmdWrite);
            Assert.Equal(0x0F, InovanceConstants.ErrorFlag);
        }

        [Fact]
        public void Constants_MaxAddresses()
        {
            Assert.Equal(7999, InovanceConstants.MaxDataRegister);
            Assert.Equal(9999, InovanceConstants.MaxAuxiliaryRelay);
            Assert.Equal(777, InovanceConstants.MaxInputOctal);
            Assert.Equal(777, InovanceConstants.MaxOutputOctal);
            Assert.Equal(32767, InovanceConstants.MaxSystemRegister);
        }

        [Theory]
        [InlineData(0x00, "正常完成")]
        [InlineData(0x01, "命令错误")]
        [InlineData(0x02, "地址错误")]
        [InlineData(0x03, "数据错误")]
        [InlineData(0x04, "通信超时")]
        [InlineData(0x05, "写入禁止")]
        [InlineData(0x0F, "通用错误")]
        [InlineData(0x10, "保护错误")]
        public void ErrorCodes_Description(byte code, string expected)
        {
            Assert.Contains(expected, InovanceErrorCodes.GetDescription(code));
        }

        [Theory]
        [InlineData(InovanceModel.H1U)]
        [InlineData(InovanceModel.H2U)]
        [InlineData(InovanceModel.H3U)]
        [InlineData(InovanceModel.H5U)]
        [InlineData(InovanceModel.AM)]
        [InlineData(InovanceModel.XG)]
        public void PlcModel_EnumDefined(InovanceModel model)
        {
            Assert.True(Enum.IsDefined(typeof(InovanceModel), model));
        }

        [Theory]
        [InlineData(InovanceArea.Input)]
        [InlineData(InovanceArea.Output)]
        [InlineData(InovanceArea.AuxiliaryRelay)]
        [InlineData(InovanceArea.StepRelay)]
        [InlineData(InovanceArea.LinkRelay)]
        [InlineData(InovanceArea.DataRegister)]
        [InlineData(InovanceArea.SystemRegister)]
        [InlineData(InovanceArea.LinkRegister)]
        [InlineData(InovanceArea.Extended)]
        public void Area_EnumDefined(InovanceArea area)
        {
            Assert.True(Enum.IsDefined(typeof(InovanceArea), area));
        }
    }
}
