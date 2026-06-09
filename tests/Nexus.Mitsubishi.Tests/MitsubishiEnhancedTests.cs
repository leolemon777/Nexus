using Xunit;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.Tests
{
    public class MitsubishiModelEnhancedTests
    {
        // ═══════════════════════════════════════════
        //  型号枚举
        // ═══════════════════════════════════════════

        [Theory]
        [InlineData(MitsubishiModel.Qna_3E)]
        [InlineData(MitsubishiModel.Qna_2E)]
        [InlineData(MitsubishiModel.A_3E)]
        [InlineData(MitsubishiModel.A_1E)]
        [InlineData(MitsubishiModel.FX_3U)]
        [InlineData(MitsubishiModel.FX_5U)]
        [InlineData(MitsubishiModel.IQ_R)]
        [InlineData(MitsubishiModel.IQ_F)]
        [InlineData(MitsubishiModel.L_Series)]
        public void Model_AllDefined(MitsubishiModel model)
        {
            Assert.True(Enum.IsDefined(typeof(MitsubishiModel), model));
        }

        // ═══════════════════════════════════════════
        //  SLMP 命令码
        // ═══════════════════════════════════════════

        [Fact]
        public void SlmpCommands_BatchRead()
        {
            Assert.Equal(0x0401, SlmpCommands.BatchReadBit);
            Assert.Equal(0x0402, SlmpCommands.BatchReadWord);
        }

        [Fact]
        public void SlmpCommands_BatchWrite()
        {
            Assert.Equal(0x1401, SlmpCommands.BatchWriteBit);
            Assert.Equal(0x1402, SlmpCommands.BatchWriteWord);
        }

        [Fact]
        public void SlmpCommands_RandomRead()
        {
            Assert.Equal(0x0403, SlmpCommands.RandomReadBit);
            Assert.Equal(0x0404, SlmpCommands.RandomReadWord);
            Assert.Equal(0x0406, SlmpCommands.RandomReadMultiLength);
        }

        [Fact]
        public void SlmpCommands_RandomWrite()
        {
            Assert.Equal(0x1403, SlmpCommands.RandomWriteBit);
            Assert.Equal(0x1404, SlmpCommands.RandomWriteWord);
            Assert.Equal(0x1406, SlmpCommands.RandomWriteMultiLength);
        }

        [Fact]
        public void SlmpCommands_Control()
        {
            Assert.Equal(0x1001, SlmpCommands.Run);
            Assert.Equal(0x1002, SlmpCommands.Stop);
            Assert.Equal(0x0101, SlmpCommands.ReadType);
            Assert.Equal(0x0102, SlmpCommands.ReadStatus);
        }

        // ═══════════════════════════════════════════
        //  MC 常量
        // ═══════════════════════════════════════════

        [Fact]
        public void McConstants_DefaultValues()
        {
            Assert.Equal(6000, McConstants.DefaultTcpPort);
            Assert.Equal(5551, McConstants.DefaultUdpPort);
            Assert.Equal(11, McConstants.Mc3EHeaderLength);
            Assert.Equal(0x5000, McConstants.SubHeader);
        }

        [Fact]
        public void McConstants_MaxLimits()
        {
            Assert.Equal(7168, McConstants.MaxBatchReadBits);
            Assert.Equal(960, McConstants.MaxBatchReadWords);
            Assert.Equal(7168, McConstants.MaxBatchWriteBits);
            Assert.Equal(960, McConstants.MaxBatchWriteWords);
            Assert.Equal(192, McConstants.MaxRandomReadAddresses);
        }

        [Fact]
        public void McConstants_FxLimits()
        {
            Assert.Equal(7999, McConstants.Fx3uMaxD);
            Assert.Equal(32767, McConstants.Fx5uMaxD);
        }

        // ═══════════════════════════════════════════
        //  SLMP 错误码
        // ═══════════════════════════════════════════

        [Theory]
        [InlineData(0x0000, "正常完成")]
        [InlineData(0xC001, "不支持的功能码")]
        [InlineData(0xC003, "地址超出范围")]
        [InlineData(0xC004, "数据长度超出范围")]
        [InlineData(0xC006, "PLC 当前模式不支持")]
        [InlineData(0xC007, "远程密码错误")]
        [InlineData(0xC023, "通信超时")]
        [InlineData(0xC024, "路由参数错误")]
        [InlineData(0xC051, "标签未找到")]
        [InlineData(0xCF70, "从站无响应")]
        [InlineData(0xFFFF, "未知错误")]
        public void SlmpErrorCodes_Description(ushort code, string expected)
        {
            Assert.Contains(expected, SlmpErrorCodes.GetDescription(code));
        }

        // ═══════════════════════════════════════════
        //  Mc3EAddress 解析
        // ═══════════════════════════════════════════

        [Fact]
        public void Mc3EAddress_ParseD100()
        {
            var (sub, addr) = Mc3EAddressParser.Parse("D100");
            Assert.Equal(0xA8, sub);
            Assert.Equal(100u, addr);
        }

        [Fact]
        public void Mc3EAddress_ParseM0()
        {
            var (sub, addr) = Mc3EAddressParser.Parse("M0");
            Assert.Equal(0x90, sub);
            Assert.Equal(0u, addr);
        }

        [Fact]
        public void Mc3EAddress_ParseX0_Hex()
        {
            var (sub, addr) = Mc3EAddressParser.Parse("X1F");
            Assert.Equal(0x9C, sub);
            Assert.Equal(0x1Fu, addr);
        }

        [Fact]
        public void Mc3EAddress_ParseY10_Hex()
        {
            var (sub, addr) = Mc3EAddressParser.Parse("Y10");
            Assert.Equal(0x9D, sub);
            Assert.Equal(0x10u, addr);
        }

        [Fact]
        public void Mc3EAddress_ParseTS100()
        {
            var (sub, addr) = Mc3EAddressParser.Parse("TS100");
            Assert.Equal(0xC1, sub);
            Assert.Equal(100u, addr);
        }

        [Fact]
        public void Mc3EAddress_ParseSM100()
        {
            var (sub, addr) = Mc3EAddressParser.Parse("SM100");
            Assert.Equal(0x91, sub);
            Assert.Equal(100u, addr);
        }

        [Fact]
        public void Mc3EAddress_ParseZR0()
        {
            var (sub, addr) = Mc3EAddressParser.Parse("ZR0");
            Assert.Equal(0xB0, sub);
            Assert.Equal(0u, addr);
        }

        // ═══════════════════════════════════════════
        //  IsBitAddress
        // ═══════════════════════════════════════════

        [Theory]
        [InlineData("M0", true)]
        [InlineData("X0", true)]
        [InlineData("Y0", true)]
        [InlineData("S100", true)]
        [InlineData("TS100", true)]
        [InlineData("B1", true)]
        [InlineData("D100", false)]
        [InlineData("W100", false)]
        [InlineData("R100", false)]
        [InlineData("SD100", false)]
        [InlineData("ZR0", false)]
        public void IsBitAddress_Tests(string address, bool expected)
        {
            Assert.Equal(expected, Mc3EAddressParser.IsBitAddress(address));
        }

        // ═══════════════════════════════════════════
        //  McFrameType
        // ═══════════════════════════════════════════

        [Theory]
        [InlineData(McFrameType.MC3E_Binary)]
        [InlineData(McFrameType.MC3E_Ascii)]
        [InlineData(McFrameType.MC4E)]
        [InlineData(McFrameType.A1E)]
        public void FrameType_AllDefined(McFrameType type)
        {
            Assert.True(Enum.IsDefined(typeof(McFrameType), type));
        }
    }
}
