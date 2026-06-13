using Xunit;
using Nexus.Iec61850;

namespace Nexus.Iec61850.Tests
{
    public class Iec61850ModelTests
    {
        [Fact]
        public void FunctionalConstraint_Values()
        {
            Assert.Equal(0x01, (byte)FunctionalConstraint.ST);
            Assert.Equal(0x02, (byte)FunctionalConstraint.MX);
            Assert.Equal(0x03, (byte)FunctionalConstraint.SP);
            Assert.Equal(0x05, (byte)FunctionalConstraint.CF);
            Assert.Equal(0x06, (byte)FunctionalConstraint.DC);
            Assert.Equal(0x0D, (byte)FunctionalConstraint.CO);
        }

        [Fact]
        public void Iec61850Constants_Ports()
        {
            Assert.Equal(102, Iec61850Constants.DefaultMmsPort);
            Assert.Equal(102, Iec61850Constants.DefaultGoosePort);
            Assert.Equal(65000, Iec61850Constants.MaxMmsPduSize);
        }

        [Fact]
        public void Iec61850Constants_EtherTypes()
        {
            Assert.Equal(0x88B8, Iec61850Constants.GooseEtherType);
            Assert.Equal(0x88BA, Iec61850Constants.SvEtherType);
        }

        [Fact]
        public void ReportTriggerOptions_Flags()
        {
            Assert.Equal(0x0001, (ushort)ReportTriggerOptions.DataChanged);
            Assert.Equal(0x0002, (ushort)ReportTriggerOptions.QualityChanged);
            Assert.Equal(0x0004, (ushort)ReportTriggerOptions.DataUpdate);
            Assert.Equal(0x0008, (ushort)ReportTriggerOptions.Integrity);
            Assert.Equal(0x0010, (ushort)ReportTriggerOptions.GeneralInterrogation);
        }

        [Theory]
        [InlineData(IecControlModel.DirectWithNormalSecurity)]
        [InlineData(IecControlModel.SboWithNormalSecurity)]
        [InlineData(IecControlModel.DirectWithEnhancedSecurity)]
        [InlineData(IecControlModel.SboWithEnhancedSecurity)]
        public void ControlModel_AllDefined(IecControlModel model)
        {
            Assert.True(Enum.IsDefined(typeof(IecControlModel), model));
        }

        [Theory]
        [InlineData(0, "正常完成")]
        [InlineData(1, "参数不匹配")]
        [InlineData(2, "对象不存在")]
        [InlineData(4, "对象不支持")]
        [InlineData(11, "控制已选择")]
        [InlineData(13, "控制被拒绝")]
        [InlineData(19, "连接丢失")]
        [InlineData(99, "未知错误")]
        public void ServiceError_Description(int code, string expected)
        {
            Assert.Contains(expected, Iec61850ErrorCodes.GetServiceErrorDescription(code));
        }
    }

    public class Iec61850ClientTests
    {
        [Fact]
        public void BuildObjectReference_Simple()
        {
            string ref_ = Iec61850Client.BuildObjectReference("LD0", "LLN0", "Beh");
            Assert.Equal("LD0/LLN0.Beh", ref_);
        }

        [Fact]
        public void BuildObjectReference_WithDa()
        {
            string ref_ = Iec61850Client.BuildObjectReference("LD0", "GGIO1", "Ind1", "stVal");
            Assert.Equal("LD0/GGIO1.Ind1.stVal", ref_);
        }

        [Fact]
        public void ParseObjectReference_Simple()
        {
            var (ld, ln, data, da) = Iec61850Client.ParseObjectReference("LD0/LLN0.Beh");
            Assert.Equal("LD0", ld);
            Assert.Equal("LLN0", ln);
            Assert.Equal("Beh", data);
            Assert.Null(da);
        }

        [Fact]
        public void ParseObjectReference_WithDa()
        {
            var (ld, ln, data, da) = Iec61850Client.ParseObjectReference("LD0/GGIO1.Ind1.stVal");
            Assert.Equal("LD0", ld);
            Assert.Equal("GGIO1", ln);
            Assert.Equal("Ind1", data);
            Assert.Equal("stVal", da);
        }

