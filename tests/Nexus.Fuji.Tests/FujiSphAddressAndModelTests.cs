using Xunit;
using Nexus.Fuji;

namespace Nexus.Fuji.Tests
{
    public class FujiSphAddressTests
    {
        // ═══════════════════════════════════════════
        //  各区域解析
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_D100()
        {
            var addr = FujiSphAddress.TryParse("D100");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.DataRegister, addr.Area);
            Assert.Equal("01", addr.AreaCode);
            Assert.Equal(100, addr.Number);
        }

        [Fact]
        public void TryParse_D0()
        {
            var addr = FujiSphAddress.TryParse("D0");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.DataRegister, addr.Area);
            Assert.Equal("01", addr.AreaCode);
            Assert.Equal(0, addr.Number);
        }

        [Fact]
        public void TryParse_M50()
        {
            var addr = FujiSphAddress.TryParse("M50");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.InternalRelay, addr.Area);
            Assert.Equal("02", addr.AreaCode);
            Assert.Equal(50, addr.Number);
        }

        [Fact]
        public void TryParse_X0()
        {
            var addr = FujiSphAddress.TryParse("X0");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.Input, addr.Area);
            Assert.Equal("03", addr.AreaCode);
            Assert.Equal(0, addr.Number);
        }

        [Fact]
        public void TryParse_Y10()
        {
            var addr = FujiSphAddress.TryParse("Y10");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.Output, addr.Area);
            Assert.Equal("04", addr.AreaCode);
            Assert.Equal(10, addr.Number);
        }

        [Fact]
        public void TryParse_T100()
        {
            var addr = FujiSphAddress.TryParse("T100");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.Timer, addr.Area);
            Assert.Equal("05", addr.AreaCode);
            Assert.Equal(100, addr.Number);
        }

        [Fact]
        public void TryParse_C20()
        {
            var addr = FujiSphAddress.TryParse("C20");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.Counter, addr.Area);
            Assert.Equal("06", addr.AreaCode);
            Assert.Equal(20, addr.Number);
        }

        [Fact]
        public void TryParse_R200()
        {
            var addr = FujiSphAddress.TryParse("R200");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.FileRegister, addr.Area);
            Assert.Equal("07", addr.AreaCode);
            Assert.Equal(200, addr.Number);
        }

        [Fact]
        public void TryParse_L30()
        {
            var addr = FujiSphAddress.TryParse("L30");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.LinkRegister, addr.Area);
            Assert.Equal("08", addr.AreaCode);
            Assert.Equal(30, addr.Number);
        }

        // ═══════════════════════════════════════════
        //  大小写和空格
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_Lowercase()
        {
            var addr = FujiSphAddress.TryParse("d100");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.DataRegister, addr.Area);
        }

        [Fact]
        public void TryParse_WithSpaces()
        {
            var addr = FujiSphAddress.TryParse("  M50  ");
            Assert.NotNull(addr);
            Assert.Equal(FujiArea.InternalRelay, addr.Area);
            Assert.Equal(50, addr.Number);
        }

        // ═══════════════════════════════════════════
        //  原始地址保留
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_RawAddressPreserved()
        {
            var addr = FujiSphAddress.TryParse("D100");
            Assert.NotNull(addr);
            Assert.Equal("D100", addr.RawAddress);
        }

        // ═══════════════════════════════════════════
        //  无效输入
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_Null()
        {
            Assert.Null(FujiSphAddress.TryParse(null!));
        }

        [Fact]
        public void TryParse_Empty()
        {
            Assert.Null(FujiSphAddress.TryParse(""));
            Assert.Null(FujiSphAddress.TryParse("   "));
        }

        [Fact]
        public void TryParse_SingleChar()
        {
            Assert.Null(FujiSphAddress.TryParse("D"));
        }

        [Fact]
        public void TryParse_UnknownPrefix()
        {
            Assert.Null(FujiSphAddress.TryParse("Z100"));
        }

        [Fact]
        public void TryParse_InvalidNumber()
        {
            Assert.Null(FujiSphAddress.TryParse("DABC"));
        }
    }

    public class FujiSphModelTests
    {
        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(18245, FujiSphConstants.DefaultTcpPort);
            Assert.Equal(9600, FujiSphConstants.DefaultBaudRate);
            Assert.Equal(0x02, FujiSphConstants.STX);
            Assert.Equal(0x03, FujiSphConstants.ETX);
            Assert.Equal(0x06, FujiSphConstants.ACK);
            Assert.Equal(0x15, FujiSphConstants.NAK);
        }

        [Fact]
        public void Constants_CommandCodes()
        {
            Assert.Equal("0A", FujiSphConstants.CmdBatchRead);
            Assert.Equal("1A", FujiSphConstants.CmdBatchWrite);
            Assert.Equal("0C", FujiSphConstants.CmdRandomRead);
            Assert.Equal("1C", FujiSphConstants.CmdRandomWrite);
            Assert.Equal("1B", FujiSphConstants.CmdBitWrite);
            Assert.Equal("20", FujiSphConstants.CmdRun);
            Assert.Equal("21", FujiSphConstants.CmdStop);
            Assert.Equal("30", FujiSphConstants.CmdReadModel);
        }

        [Fact]
        public void Constants_MaxAddresses()
        {
            Assert.Equal(32767, FujiSphConstants.MaxDataRegister);
            Assert.Equal(4095, FujiSphConstants.MaxInternalRelay);
            Assert.Equal(2047, FujiSphConstants.MaxInput);
            Assert.Equal(2047, FujiSphConstants.MaxOutput);
            Assert.Equal(511, FujiSphConstants.MaxTimer);
            Assert.Equal(511, FujiSphConstants.MaxCounter);
        }

        [Theory]
        [InlineData("00", "正常完成")]
        [InlineData("01", "命令错误")]
        [InlineData("02", "地址错误")]
        [InlineData("03", "数据错误")]
        [InlineData("04", "BCC 校验错误")]
        [InlineData("05", "通信超时")]
        [InlineData("06", "写入禁止")]
        [InlineData("FF", "通用错误")]
        [InlineData("99", "未知错误 (99)")]
        public void ErrorCodes_Description(string code, string expected)
        {
            Assert.Contains(expected, FujiErrorCodes.GetDescription(code));
        }

        [Theory]
        [InlineData(FujiPlcModel.SPH)]
        [InlineData(FujiPlcModel.SPB)]
        [InlineData(FujiPlcModel.SPBN)]
        [InlineData(FujiPlcModel.NX)]
        [InlineData(FujiPlcModel.MicrexSX)]
        [InlineData(FujiPlcModel.SP10)]
        [InlineData(FujiPlcModel.SP20)]
        public void PlcModel_EnumDefined(FujiPlcModel model)
        {
            Assert.True(Enum.IsDefined(typeof(FujiPlcModel), model));
        }

        [Theory]
        [InlineData(FujiArea.DataRegister)]
        [InlineData(FujiArea.InternalRelay)]
        [InlineData(FujiArea.Input)]
        [InlineData(FujiArea.Output)]
        [InlineData(FujiArea.Timer)]
        [InlineData(FujiArea.Counter)]
        [InlineData(FujiArea.FileRegister)]
        [InlineData(FujiArea.LinkRegister)]
        public void Area_EnumDefined(FujiArea area)
        {
            Assert.True(Enum.IsDefined(typeof(FujiArea), area));
        }
    }
}
