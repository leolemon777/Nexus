using Xunit;
using Nexus.Siemens;

namespace Nexus.Siemens.Tests
{
    public class SiemensS7ModelEnhancedTests
    {
        // ═══════════════════════════════════════════
        //  PLC 型号枚举
        // ═══════════════════════════════════════════

        [Fact]
        public void SiemensPLCS_AllDefined()
        {
            Assert.True(Enum.IsDefined(typeof(SiemensPLCS), SiemensPLCS.S7_200));
            Assert.True(Enum.IsDefined(typeof(SiemensPLCS), SiemensPLCS.S7_200Smart));
            Assert.True(Enum.IsDefined(typeof(SiemensPLCS), SiemensPLCS.S7_300));
            Assert.True(Enum.IsDefined(typeof(SiemensPLCS), SiemensPLCS.S7_400));
            Assert.True(Enum.IsDefined(typeof(SiemensPLCS), SiemensPLCS.S7_1200));
            Assert.True(Enum.IsDefined(typeof(SiemensPLCS), SiemensPLCS.S7_1500));
            Assert.True(Enum.IsDefined(typeof(SiemensPLCS), SiemensPLCS.S7_1200Plus));
            Assert.True(Enum.IsDefined(typeof(SiemensPLCS), SiemensPLCS.S7_1500Plus));
            Assert.True(Enum.IsDefined(typeof(SiemensPLCS), SiemensPLCS.LOGO));
        }

        // ═══════════════════════════════════════════
        //  S7Area 枚举值
        // ═══════════════════════════════════════════

        [Fact]
        public void S7Area_Values()
        {
            Assert.Equal(0x81, (byte)S7Area.PE);
            Assert.Equal(0x82, (byte)S7Area.PA);
            Assert.Equal(0x83, (byte)S7Area.MK);
            Assert.Equal(0x84, (byte)S7Area.DB);
            Assert.Equal(0x1C, (byte)S7Area.CT);
            Assert.Equal(0x1D, (byte)S7Area.TM);
            Assert.Equal(0x85, (byte)S7Area.V);
        }

        // ═══════════════════════════════════════════
        //  S7DataType 枚举值
        // ═══════════════════════════════════════════

        [Fact]
        public void S7DataType_Values()
        {
            Assert.Equal(0x01, (byte)S7DataType.Bit);
            Assert.Equal(0x02, (byte)S7DataType.Byte);
            Assert.Equal(0x04, (byte)S7DataType.Word);
            Assert.Equal(0x06, (byte)S7DataType.DInt);
            Assert.Equal(0x08, (byte)S7DataType.Real);
            Assert.Equal(0x1C, (byte)S7DataType.Counter);
            Assert.Equal(0x1D, (byte)S7DataType.Timer);
        }

        // ═══════════════════════════════════════════
        //  S7Constants
        // ═══════════════════════════════════════════

        [Fact]
        public void S7Constants_DefaultValues()
        {
            Assert.Equal(102, S7Constants.DefaultPort);
            Assert.Equal(4, S7Constants.TpktHeaderLength);
            Assert.Equal(11, S7Constants.CotpCrLength);
            Assert.Equal(3, S7Constants.CotpDataLength);
            Assert.Equal(10, S7Constants.S7HeaderLength);
            Assert.Equal(240, S7Constants.DefaultPduSize);
            Assert.Equal(960, S7Constants.MaxPduSize_1200);
            Assert.Equal(19, S7Constants.MaxAddressItems);
        }

        [Fact]
        public void S7Constants_MessageTypes()
        {
            Assert.Equal(0x01, S7Constants.MsgJob);
            Assert.Equal(0x02, S7Constants.MsgAck);
            Assert.Equal(0x03, S7Constants.MsgAckData);
            Assert.Equal(0x07, S7Constants.MsgUserData);
        }

        // ═══════════════════════════════════════════
        //  S7ErrorCodes
        // ═══════════════════════════════════════════

        [Theory]
        [InlineData(0x00, 0x00, "无错误")]
        [InlineData(0x81, 0x05, "对象访问错误")]
        [InlineData(0x81, 0x07, "未找到请求的对象")]
        [InlineData(0x82, 0x01, "不正确的变量地址")]
        [InlineData(0x82, 0x04, "数据长度不匹配")]
        [InlineData(0x85, 0x01, "PDU 太长")]
        [InlineData(0x87, 0x01, "对象不存在")]
        [InlineData(0x87, 0x02, "对象不可访问")]
        [InlineData(0xFF, 0xFF, "未知错误")]
        public void S7ErrorCodes_Description(byte errorClass, byte errorCode, string expected)
        {
            string desc = S7ErrorCodes.GetDescription(errorClass, errorCode);
            Assert.Contains(expected, desc);
        }

        // ═══════════════════════════════════════════
        //  SiemensS7Address 解析
        // ═══════════════════════════════════════════

        [Fact]
        public void S7Address_ParseDB()
        {
            var addr = SiemensS7Address.Parse("DB1.DBW100");
            Assert.Equal(S7Area.DB, addr.Area);
            Assert.Equal(1, addr.DBNumber);
            Assert.Equal(100, addr.ByteAddress);
            Assert.Equal(2, addr.DataSize);
        }

        [Fact]
        public void S7Address_ParseDBD()
        {
            var addr = SiemensS7Address.Parse("DB10.DBD50");
            Assert.Equal(S7Area.DB, addr.Area);
            Assert.Equal(10, addr.DBNumber);
            Assert.Equal(50, addr.ByteAddress);
            Assert.Equal(4, addr.DataSize);
        }

        [Fact]
        public void S7Address_ParseDBX()
        {
            var addr = SiemensS7Address.Parse("DB1.DBX0.5");
            Assert.Equal(S7Area.DB, addr.Area);
            Assert.Equal(1, addr.DBNumber);
            Assert.Equal(0, addr.ByteAddress);
            Assert.Equal(5, addr.BitOffset);
            Assert.Equal(1, addr.DataSize);
        }

        [Fact]
        public void S7Address_ParseM()
        {
            var addr = SiemensS7Address.Parse("MW100");
            Assert.Equal(S7Area.MK, addr.Area);
            Assert.Equal(100, addr.ByteAddress);
        }

        [Fact]
        public void S7Address_ParseI()
        {
            var addr = SiemensS7Address.Parse("IW50");
            Assert.Equal(S7Area.PE, addr.Area);
            Assert.Equal(50, addr.ByteAddress);
        }

        [Fact]
        public void S7Address_ParseQ()
        {
            var addr = SiemensS7Address.Parse("QW30");
            Assert.Equal(S7Area.PA, addr.Area);
            Assert.Equal(30, addr.ByteAddress);
        }

        [Fact]
        public void S7Address_ParseV()
        {
            var addr = SiemensS7Address.Parse("V100");
            Assert.Equal(S7Area.V, addr.Area);
            Assert.Equal(0, addr.DBNumber);
        }

        [Fact]
        public void S7Address_TryParse_Invalid()
        {
            Assert.False(SiemensS7Address.TryParse("", out _));
            Assert.False(SiemensS7Address.TryParse("Z100", out _));
        }

        [Fact]
        public void S7Address_ToString()
        {
            var addr = SiemensS7Address.Parse("DB1.DBW100");
            Assert.Contains("DB", addr.ToString());
        }
    }
}
