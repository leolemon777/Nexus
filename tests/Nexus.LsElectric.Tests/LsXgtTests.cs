using Xunit;
using Nexus.LsElectric;
using System;
using System.Text;

namespace Nexus.LsElectric.Tests
{
    public class LsXgtTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new LsXgtClient("192.168.1.1");
            Assert.Equal("192.168.1.1", client.IpAddress);
            Assert.Equal(2004, client.Port);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new LsXgtClient("192.168.1.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new LsXgtClient("192.168.1.1");
            client.Dispose();
        }

        [Fact]
        public void BuildWriteRequest_WithUInt64Payload_UsesEightDataBytes()
        {
            byte[] frame = LsXgtTcpClient.BuildWriteRequest(0xA9, "D100", Nexus.DataConverter.GetBytes(0x1122334455667788UL));

            Assert.Equal(36, frame.Length);
            Assert.Equal(0xA9, frame[13]);
            Assert.Equal(0x00, frame[16]);
            Assert.Equal(0x10, frame[17]);
            Assert.Equal("D100    ", Encoding.ASCII.GetString(frame, 20, 8));
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }, frame[28..36]);
        }

        [Fact]
        public void BuildWriteRequest_WithDoublePayload_UsesEightDataBytes()
        {
            byte[] frame = LsXgtTcpClient.BuildWriteRequest(0xA9, "D100", Nexus.DataConverter.GetBytes(1.5d));

            Assert.Equal(new byte[] { 0x3F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, frame[28..36]);
        }

        [Fact]
        public void BuildWriteRequest_NullPayload_Throws()
        {
            Assert.Throws<NullReferenceException>(() => LsXgtTcpClient.BuildWriteRequest(0xA9, "D100", null!));
        }

        [Fact]
        public void BuildReadRequest_SingleWord_HasCorrectFrameStructure()
        {
            byte[] frame = LsXgtTcpClient.BuildReadRequest(0xA9, "D100", 1);

            // Frame: ENQ(1) + Company(2) + PLCInfo(2) + Filler(4) + CpuTo(1) + CpuFrom(1) + SFC(1) + MFC(1) + DataType(1) + Reserved(2) + ExtLen(2) + Data(10)
            Assert.Equal(0x05, frame[0]); // ENQ
            Assert.Equal((byte)'L', frame[1]);
            Assert.Equal((byte)'S', frame[2]);
            Assert.Equal(0x54, frame[12]); // MFC = Read
            Assert.Equal(0x01, frame[11]); // SFC = 01
            Assert.Equal(0xA9, frame[13]); // DataType = Word
            Assert.Equal("D100    ", Encoding.ASCII.GetString(frame, 20, 8));
        }

        [Fact]
        public void BuildReadRequest_MultipleWords_CountEncoded()
        {
            byte[] frame = LsXgtTcpClient.BuildReadRequest(0xA9, "D100", 10);
            // Count at data bytes 8-9 (big endian)
            int dataStart = 20;
            Assert.Equal(0, frame[dataStart + 8]);       // Hi byte of count
            Assert.Equal(10, frame[dataStart + 9]);      // Lo byte of count
        }

        [Fact]
        public void BuildWriteRequest_Int16_WritesTwoBytes()
        {
            byte[] frame = LsXgtTcpClient.BuildWriteRequest(0xA9, "D200", Nexus.DataConverter.GetBytes((short)1234));
            Assert.Equal(30, frame.Length); // 20 header + 8 addr + 2 data
        }

        [Fact]
        public void BuildWriteRequest_Float_WritesFourBytes()
        {
            byte[] frame = LsXgtTcpClient.BuildWriteRequest(0xA9, "D300", Nexus.DataConverter.GetBytes(3.14f));
            Assert.Equal(32, frame.Length); // 20 header + 8 addr + 4 data
        }

        [Fact]
        public void BuildWriteRequest_String_WritesAsciiBytes()
        {
            byte[] frame = LsXgtTcpClient.BuildWriteRequest(0xA6, "D100", Encoding.ASCII.GetBytes("Hello"));
            // Frame includes header(20) + addr(8) + data(5) = 33
            Assert.Equal(33, frame.Length);
            Assert.Equal("D100    ", Encoding.ASCII.GetString(frame, 20, 8));
        }

        [Fact]
        public void BuildXgtFrame_EmptyData_CreatesHeaderOnly()
        {
            byte[] frame = LsXgtTcpClient.BuildXgtFrame(0x54, 0x01, 0xA9, Array.Empty<byte>());
            Assert.Equal(20, frame.Length);
            Assert.Equal(0x05, frame[0]); // ENQ
        }

        [Fact]
        public void ReadOperations_WhenNotConnected_ReturnError()
        {
            var client = new LsXgtClient("192.168.1.1");
            Assert.False(client.ReadInt16("D100").IsSuccess);
            Assert.False(client.ReadUInt16("D100").IsSuccess);
            Assert.False(client.ReadInt32("D100").IsSuccess);
            Assert.False(client.ReadFloat("D100").IsSuccess);
            Assert.False(client.ReadBool("M100").IsSuccess);
            Assert.False(client.ReadString("D100", 10).IsSuccess);
        }

        [Fact]
        public void WriteOperations_WhenNotConnected_ReturnError()
        {
            var client = new LsXgtClient("192.168.1.1");
            Assert.False(client.Write("D100", (short)42).IsSuccess);
            Assert.False(client.Write("D100", 3.14f).IsSuccess);
            Assert.False(client.Write("M100", true).IsSuccess);
        }

        [Fact]
        public void BatchOperations_EmptyInput_ReturnsError()
        {
            var client = new LsXgtClient("192.168.1.1");
            Assert.False(client.BatchRead(new string[0]).IsSuccess);
        }

        [Fact]
        public void Subscribe_Unsubscribe_DoesNotThrow()
        {
            var client = new LsXgtClient("192.168.1.1");
            client.Subscribe("D100", 1000, "Int16");
            client.Unsubscribe("D100");
            client.StartSubscriptions();
            client.StopSubscriptions();
            client.Dispose();
        }
    }

    public class LsXgtAddressExtraTests
    {
        [Theory]
        [InlineData("D100", 0x07, 100, LsXgtArea.DataRegister, false)]
        [InlineData("D0", 0x07, 0, LsXgtArea.DataRegister, false)]
        [InlineData("M100", 0x01, 100, LsXgtArea.InternalRelay, true)]
        [InlineData("P0", 0x00, 0, LsXgtArea.IO, true)]
        [InlineData("L10", 0x02, 10, LsXgtArea.LinkRelay, true)]
        [InlineData("K50", 0x03, 50, LsXgtArea.KeepRelay, true)]
        [InlineData("F3", 0x04, 3, LsXgtArea.SpecialRelay, true)]
        [InlineData("T0", 0x05, 0, LsXgtArea.Timer, false)]
        [InlineData("C0", 0x06, 0, LsXgtArea.Counter, false)]
        [InlineData("N100", 0x08, 100, LsXgtArea.FileRegister, false)]
        public void Parse_ValidAddresses(string address, byte expectedAreaCode, int expectedOffset, LsXgtArea expectedArea, bool expectedIsBit)
        {
            var result = LsXgtAddress.Parse(address);
            Assert.Equal(expectedAreaCode, result.AreaCode);
            Assert.Equal(expectedOffset, result.Offset);
            Assert.Equal(expectedArea, result.Area);
            Assert.Equal(expectedIsBit, result.IsBitArea);
        }

        [Fact]
        public void Parse_EmptyAddress_Throws()
        {
            Assert.Throws<ArgumentException>(() => LsXgtAddress.Parse(""));
            Assert.Throws<ArgumentException>(() => LsXgtAddress.Parse("   "));
        }

        [Fact]
        public void Parse_SingleChar_Throws()
        {
            Assert.Throws<ArgumentException>(() => LsXgtAddress.Parse("D"));
        }

        [Fact]
        public void Parse_StartsWithDigit_Throws()
        {
            Assert.Throws<ArgumentException>(() => LsXgtAddress.Parse("100"));
        }

        [Fact]
        public void Parse_UnknownPrefix_DefaultsToDataRegister()
        {
            var result = LsXgtAddress.Parse("Z100");
            Assert.Equal(LsXgtArea.DataRegister, result.Area);
            Assert.Equal(0x07, result.AreaCode);
        }

        [Fact]
        public void TryParse_Valid_ReturnsAddress()
        {
            var result = LsXgtAddress.TryParse("D100");
            Assert.NotNull(result);
            Assert.Equal(100, result!.Offset);
        }

        [Fact]
        public void TryParse_Invalid_ReturnsNull()
        {
            Assert.Null(LsXgtAddress.TryParse(""));
            Assert.Null(LsXgtAddress.TryParse(null!));
        }

        [Fact]
        public void WithOffset_ReturnsNewAddress()
        {
            var addr = LsXgtAddress.Parse("D100");
            var offset = addr.WithOffset(10);
            Assert.Equal(110, offset.Offset);
            Assert.Equal(addr.AreaCode, offset.AreaCode);
        }

        [Fact]
        public void ToString_ContainsUsefulInfo()
        {
            var addr = LsXgtAddress.Parse("D100");
            string s = addr.ToString();
            Assert.Contains("DataRegister", s);
            Assert.Contains("0x07", s);
        }

        [Fact]
        public void Parse_CaseInsensitive()
        {
            var upper = LsXgtAddress.Parse("D100");
            var lower = LsXgtAddress.Parse("d100");
            Assert.Equal(upper.AreaCode, lower.AreaCode);
            Assert.Equal(upper.Offset, lower.Offset);
        }
    }
}