        [Fact]
        public void ParseObjectReference_Invalid()
        {
            Assert.Throws<ArgumentException>(() => Iec61850Client.ParseObjectReference(""));
            Assert.Throws<FormatException>(() => Iec61850Client.ParseObjectReference("LD0"));
        }

        [Fact]
        public void BuildGetDataValuesRequest_Format()
        {
            byte[] req = Iec61850Client.BuildGetDataValuesRequest("LD0", "GGIO1", "Ind1", FunctionalConstraint.ST);
            Assert.Equal(102, req.Length);
            Assert.Equal(0x03, req[0]); // GetDataValues service
            Assert.Equal((byte)FunctionalConstraint.ST, req[101]);
        }

        [Fact]
        public void BuildSetDataValuesRequest_Format()
        {
            byte[] value = new byte[] { 0x01 };
            byte[] req = Iec61850Client.BuildSetDataValuesRequest("LD0", "GGIO1", "Ind1", FunctionalConstraint.SP, value);
            Assert.True(req.Length > 102);
            Assert.Equal(0x04, req[0]); // SetDataValues service
            Assert.Equal((byte)FunctionalConstraint.SP, req[101]);
        }

        [Fact]
        public void Client_DefaultProperties()
        {
            var client = new Iec61850Client("192.168.1.1");
            Assert.Equal("LD0", client.LogicalDevice);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Client_DisposeDoesNotThrow()
        {
            var client = new Iec61850Client("127.0.0.1");
            client.Dispose();
        }

        [Fact]
        public void Client_LogicalDevice_DefaultIsLD0()
        {
            var client = new Iec61850Client("10.0.0.1");
            Assert.Equal("LD0", client.LogicalDevice);
        }

        [Fact]
        public void BuildObjectReference_EmptyDa_ReturnsNoSuffix()
        {
            string ref_ = Iec61850Client.BuildObjectReference("LD1", "MMXU1", "TotW");
            Assert.Equal("LD1/MMXU1.TotW", ref_);
        }

        [Fact]
        public void ParseObjectReference_ThreeSegments()
        {
            var (ld, ln, data, da) = Iec61850Client.ParseObjectReference("IED1/LPHD1.PhyNam");
            Assert.Equal("IED1", ld);
            Assert.Equal("LPHD1", ln);
            Assert.Equal("PhyNam", data);
            Assert.Null(da);
        }

        [Fact]
        public void BuildGetDataValuesRequest_DifferentFC()
        {
            byte[] req = Iec61850Client.BuildGetDataValuesRequest("LD0", "MMXU1", "TotW", FunctionalConstraint.MX);
            Assert.Equal((byte)FunctionalConstraint.MX, req[101]);
        }

        [Fact]
        public void FunctionalConstraint_AllDefined()
        {
            Assert.True(Enum.IsDefined(typeof(FunctionalConstraint), FunctionalConstraint.ST));
            Assert.True(Enum.IsDefined(typeof(FunctionalConstraint), FunctionalConstraint.MX));
            Assert.True(Enum.IsDefined(typeof(FunctionalConstraint), FunctionalConstraint.SP));
            Assert.True(Enum.IsDefined(typeof(FunctionalConstraint), FunctionalConstraint.CF));
            Assert.True(Enum.IsDefined(typeof(FunctionalConstraint), FunctionalConstraint.DC));
            Assert.True(Enum.IsDefined(typeof(FunctionalConstraint), FunctionalConstraint.CO));
        }

        [Fact]
        public void Iec61850Constants_DefaultMmsPort_Is102()
        {
            Assert.Equal(102, Iec61850Constants.DefaultMmsPort);
        }

        [Fact]
        public void ReportTriggerOptions_CombinedFlags()
        {
            ushort combined = (ushort)(ReportTriggerOptions.DataChanged | ReportTriggerOptions.QualityChanged);
            Assert.Equal(0x0003, combined);
        }

        [Fact]
        public void IecControlModel_AllFourValues()
        {
            Assert.Equal(4, Enum.GetValues(typeof(IecControlModel)).Length);
        }
    }
}
