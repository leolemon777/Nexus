using Xunit;
using Nexus.Yaskawa;

namespace Nexus.Yaskawa.Tests
{
    public class MemobusAddressTests
    {
        // ═══════════════════════════════════════════
        //  标准数字地址解析
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_StandardAddress100()
        {
            var addr = MemobusAddress.TryParse("100");
            Assert.NotNull(addr);
            Assert.False(addr.IsNamed);
            Assert.Equal((ushort)100, addr.AddressValue);
            Assert.Equal((byte)3, addr.SubFunctionCode);
            Assert.Equal(MemobusArea.HoldingRegister, addr.Area);
        }

        [Fact]
        public void TryParse_StandardAddressWithSfc1()
        {
            var addr = MemobusAddress.TryParse("50;x=1");
            Assert.NotNull(addr);
            Assert.Equal((ushort)50, addr.AddressValue);
            Assert.Equal((byte)1, addr.SubFunctionCode);
            Assert.Equal(MemobusArea.Coil, addr.Area);
        }

        [Fact]
        public void TryParse_StandardAddressWithSfc2()
        {
            var addr = MemobusAddress.TryParse("30;x=2");
            Assert.NotNull(addr);
            Assert.Equal((byte)2, addr.SubFunctionCode);
            Assert.Equal(MemobusArea.DiscreteInput, addr.Area);
        }

        [Fact]
        public void TryParse_StandardAddressWithSfc4()
        {
            var addr = MemobusAddress.TryParse("20;x=4");
            Assert.NotNull(addr);
            Assert.Equal((byte)4, addr.SubFunctionCode);
            Assert.Equal(MemobusArea.InputRegister, addr.Area);
        }

        [Fact]
        public void TryParse_StandardAddressWithSfc9()
        {
            var addr = MemobusAddress.TryParse("100;x=9");
            Assert.NotNull(addr);
            Assert.Equal((byte)9, addr.SubFunctionCode);
            Assert.Equal(MemobusArea.ExtendedHolding, addr.Area);
        }

        [Fact]
        public void TryParse_StandardAddressWithSfc10()
        {
            var addr = MemobusAddress.TryParse("200;x=10");
            Assert.NotNull(addr);
            Assert.Equal((byte)10, addr.SubFunctionCode);
            Assert.Equal(MemobusArea.ExtendedInput, addr.Area);
        }

        // ═══════════════════════════════════════════
        //  命名区域地址解析
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_NamedM100()
        {
            var addr = MemobusAddress.TryParse("M100");
            Assert.NotNull(addr);
            Assert.True(addr.IsNamed);
            Assert.Equal((uint)100, addr.NamedAddressValue);
            Assert.Equal((byte)'M', (byte)addr.Area);
            Assert.False(addr.IsBitAccess);
            Assert.Equal((byte)0x49, addr.SubFunctionCode);
        }

        [Fact]
        public void TryParse_NamedG200()
        {
            var addr = MemobusAddress.TryParse("G200");
            Assert.NotNull(addr);
            Assert.True(addr.IsNamed);
            Assert.Equal((uint)200, addr.NamedAddressValue);
            Assert.Equal((byte)'G', (byte)addr.Area);
        }

        [Fact]
        public void TryParse_NamedI0()
        {
            var addr = MemobusAddress.TryParse("I0");
            Assert.NotNull(addr);
            Assert.True(addr.IsNamed);
            Assert.Equal((uint)0, addr.NamedAddressValue);
            Assert.Equal((byte)'I', (byte)addr.Area);
        }

        [Fact]
        public void TryParse_NamedO10()
        {
            var addr = MemobusAddress.TryParse("O10");
            Assert.NotNull(addr);
            Assert.True(addr.IsNamed);
            Assert.Equal((uint)10, addr.NamedAddressValue);
            Assert.Equal((byte)'O', (byte)addr.Area);
        }

        [Fact]
        public void TryParse_NamedS50()
        {
            var addr = MemobusAddress.TryParse("S50");
            Assert.NotNull(addr);
            Assert.True(addr.IsNamed);
            Assert.Equal((uint)50, addr.NamedAddressValue);
            Assert.Equal((byte)'S', (byte)addr.Area);
        }

        // ═══════════════════════════════════════════
        //  位访问解析
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_BitAccess_MB100()
        {
            var addr = MemobusAddress.TryParse("MB100");
            Assert.NotNull(addr);
            Assert.True(addr.IsBitAccess);
            Assert.Equal((byte)0x41, addr.SubFunctionCode);
        }

        [Fact]
        public void TryParse_BitAccess_M100_5()
        {
            var addr = MemobusAddress.TryParse("M100.5");
            Assert.NotNull(addr);
            Assert.True(addr.IsBitAccess);
            Assert.True(addr.BoolIndex > 0);
        }

        [Fact]
        public void TryParse_BitAccess_M10A()
        {
            var addr = MemobusAddress.TryParse("M10A");
            Assert.NotNull(addr);
            Assert.True(addr.IsBitAccess);
            // 10 * 16 + 10 = 170
            Assert.Equal(170, addr.BoolIndex);
        }

        [Fact]
        public void CalculateBoolIndex_WordDotBit()
        {
            int idx = MemobusAddress.CalculateBoolIndexInternal("M100.5");
            Assert.Equal(100 * 16 + 5, idx);
        }

        [Fact]
        public void CalculateBoolIndex_WordLetter()
        {
            int idx = MemobusAddress.CalculateBoolIndexInternal("M10F");
            Assert.Equal(10 * 16 + 15, idx);
        }

