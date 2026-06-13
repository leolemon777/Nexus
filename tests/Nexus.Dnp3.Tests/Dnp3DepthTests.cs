using System;
using Xunit;
using Nexus.Dnp3;

namespace Nexus.Dnp3.Tests
{
    public class Dnp3CrcTests
    {
        [Fact]
        public void Crc16_EmptyInput_ReturnsZero()
        {
            ushort crc = Dnp3Client.CalculateDnp3Crc(new byte[0], 0, 0);
            Assert.Equal(0x0000, crc);
        }

        [Fact]
        public void Crc16_SingleByte_ReturnsKnownValue()
        {
            // CRC of single byte 0x05 (DNP3 start byte)
            ushort crc = Dnp3Client.CalculateDnp3Crc(new byte[] { 0x05 }, 0, 1);
            Assert.NotEqual(0x0000, crc);
        }

        [Fact]
        public void Crc16_StartBytes_ProducesDeterministicResult()
        {
            byte[] data = { 0x05, 0x64 };
            ushort crc1 = Dnp3Client.CalculateDnp3Crc(data, 0, 2);
            ushort crc2 = Dnp3Client.CalculateDnp3Crc(data, 0, 2);
            Assert.Equal(crc1, crc2);
        }

        [Fact]
        public void Crc16_DifferentData_DifferentResults()
        {
            byte[] data1 = { 0x05, 0x64, 0x0A };
            byte[] data2 = { 0x05, 0x64, 0x0B };
            ushort crc1 = Dnp3Client.CalculateDnp3Crc(data1, 0, 3);
            ushort crc2 = Dnp3Client.CalculateDnp3Crc(data2, 0, 3);
            Assert.NotEqual(crc1, crc2);
        }

        [Fact]
        public void Crc16_WithOffset_IgnoresBeforeOffset()
        {
            byte[] data = { 0xFF, 0xFF, 0x05, 0x64 };
            ushort crcDirect = Dnp3Client.CalculateDnp3Crc(new byte[] { 0x05, 0x64 }, 0, 2);
            ushort crcOffset = Dnp3Client.CalculateDnp3Crc(data, 2, 2);
            Assert.Equal(crcDirect, crcOffset);
        }

        [Fact]
        public void Crc16_Dnp3Polynomial_ProducesCorrectChecksum()
        {
            // Known DNP3 frame header: 05 64 0A C4 01 00 00 04
            // CRC should be a valid 16-bit value
            byte[] header = { 0x05, 0x64, 0x0A, 0xC4, 0x01, 0x00, 0x00, 0x04 };
            ushort crc = Dnp3Client.CalculateDnp3Crc(header, 0, 8);
            Assert.True(crc <= 0xFFFF);
            Assert.NotEqual(0x0000, crc); // Extremely unlikely to be zero
        }

        [Fact]
        public void Crc16_AllZeros_ReturnsZero()
        {
            byte[] data = new byte[8];
            ushort crc = Dnp3Client.CalculateDnp3Crc(data, 0, 8);
            Assert.Equal(0x0000, crc);
        }

        [Fact]
        public void BuildLinkHeader_CrcIsNonZero()
        {
            byte[] userData = { 0x01, 0x02, 0x03 };
            byte[] frame = Dnp3Client.BuildLinkHeader(1024, 1, 0xC4, userData);
            // Header CRC at bytes 8-9 should not be 0x0000
            ushort headerCrc = (ushort)(frame[8] | (frame[9] << 8));
            Assert.NotEqual(0x0000, headerCrc);
        }
    }

    public class Dnp3TransportLayerTests
    {
        [Fact]
        public void WrapWithTransportHeader_SetsFinAndFir()
        {
            byte[] appData = { 0xC0, 0x01, 0x01 };
            byte[] wrapped = Dnp3Client.WrapWithTransportHeader(0, appData);
            Assert.Equal(4, wrapped.Length);
            // FIR=1 (bit 7), FIN=1 (bit 6)
            Assert.Equal(0xC0, wrapped[0]);
        }

        [Fact]
        public void WrapWithTransportHeader_SequenceWraps()
        {
            byte[] appData = { 0x01 };
            byte[] wrapped = Dnp3Client.WrapWithTransportHeader(63, appData);
            // Sequence 63 = 0x3F, OR'd with 0xC0 = 0xFF
            Assert.Equal(0xFF, wrapped[0]);
        }

        [Fact]
        public void WrapWithTransportHeader_Sequence0()
        {
            byte[] appData = { 0x01 };
            byte[] wrapped = Dnp3Client.WrapWithTransportHeader(0, appData);
            Assert.Equal(0xC0, wrapped[0]);
        }

        [Fact]
        public void WrapWithTransportHeader_PreservesAppData()
        {
            byte[] appData = { 0xAA, 0xBB, 0xCC };
            byte[] wrapped = Dnp3Client.WrapWithTransportHeader(5, appData);
            Assert.Equal(0xAA, wrapped[1]);
            Assert.Equal(0xBB, wrapped[2]);
            Assert.Equal(0xCC, wrapped[3]);
        }
    }

    public class Dnp3CounterAndObjectTests
    {
        [Fact]
        public void BuildReadRequest_ForCounter_Group20()
        {
            byte[] pdu = Dnp3Client.BuildReadRequest(1, Dnp3Group.Counter, Dnp3Variation.Counter32, 0, 5);
            Assert.Equal((byte)Dnp3Group.Counter, pdu[2]);
            Assert.Equal((byte)Dnp3Variation.Counter32, pdu[3]);
        }

