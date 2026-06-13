using Xunit;
using Nexus.Vigor;
using System;

namespace Nexus.Vigor.Tests
{
    public class VigorAddressTests
    {
        [Fact]
        public void Parse_X0_BitArea()
        {
            var addr = VigorAddress.Parse("X0");
            Assert.Equal("X", addr.Prefix);
            Assert.Equal(0, addr.Number);
            Assert.Equal(0x90, addr.DataCode);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void Parse_Y10_BitArea()
        {
            var addr = VigorAddress.Parse("Y10");
            Assert.Equal("Y", addr.Prefix);
            Assert.Equal(10, addr.Number);
            Assert.Equal(0x91, addr.DataCode);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void Parse_M100_BitArea()
        {
            var addr = VigorAddress.Parse("M100");
            Assert.Equal("M", addr.Prefix);
            Assert.Equal(100, addr.Number);
            Assert.Equal(0x92, addr.DataCode);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void Parse_M9000_HighRange()
        {
            var addr = VigorAddress.Parse("M9000");
            Assert.Equal(0x94, addr.DataCode);
        }

        [Fact]
        public void Parse_D0_WordArea()
        {
            var addr = VigorAddress.Parse("D0");
            Assert.Equal("D", addr.Prefix);
            Assert.Equal(0, addr.Number);
            Assert.Equal(0xA0, addr.DataCode);
            Assert.False(addr.IsBit);
        }

        [Fact]
        public void Parse_D9000_HighRange()
        {
            var addr = VigorAddress.Parse("D9000");
            Assert.Equal(0xA1, addr.DataCode);
        }

        [Fact]
        public void Parse_SD_WordArea()
        {
            var addr = VigorAddress.Parse("SD100");
            Assert.Equal("SD", addr.Prefix);
            Assert.Equal(0xA1, addr.DataCode);
            Assert.False(addr.IsBit);
        }

        [Fact]
        public void Parse_R_WordArea()
        {
            var addr = VigorAddress.Parse("R50");
            Assert.Equal("R", addr.Prefix);
            Assert.Equal(0xA2, addr.DataCode);
            Assert.False(addr.IsBit);
        }

        [Fact]
        public void Parse_S_BitArea()
        {
            var addr = VigorAddress.Parse("S5");
            Assert.Equal("S", addr.Prefix);
            Assert.Equal(0x93, addr.DataCode);
            Assert.True(addr.IsBit);
        }

        [Fact]
        public void Parse_LowercaseInput()
        {
            var addr = VigorAddress.Parse("x10");
            Assert.Equal("X", addr.Prefix);
            Assert.Equal(10, addr.Number);
        }

        [Fact]
        public void Parse_Empty_Throws()
        {
            Assert.Throws<ArgumentException>(() => VigorAddress.Parse(""));
        }

        [Fact]
        public void Parse_UnknownPrefix_Throws()
        {
            Assert.Throws<ArgumentException>(() => VigorAddress.Parse("Z100"));
        }

        [Fact]
        public void Parse_InvalidNumber_Throws()
        {
            Assert.Throws<ArgumentException>(() => VigorAddress.Parse("RABC"));
        }

        [Fact]
        public void EncodeBcdAddress_0()
        {
            var bcd = VigorAddress.EncodeBcdAddress(0);
            Assert.Equal(3, bcd.Length);
            Assert.Equal(0x00, bcd[0]);
            Assert.Equal(0x00, bcd[1]);
            Assert.Equal(0x00, bcd[2]);
        }

        [Fact]
        public void EncodeBcdAddress_123456()
        {
            var bcd = VigorAddress.EncodeBcdAddress(123456);
            Assert.Equal(3, bcd.Length);
            Assert.Equal(0x12, bcd[0]);
            Assert.Equal(0x34, bcd[1]);
            Assert.Equal(0x56, bcd[2]);
        }

        [Fact]
        public void IncrementAddress_Increments()
        {
            string result = VigorAddress.IncrementAddress("D100", 5);
            Assert.Equal("D105", result);
        }
    }

    public class VigorModelTests
    {
        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(0x10, VigorConstants.STX);
            Assert.Equal(0x02, VigorConstants.CODE);
            Assert.Equal(0x03, VigorConstants.ETX);
            Assert.Equal(32, VigorConstants.MaxWordReadCount);
            Assert.Equal(1024, VigorConstants.MaxBitReadCount);
            Assert.Equal(16, VigorConstants.MaxDWord32ReadCount);
            Assert.Equal(8, VigorConstants.FixedDataLen);
        }

        [Theory]
        [InlineData(VigorCommand.ReadWord, 0x20)]
        [InlineData(VigorCommand.ReadBit, 0x21)]
        [InlineData(VigorCommand.WriteWord, 0x28)]
        [InlineData(VigorCommand.WriteBit, 0x29)]
        public void Command_Values(VigorCommand cmd, byte expected)
        {
            Assert.Equal(expected, (byte)cmd);
        }
    }

    public class VigorProtocolTests
    {
        [Fact]
        public void BuildReadCommand_ProducesValidFrame()
        {
            byte[] cmd = VigorProtocol.BuildReadCommand(1, VigorCommand.ReadWord, 0xA0, 0, 1);
            Assert.NotNull(cmd);
            Assert.True(cmd.Length > 0);
            Assert.Equal(VigorConstants.STX, cmd[0]);
        }

        [Fact]
        public void BuildWriteCommand_ProducesValidFrame()
        {
            byte[] data = new byte[] { 0x01, 0x00 };
            byte[] cmd = VigorProtocol.BuildWriteCommand(1, VigorCommand.WriteWord, 0xA0, 0, 1, data);
            Assert.NotNull(cmd);
            Assert.True(cmd.Length > 0);
            Assert.Equal(VigorConstants.STX, cmd[0]);
        }

        [Fact]
        public void ParseResponse_TooShort_ReturnsError()
        {
            var result = VigorProtocol.ParseResponse(new byte[] { 0x10, 0x02 }, VigorCommand.ReadWord);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void ParseResponse_Null_ReturnsError()
        {
            var result = VigorProtocol.ParseResponse(null!, VigorCommand.ReadWord);
            Assert.False(result.IsSuccess);
        }
    }
}
