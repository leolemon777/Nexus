using Xunit;
using Nexus.Fatek;

namespace Nexus.Fatek.Tests
{
    public class FatekAddressTests
    {
        // ═══════════════════════════════════════════
        //  位区域解析
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_R0()
        {
            var addr = FatekAddress.TryParse("R0");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.InternalRelay, addr.Area);
            Assert.Equal("R", addr.AreaCode);
            Assert.Equal(0, addr.Number);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void TryParse_R9999()
        {
            var addr = FatekAddress.TryParse("R9999");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.InternalRelay, addr.Area);
            Assert.Equal(9999, addr.Number);
        }

        [Fact]
        public void TryParse_X0()
        {
            var addr = FatekAddress.TryParse("X0");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.Input, addr.Area);
            Assert.Equal("X", addr.AreaCode);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void TryParse_Y10()
        {
            var addr = FatekAddress.TryParse("Y10");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.Output, addr.Area);
            Assert.Equal(10, addr.Number);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void TryParse_M100()
        {
            var addr = FatekAddress.TryParse("M100");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.AuxiliaryRelay, addr.Area);
            Assert.Equal(100, addr.Number);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void TryParse_S50()
        {
            var addr = FatekAddress.TryParse("S50");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.StepRelay, addr.Area);
            Assert.Equal("S", addr.AreaCode);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void TryParse_TS10()
        {
            var addr = FatekAddress.TryParse("TS10");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.TimerContact, addr.Area);
            Assert.Equal("TS", addr.AreaCode);
            Assert.Equal(10, addr.Number);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void TryParse_CS20()
        {
            var addr = FatekAddress.TryParse("CS20");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.CounterContact, addr.Area);
            Assert.Equal("CS", addr.AreaCode);
            Assert.Equal(20, addr.Number);
            Assert.True(addr.IsBit);
        }

        // ═══════════════════════════════════════════
        //  寄存器区域解析
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_D100()
        {
            var addr = FatekAddress.TryParse("D100");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.DataRegister, addr.Area);
            Assert.Equal(100, addr.Number);
            Assert.False(addr.IsBit);
        }

        [Fact]
        public void TryParse_D3899()
        {
            var addr = FatekAddress.TryParse("D3899");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.DataRegister, addr.Area);
            Assert.Equal(3899, addr.Number);
        }

        [Fact]
        public void TryParse_T0()
        {
            var addr = FatekAddress.TryParse("T0");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.TimerValue, addr.Area);
            Assert.Equal(0, addr.Number);
            Assert.False(addr.IsBit);
        }

        [Fact]
        public void TryParse_C255()
        {
            var addr = FatekAddress.TryParse("C255");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.CounterValue, addr.Area);
            Assert.Equal(255, addr.Number);
            Assert.False(addr.IsBit);
        }

        // ═══════════════════════════════════════════
        //  大小写和空格
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_Lowercase()
        {
            var addr = FatekAddress.TryParse("d100");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.DataRegister, addr.Area);
        }

        [Fact]
        public void TryParse_WithSpaces()
        {
            var addr = FatekAddress.TryParse("  R50  ");
            Assert.NotNull(addr);
            Assert.Equal(FatekArea.InternalRelay, addr.Area);
            Assert.Equal(50, addr.Number);
        }

        // ═══════════════════════════════════════════
        //  无效输入
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_Null()
        {
            Assert.Null(FatekAddress.TryParse(null!));
        }

        [Fact]
        public void TryParse_Empty()
        {
            Assert.Null(FatekAddress.TryParse(""));
            Assert.Null(FatekAddress.TryParse("   "));
        }

        [Fact]
        public void TryParse_UnknownPrefix()
        {
            Assert.Null(FatekAddress.TryParse("Z100"));
        }

        [Fact]
        public void TryParse_NoNumber()
        {
            Assert.Null(FatekAddress.TryParse("R"));
            Assert.Null(FatekAddress.TryParse("D"));
        }

        [Fact]
        public void TryParse_InvalidNumber()
        {
            Assert.Null(FatekAddress.TryParse("RABC"));
        }

        // ═══════════════════════════════════════════
        //  ToCommandFormat
        // ═══════════════════════════════════════════

        [Fact]
        public void ToCommandFormat_PadsTo4Digits()
        {
            var addr = FatekAddress.TryParse("R5");
            Assert.NotNull(addr);
            Assert.Equal("R0005", addr.ToCommandFormat());
        }

        [Fact]
        public void ToCommandFormat_4Digits()
        {
            var addr = FatekAddress.TryParse("D1234");
            Assert.NotNull(addr);
            Assert.Equal("D1234", addr.ToCommandFormat());
        }
    }

    public class FatekModelTests
    {
        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(5000, FatekConstants.DefaultTcpPort);
            Assert.Equal(9600, FatekConstants.DefaultBaudRate);
            Assert.Equal(0x02, FatekConstants.STX);
            Assert.Equal(0x03, FatekConstants.ETX);
            Assert.Equal(64, FatekConstants.MaxReadRegisters);
            Assert.Equal(256, FatekConstants.MaxBits);
        }

        [Fact]
        public void Constants_MaxAddresses()
        {
            Assert.Equal(9999, FatekConstants.MaxR);
            Assert.Equal(3899, FatekConstants.MaxD);
            Assert.Equal(255, FatekConstants.MaxT);
            Assert.Equal(255, FatekConstants.MaxC);
        }

        [Theory]
        [InlineData("0", "正常完成")]
        [InlineData("1", "地址错误")]
        [InlineData("2", "数据错误")]
        [InlineData("3", "命令错误")]
        [InlineData("4", "校验错误")]
        [InlineData("5", "通信错误")]
        [InlineData("6", "写入禁止")]
        [InlineData("7", "PLC 繁忙")]
        [InlineData("8", "站号错误")]
        [InlineData("99", "未知错误 (99)")]
        public void ErrorCodes_Description(string code, string expected)
        {
            Assert.Contains(expected, FatekErrorCodes.GetDescription(code));
        }

        [Theory]
        [InlineData(FatekModel.FBs10MA)]
        [InlineData(FatekModel.FBs60MA)]
        [InlineData(FatekModel.FBs32MC)]
        [InlineData(FatekModel.FBs44MB)]
        [InlineData(FatekModel.B1)]
        [InlineData(FatekModel.B1z)]
        public void Model_EnumDefined(FatekModel model)
        {
            Assert.True(Enum.IsDefined(typeof(FatekModel), model));
        }

        [Theory]
        [InlineData(FatekArea.InternalRelay)]
        [InlineData(FatekArea.Input)]
        [InlineData(FatekArea.Output)]
        [InlineData(FatekArea.AuxiliaryRelay)]
        [InlineData(FatekArea.DataRegister)]
        [InlineData(FatekArea.TimerValue)]
        [InlineData(FatekArea.CounterValue)]
        [InlineData(FatekArea.TimerContact)]
        [InlineData(FatekArea.CounterContact)]
        [InlineData(FatekArea.StepRelay)]
        public void Area_EnumDefined(FatekArea area)
        {
            Assert.True(Enum.IsDefined(typeof(FatekArea), area));
        }
    }
}