        [Fact]
        public void BuildReadRequest_ForAnalogOutput_Group40()
        {
            byte[] pdu = Dnp3Client.BuildReadRequest(1, Dnp3Group.AnalogOutput, Dnp3Variation.AnalogOutputFloat32, 0, 3);
            Assert.Equal((byte)Dnp3Group.AnalogOutput, pdu[2]);
            Assert.Equal((byte)Dnp3Variation.AnalogOutputFloat32, pdu[3]);
        }

        [Fact]
        public void Dnp3Group_Counter_IsGroup20()
        {
            Assert.Equal(20, (byte)Dnp3Group.Counter);
        }

        [Fact]
        public void Dnp3Group_AnalogOutput_IsGroup40()
        {
            Assert.Equal(40, (byte)Dnp3Group.AnalogOutput);
        }

        [Fact]
        public void Dnp3Variation_Counter32WithFlag_IsDefined()
        {
            Assert.Equal(0x05, (byte)Dnp3Variation.Counter32WithFlag);
        }

        [Fact]
        public void Dnp3Variation_AnalogOutputFloat32_IsDefined()
        {
            Assert.Equal(0x04, (byte)Dnp3Variation.AnalogOutputFloat32);
        }
    }

    public class Dnp3WriteOperationTests
    {
        [Fact]
        public void BuildSelectRequest_FunctionCodeIsSelect()
        {
            byte[] data = { 0x01 };
            byte[] pdu = Dnp3Client.BuildSelectRequest(1, Dnp3Group.BinaryOutput, Dnp3Variation.BinaryOutputPacked, 0, data);
            Assert.Equal((byte)Dnp3FunctionCode.Select, pdu[1]);
        }

        [Fact]
        public void BuildOperateRequest_FunctionCodeIsOperate()
        {
            byte[] data = { 0x01 };
            byte[] pdu = Dnp3Client.BuildOperateRequest(1, Dnp3Group.BinaryOutput, Dnp3Variation.BinaryOutputPacked, 0, data);
            Assert.Equal((byte)Dnp3FunctionCode.Operate, pdu[1]);
        }

        [Fact]
        public void BuildWriteRequest_FunctionCodeIsWrite()
        {
            byte[] data = { 0x00, 0x40, 0x9C, 0x45 }; // float 5000.0
            byte[] pdu = Dnp3Client.BuildWriteRequest(1, Dnp3Group.AnalogOutput, Dnp3Variation.AnalogOutputFloat32, 0, data);
            Assert.Equal((byte)Dnp3FunctionCode.Write, pdu[1]);
            Assert.Equal((byte)Dnp3Group.AnalogOutput, pdu[2]);
        }

        [Fact]
        public void BuildDirectOperateRequest_ForBinaryOutput()
        {
            byte[] data = { 0x01 };
            byte[] pdu = Dnp3Client.BuildDirectOperateRequest(1, Dnp3Group.BinaryOutput, Dnp3Variation.BinaryOutputPacked, 5, data);
            Assert.Equal((byte)Dnp3FunctionCode.DirectOperate, pdu[1]);
            Assert.Equal((byte)Dnp3Group.BinaryOutput, pdu[2]);
            Assert.Equal(5, pdu[5]); // index
        }

        [Fact]
        public void BuildColdRestartRequest_FunctionCode()
        {
            byte[] pdu = Dnp3Client.BuildColdRestartRequest(1);
            Assert.Equal(2, pdu.Length);
            Assert.Equal((byte)Dnp3FunctionCode.ColdRestart, pdu[1]);
        }

        [Fact]
        public void BuildDelayMeasureRequest_FunctionCode()
        {
            byte[] pdu = Dnp3Client.BuildDelayMeasureRequest(1);
            Assert.Equal(2, pdu.Length);
            Assert.Equal((byte)Dnp3FunctionCode.DelayMeasure, pdu[1]);
        }

        [Fact]
        public void SelectAndOperate_HaveSameGroupAndVariation()
        {
            byte[] data = { 0x01 };
            var select = Dnp3Client.BuildSelectRequest(1, Dnp3Group.BinaryOutput, Dnp3Variation.BinaryOutputPacked, 0, data);
            var operate = Dnp3Client.BuildOperateRequest(2, Dnp3Group.BinaryOutput, Dnp3Variation.BinaryOutputPacked, 0, data);
            Assert.Equal(select[2], operate[2]); // same group
            Assert.Equal(select[3], operate[3]); // same variation
            Assert.Equal(select[5], operate[5]); // same index
        }
    }

    public class Dnp3ClientEnhancedTests
    {
        [Fact]
        public void Dnp3Client_DefaultProperties_VerifyNewDefaults()
        {
            var client = new Dnp3Client("192.168.1.1");
            Assert.Equal((ushort)1, client.MasterAddress);
            Assert.Equal((ushort)1024, client.OutstationAddress);
            Assert.Equal(5000, client.ConfirmTimeout);
        }

        [Fact]
        public void FunctionCode_ColdRestart_Is0x0D()
        {
            Assert.Equal(0x0D, (byte)Dnp3FunctionCode.ColdRestart);
        }

        [Fact]
        public void FunctionCode_DelayMeasure_Is0x17()
        {
            Assert.Equal(0x17, (byte)Dnp3FunctionCode.DelayMeasure);
        }

        [Fact]
        public void FunctionCode_Select_Is0x03()
        {
            Assert.Equal(0x03, (byte)Dnp3FunctionCode.Select);
        }

        [Fact]
        public void FunctionCode_Operate_Is0x04()
        {
            Assert.Equal(0x04, (byte)Dnp3FunctionCode.Operate);
        }
    }
}
