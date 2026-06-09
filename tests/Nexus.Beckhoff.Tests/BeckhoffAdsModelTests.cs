using Xunit;
using Nexus.Beckhoff;

namespace Nexus.Beckhoff.Tests
{
    public class BeckhoffAdsModelTests
    {
        [Fact]
        public void AdsCommand_Values()
        {
            Assert.Equal((ushort)0x0001, (ushort)AdsCommand.ReadDeviceInfo);
            Assert.Equal((ushort)0x0002, (ushort)AdsCommand.Read);
            Assert.Equal((ushort)0x0003, (ushort)AdsCommand.Write);
            Assert.Equal((ushort)0x0004, (ushort)AdsCommand.ReadState);
            Assert.Equal((ushort)0x0005, (ushort)AdsCommand.WriteControl);
            Assert.Equal((ushort)0x0006, (ushort)AdsCommand.AddDeviceNotification);
            Assert.Equal((ushort)0x0007, (ushort)AdsCommand.DeleteDeviceNotification);
            Assert.Equal((ushort)0x0008, (ushort)AdsCommand.DeviceNotification);
            Assert.Equal((ushort)0x0009, (ushort)AdsCommand.ReadWrite);
        }

        [Fact]
        public void AdsDataType_Values()
        {
            Assert.Equal((uint)0x0001, (uint)AdsDataType.Bit);
            Assert.Equal((uint)0x0002, (uint)AdsDataType.Int16);
            Assert.Equal((uint)0x0004, (uint)AdsDataType.Int32);
            Assert.Equal((uint)0x0008, (uint)AdsDataType.Float32);
            Assert.Equal((uint)0x0009, (uint)AdsDataType.Float64);
            Assert.Equal((uint)0x001E, (uint)AdsDataType.String);
        }

        [Fact]
        public void AdsErrorCode_Values()
        {
            Assert.Equal((uint)0, (uint)AdsErrorCode.NoError);
            Assert.Equal((uint)0x0015, (uint)AdsErrorCode.SymbolNotFound);
            Assert.Equal((uint)0x0006, (uint)AdsErrorCode.InvalidIndexOffset);
        }

        [Fact]
        public void Constants_PortValues()
        {
            Assert.Equal(851, BeckhoffAdsConstants.PortTc3Plc);
            Assert.Equal(801, BeckhoffAdsConstants.PortTc2Plc);
            Assert.Equal(48898, BeckhoffAdsConstants.DefaultPort);
        }

        [Fact]
        public void Constants_AmsHeaderLength()
        {
            Assert.Equal(32, BeckhoffAdsConstants.AmsHeaderLength);
            Assert.Equal(6, BeckhoffAdsConstants.TcpHeaderLength);
        }

        [Fact]
        public void Constants_SymbolIndexGroup()
        {
            Assert.Equal(0xF003u, BeckhoffAdsConstants.SymbolIndexGroup);
        }

        [Theory]
        [InlineData(BeckhoffPlcModel.Tc2)]
        [InlineData(BeckhoffPlcModel.Tc3)]
        [InlineData(BeckhoffPlcModel.Cx5020)]
        [InlineData(BeckhoffPlcModel.Cx5130)]
        public void PlcModel_EnumDefined(BeckhoffPlcModel model)
        {
            Assert.True(Enum.IsDefined(typeof(BeckhoffPlcModel), model));
        }
    }
}
