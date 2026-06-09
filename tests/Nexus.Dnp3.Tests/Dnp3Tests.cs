using Xunit;
using Nexus.Dnp3;

namespace Nexus.Dnp3.Tests
{
    public class Dnp3ModelTests
    {
        // ═══════════════════════════════════════════
        //  功能码
        // ═══════════════════════════════════════════

        [Fact]
        public void FunctionCode_Values()
        {
            Assert.Equal(0x00, (byte)Dnp3FunctionCode.Confirm);
            Assert.Equal(0x01, (byte)Dnp3FunctionCode.Read);
            Assert.Equal(0x02, (byte)Dnp3FunctionCode.Write);
            Assert.Equal(0x05, (byte)Dnp3FunctionCode.DirectOperate);
            Assert.Equal(0x07, (byte)Dnp3FunctionCode.Freeze);
            Assert.Equal(0x0D, (byte)Dnp3FunctionCode.ColdRestart);
            Assert.Equal(0x81, (byte)Dnp3FunctionCode.Response);
            Assert.Equal(0x82, (byte)Dnp3FunctionCode.UnsolicitedResponse);
        }

        // ═══════════════════════════════════════════
        //  数据组
        // ═══════════════════════════════════════════

        [Fact]
        public void Dnp3Group_Values()
        {
            Assert.Equal(1, (byte)Dnp3Group.BinaryInput);
            Assert.Equal(2, (byte)Dnp3Group.BinaryInputEvent);
            Assert.Equal(10, (byte)Dnp3Group.BinaryOutput);
            Assert.Equal(20, (byte)Dnp3Group.Counter);
            Assert.Equal(30, (byte)Dnp3Group.AnalogInput);
            Assert.Equal(40, (byte)Dnp3Group.AnalogOutput);
            Assert.Equal(50, (byte)Dnp3Group.TimeAndDate);
            Assert.Equal(60, (byte)Dnp3Group.ClassData);
            Assert.Equal(80, (byte)Dnp3Group.DeviceInformation);
        }

        // ═══════════════════════════════════════════
        //  变体号
        // ═══════════════════════════════════════════

        [Fact]
        public void Variation_Values()
        {
            Assert.Equal(0x01, (byte)Dnp3Variation.BinaryInputPacked);
            Assert.Equal(0x04, (byte)Dnp3Variation.AnalogInputFloat32);
            Assert.Equal(0x01, (byte)Dnp3Variation.AnalogInputInt16);
            Assert.Equal(0x02, (byte)Dnp3Variation.AnalogInputInt32);
            Assert.Equal(0x05, (byte)Dnp3Variation.AnalogInputFloat64);
            Assert.Equal(0x01, (byte)Dnp3Variation.Counter32);
        }

        // ═══════════════════════════════════════════
        //  常量
        // ═══════════════════════════════════════════

        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(20000, Dnp3Constants.DefaultTcpPort);
            Assert.Equal(9600, Dnp3Constants.DefaultBaudRate);
            Assert.Equal(0x05, Dnp3Constants.StartByte1);
            Assert.Equal(0x64, Dnp3Constants.StartByte2);
            Assert.Equal(10, Dnp3Constants.LinkHeaderLength);
            Assert.Equal(2048, Dnp3Constants.MaxAppDataSize);
            Assert.Equal(1, Dnp3Constants.DefaultMasterAddress);
            Assert.Equal(1024, Dnp3Constants.DefaultOutstationAddress);
        }

        // ═══════════════════════════════════════════
        //  IIN 标志
        // ═══════════════════════════════════════════

        [Theory]
        [InlineData(Dnp3IinFlags.None)]
        [InlineData(Dnp3IinFlags.DeviceRestart)]
        [InlineData(Dnp3IinFlags.NeedTime)]
        [InlineData(Dnp3IinFlags.DeviceTrouble)]
        [InlineData(Dnp3IinFlags.DataOverflow)]
        public void IinFlags_AllDefined(Dnp3IinFlags flags)
        {
            Assert.True(Enum.IsDefined(typeof(Dnp3IinFlags), flags));
        }

        // ═══════════════════════════════════════════
        //  错误码
        // ═══════════════════════════════════════════

        [Fact]
        public void IinDescription_Normal()
        {
            Assert.Equal("正常", Dnp3ErrorCodes.GetIinDescription(0x0000));
        }

        [Fact]
        public void IinDescription_MultipleFlags()
        {
            string desc = Dnp3ErrorCodes.GetIinDescription(0x0006); // DeviceRestart + NeedTime
            Assert.Contains("设备重启", desc);
            Assert.Contains("需要时间同步", desc);
        }

        [Theory]
        [InlineData(0, "正常")]
        [InlineData(1, "功能码不支持")]
        [InlineData(2, "对象未知")]
        [InlineData(4, "数据溢出")]
        [InlineData(6, "对象只读")]
        [InlineData(99, "未知错误")]
        public void AppError_Description(byte code, string expected)
        {
            Assert.Contains(expected, Dnp3ErrorCodes.GetAppErrorDescription(code));
        }
    }

    public class Dnp3ClientTests
    {
        [Fact]
        public void BuildReadRequest_Format()
        {
            byte[] pdu = Dnp3Client.BuildReadRequest(1, Dnp3Group.AnalogInput, Dnp3Variation.AnalogInputFloat32, 0, 10);
            Assert.True(pdu.Length >= 8);
            Assert.Equal((byte)Dnp3FunctionCode.Read, pdu[1]); // FC
            Assert.Equal((byte)Dnp3Group.AnalogInput, pdu[2]); // Group
        }

        [Fact]
        public void BuildDirectOperateRequest_Format()
        {
            byte[] data = new byte[] { 0x00, 0x01, 0x02, 0x03 };
            byte[] pdu = Dnp3Client.BuildDirectOperateRequest(2, Dnp3Group.AnalogOutput, Dnp3Variation.AnalogInputFloat32, 5, data);
            Assert.True(pdu.Length >= 8);
            Assert.Equal((byte)Dnp3FunctionCode.DirectOperate, pdu[1]);
        }

        [Fact]
        public void BuildLinkHeader_Format()
        {
            byte[] userData = new byte[] { 0x01, 0x02, 0x03 };
            byte[] frame = Dnp3Client.BuildLinkHeader(1024, 1, 0xC4, userData);
            Assert.Equal(Dnp3Constants.StartByte1, frame[0]);
            Assert.Equal(Dnp3Constants.StartByte2, frame[1]);
            Assert.Equal(0xC4, frame[3]); // Control
            // Dest = 1024
            Assert.Equal((byte)(1024 & 0xFF), frame[4]);
            Assert.Equal((byte)((1024 >> 8) & 0xFF), frame[5]);
            // Src = 1
            Assert.Equal(1, frame[6]);
            Assert.Equal(0, frame[7]);
        }

        [Fact]
        public void Dnp3Client_DefaultProperties()
        {
            var client = new Dnp3Client("192.168.1.1");
            Assert.Equal((ushort)1, client.MasterAddress);
            Assert.Equal((ushort)1024, client.OutstationAddress);
        }
    }
}
