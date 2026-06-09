using Xunit;
using Nexus.AllenBradley;

namespace Nexus.AllenBradley.Tests
{
    public class CipModelTests
    {
        // ═══════════════════════════════════════════
        //  CIP 数据类型
        // ═══════════════════════════════════════════

        [Fact]
        public void CipDataType_Values()
        {
            Assert.Equal(0x00C1, (ushort)CipDataType.Bool);
            Assert.Equal(0x00C2, (ushort)CipDataType.Sint);
            Assert.Equal(0x00C3, (ushort)CipDataType.Int);
            Assert.Equal(0x00C4, (ushort)CipDataType.Dint);
            Assert.Equal(0x00C5, (ushort)CipDataType.Lint);
            Assert.Equal(0x00C6, (ushort)CipDataType.Usint);
            Assert.Equal(0x00C7, (ushort)CipDataType.Uint);
            Assert.Equal(0x00C8, (ushort)CipDataType.Udint);
            Assert.Equal(0x00C9, (ushort)CipDataType.Ulint);
            Assert.Equal(0x00CA, (ushort)CipDataType.Real);
            Assert.Equal(0x00CB, (ushort)CipDataType.Lreal);
            Assert.Equal(0x00D0, (ushort)CipDataType.String);
            Assert.Equal(0x02A0, (ushort)CipDataType.Struct);
        }

        // ═══════════════════════════════════════════
        //  CIP 服务码
        // ═══════════════════════════════════════════

        [Fact]
        public void CipService_Values()
        {
            Assert.Equal(0x4C, (byte)CipService.Read);
            Assert.Equal(0x52, (byte)CipService.ReadFragmented);
            Assert.Equal(0x4D, (byte)CipService.Write);
            Assert.Equal(0x53, (byte)CipService.WriteFragmented);
            Assert.Equal(0x0A, (byte)CipService.MultipleService);
            Assert.Equal(0x54, (byte)CipService.ForwardOpen);
            Assert.Equal(0x4E, (byte)CipService.ForwardClose);
        }

        // ═══════════════════════════════════════════
        //  ENIP 命令码
        // ═══════════════════════════════════════════

        [Fact]
        public void EnipCommand_Values()
        {
            Assert.Equal(0x0001, (ushort)EnipCommand.Nop);
            Assert.Equal(0x0063, (ushort)EnipCommand.ListIdentity);
            Assert.Equal(0x006F, (ushort)EnipCommand.SendRRData);
            Assert.Equal(0x0070, (ushort)EnipCommand.SendUnitData);
        }

        // ═══════════════════════════════════════════
        //  CIP 常量
        // ═══════════════════════════════════════════

        [Fact]
        public void CipConstants_DefaultValues()
        {
            Assert.Equal(44818, CipConstants.DefaultPort);
            Assert.Equal(24, CipConstants.EnipHeaderLength);
            Assert.Equal(508, CipConstants.DefaultMaxPduSize);
            Assert.Equal(82, CipConstants.MaxTagNameLength);
            Assert.Equal(0x91, CipConstants.SymbolicSegmentType);
        }

        // ═══════════════════════════════════════════
        //  CIP 错误码
        // ═══════════════════════════════════════════

        [Theory]
        [InlineData(0x00, "成功")]
        [InlineData(0x01, "连接失败")]
        [InlineData(0x04, "路径段错误")]
        [InlineData(0x06, "部分传输")]
        [InlineData(0x08, "服务不支持")]
        [InlineData(0x0C, "属性不支持 — 标签只读")]
        [InlineData(0x11, "忙")]
        [InlineData(0xFF, "未知错误")]
        public void CipErrorCodes_Description(byte status, string expected)
        {
            Assert.Contains(expected, CipErrorCodes.GetDescription(status));
        }

        // ═══════════════════════════════════════════
        //  PLC 型号
        // ═══════════════════════════════════════════

        [Theory]
        [InlineData(AbPlcModel.ControlLogix5570)]
        [InlineData(AbPlcModel.ControlLogix5580)]
        [InlineData(AbPlcModel.CompactLogix5380)]
        [InlineData(AbPlcModel.MicroLogix1400)]
        [InlineData(AbPlcModel.Micro800)]
        [InlineData(AbPlcModel.PLC5)]
        [InlineData(AbPlcModel.SLC500)]
        public void AbPlcModel_AllDefined(AbPlcModel model)
        {
            Assert.True(Enum.IsDefined(typeof(AbPlcModel), model));
        }
    }
}