        [Fact]
        public void CalculateBoolIndex_WordOnly()
        {
            int idx = MemobusAddress.CalculateBoolIndexInternal("M10");
            Assert.Equal(10 * 16, idx);
        }

        // ═══════════════════════════════════════════
        //  辅助方法
        // ═══════════════════════════════════════════

        [Fact]
        public void IsNamedPrefix_Valid()
        {
            Assert.True(MemobusAddress.IsNamedPrefix("M100"));
            Assert.True(MemobusAddress.IsNamedPrefix("G200"));
            Assert.True(MemobusAddress.IsNamedPrefix("I0"));
            Assert.True(MemobusAddress.IsNamedPrefix("O10"));
            Assert.True(MemobusAddress.IsNamedPrefix("S50"));
        }

        [Fact]
        public void IsNamedPrefix_Invalid()
        {
            Assert.False(MemobusAddress.IsNamedPrefix("100"));
            Assert.False(MemobusAddress.IsNamedPrefix("D100"));
            Assert.False(MemobusAddress.IsNamedPrefix(""));
            Assert.False(MemobusAddress.IsNamedPrefix(null!));
        }

        [Fact]
        public void GetDataType()
        {
            Assert.Equal((byte)'M', MemobusAddress.GetDataType("M100"));
            Assert.Equal((byte)'G', MemobusAddress.GetDataType("g200"));
            Assert.Equal((byte)'I', MemobusAddress.GetDataType("I0"));
        }

        [Fact]
        public void CalculateBitIndex_Digit()
        {
            Assert.Equal(5, MemobusAddress.CalculateBitIndex("5"));
            Assert.Equal(0, MemobusAddress.CalculateBitIndex("0"));
            Assert.Equal(9, MemobusAddress.CalculateBitIndex("9"));
        }

        [Fact]
        public void CalculateBitIndex_Hex()
        {
            Assert.Equal(10, MemobusAddress.CalculateBitIndex("A"));
            Assert.Equal(15, MemobusAddress.CalculateBitIndex("F"));
            Assert.Equal(13, MemobusAddress.CalculateBitIndex("D"));
        }

        [Fact]
        public void TryParse_NullOrEmpty()
        {
            Assert.Null(MemobusAddress.TryParse(null!));
            Assert.Null(MemobusAddress.TryParse(""));
            Assert.Null(MemobusAddress.TryParse("   "));
        }

        [Fact]
        public void TryParse_InvalidAddress()
        {
            Assert.Null(MemobusAddress.TryParse("ABC"));
            Assert.Null(MemobusAddress.TryParse("Z100"));
        }

        [Fact]
        public void GetAreaDescription_Named()
        {
            var addr = MemobusAddress.TryParse("M100");
            Assert.NotNull(addr);
            Assert.Equal("Named_M", addr.GetAreaDescription());
        }

        [Fact]
        public void GetAreaDescription_Standard()
        {
            var addr = MemobusAddress.TryParse("100");
            Assert.NotNull(addr);
            Assert.Equal("HoldingRegister", addr.GetAreaDescription());
        }
    }

    public class MemobusModelTests
    {
        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(502, MemobusConstants.DefaultPort);
            Assert.Equal(12, MemobusConstants.OuterHeaderLength);
            Assert.Equal(0x11, MemobusConstants.OuterHeaderMarker);
            Assert.Equal(125, MemobusConstants.MaxReadRegisters);
            Assert.Equal(100, MemobusConstants.MaxWriteRegisters);
            Assert.Equal(2000, MemobusConstants.MaxReadCoils);
        }

        [Fact]
        public void Mfc_EnumValues()
        {
            Assert.Equal(0x20, (byte)MemobusMfc.Standard);
            Assert.Equal(0x43, (byte)MemobusMfc.Named);
        }

        [Fact]
        public void Sfc_EnumValues()
        {
            Assert.Equal(1, (byte)MemobusSfc.ReadCoil);
            Assert.Equal(3, (byte)MemobusSfc.ReadHoldingRegister);
            Assert.Equal(0x10, (byte)MemobusSfc.WriteMultipleRegisters);
            Assert.Equal(9, (byte)MemobusSfc.ExtendedRead);
            Assert.Equal(0x0D, (byte)MemobusSfc.ReadRandom);
            Assert.Equal(0x41, (byte)MemobusSfc.NamedReadBit);
            Assert.Equal(0x49, (byte)MemobusSfc.NamedReadWord);
            Assert.Equal(0x4B, (byte)MemobusSfc.NamedWriteWord);
        }

        [Theory]
        [InlineData(0x00, "正常完成")]
        [InlineData(0x01, "非法功能码")]
        [InlineData(0x02, "非法数据地址")]
        [InlineData(0x03, "非法数据值")]
        [InlineData(0x40, "从站设备故障")]
        [InlineData(0x41, "CPU 异常")]
        [InlineData(0x42, "无法执行")]
        [InlineData(0x99, "未知错误 (99)")]
        public void ErrorCodes_Description(byte code, string expected)
        {
            Assert.Equal(expected, MemobusErrorCodes.GetDescription(code));
        }

        [Fact]
        public void YaskawaModel_EnumDefined()
        {
            Assert.True(Enum.IsDefined(typeof(YaskawaModel), YaskawaModel.Mp2300S));
            Assert.True(Enum.IsDefined(typeof(YaskawaModel), YaskawaModel.Ga700));
            Assert.True(Enum.IsDefined(typeof(YaskawaModel), YaskawaModel.Slio));
        }
    }
}
