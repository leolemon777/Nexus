using Xunit;
using Nexus.Yokogawa;

namespace Nexus.Yokogawa.Tests
{
    public class YokogawaModelTests
    {
        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(10001, YokogawaConstants.DefaultPort);
            Assert.Equal(28, YokogawaConstants.FrameHeaderLength);
            Assert.Equal(512, YokogawaConstants.MaxReadRegisters);
            Assert.Equal(512, YokogawaConstants.MaxWriteRegisters);
            Assert.Equal(4096, YokogawaConstants.MaxReadRelays);
            Assert.Equal(4096, YokogawaConstants.MaxWriteRelays);
        }

        [Fact]
        public void Constants_DataCodes_Register()
        {
            Assert.Equal(4, YokogawaConstants.DataCodeD);
            Assert.Equal(2, YokogawaConstants.DataCodeB);
            Assert.Equal(6, YokogawaConstants.DataCodeF);
            Assert.Equal(18, YokogawaConstants.DataCodeR);
            Assert.Equal(22, YokogawaConstants.DataCodeV);
            Assert.Equal(26, YokogawaConstants.DataCodeZ);
            Assert.Equal(23, YokogawaConstants.DataCodeW);
            Assert.Equal(33, YokogawaConstants.DataCodeTN);
            Assert.Equal(49, YokogawaConstants.DataCodeCN);
        }

        [Fact]
        public void Constants_DataCodes_Relay()
        {
            Assert.Equal(24, YokogawaConstants.DataCodeX);
            Assert.Equal(25, YokogawaConstants.DataCodeY);
            Assert.Equal(9, YokogawaConstants.DataCodeI);
            Assert.Equal(5, YokogawaConstants.DataCodeE);
            Assert.Equal(13, YokogawaConstants.DataCodeM);
            Assert.Equal(20, YokogawaConstants.DataCodeT);
            Assert.Equal(3, YokogawaConstants.DataCodeC);
            Assert.Equal(12, YokogawaConstants.DataCodeL);
        }

        [Theory]
        [InlineData(0, "正常完成")]
        [InlineData(1, "命令格式错误")]
        [InlineData(2, "数据代码错误")]
        [InlineData(3, "地址错误")]
        [InlineData(4, "数据长度错误")]
        [InlineData(5, "写入数据错误")]
        [InlineData(6, "PLC 处于保护模式")]
        [InlineData(7, "通信超时")]
        [InlineData(8, "PLC 繁忙")]
        [InlineData(9, "系统错误")]
        [InlineData(99, "未知错误 (99)")]
        public void ErrorCodes_Description(int code, string expected)
        {
            Assert.Contains(expected, YokogawaErrorCodes.GetDescription(code));
        }

        [Theory]
        [InlineData(YokogawaModel.FAM3)]
        [InlineData(YokogawaModel.FAM3RangeFree)]
        [InlineData(YokogawaModel.VnetIP)]
        [InlineData(YokogawaModel.Stardom)]
        [InlineData(YokogawaModel.FCN)]
        [InlineData(YokogawaModel.FCJ)]
        [InlineData(YokogawaModel.CentumVP)]
        public void PlcModel_EnumDefined(YokogawaModel model)
        {
            Assert.True(Enum.IsDefined(typeof(YokogawaModel), model));
        }

        [Fact]
        public void YokogawaAddress_IsBitType_Register()
        {
            // D 寄存器（code=4）不是位类型
            Assert.False(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeD));
            Assert.False(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeB));
            Assert.False(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeR));
            Assert.False(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeTN));
        }

        [Fact]
        public void YokogawaAddress_IsBitType_Relay()
        {
            // 继电器（位）类型
            Assert.True(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeX));
            Assert.True(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeY));
            Assert.True(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeI));
            Assert.True(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeE));
            Assert.True(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeM));
            Assert.True(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeT));
            Assert.True(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeC));
            Assert.True(YokogawaAddress.IsBitDataCode(YokogawaConstants.DataCodeL));
        }

        [Fact]
        public void YokogawaAddress_GetAddressBinaryContent()
        {
            var addr = new YokogawaAddress { DataCode = 4, AddressStart = 100, Length = 2 };
            byte[] bin = addr.GetAddressBinaryContent();
            Assert.Equal(6, bin.Length);
            // DataCode=4 → [0x00, 0x04, ...]
            Assert.Equal(0x00, bin[0]);
            Assert.Equal(0x04, bin[1]);
            // AddressStart=100 → [0x00, 0x00, 0x00, 0x64]
            Assert.Equal(0x00, bin[2]);
            Assert.Equal(0x00, bin[3]);
            Assert.Equal(0x00, bin[4]);
            Assert.Equal(100, bin[5]);
        }
    }
}
