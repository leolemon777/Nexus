using Xunit;
using Nexus.Keyence;

namespace Nexus.Keyence.Tests
{
    public class KeyenceKvAddressTests
    {
        [Theory]
        [InlineData("DM100", 100, 0x03, 0x06)]
        [InlineData("DM0", 0, 0x03, 0x06)]
        [InlineData("D100", 100, 0x03, 0x06)]
        [InlineData("WR0", 0, 0x01, 0x05)]
        [InlineData("WR100", 100, 0x01, 0x05)]
        [InlineData("HR10", 0x0800 + 10, 0x01, 0x05)]
        [InlineData("AR3", 0x1000 + 3, 0x01, 0x05)]
        [InlineData("TC0", 0x1800, 0x01, 0x05)]
        [InlineData("CC0", 0x1C00, 0x01, 0x05)]
        [InlineData("CM100", 0x2000 + 100, 0x03, 0x06)]
        [InlineData("TM50", 0x2400 + 50, 0x03, 0x06)]
        public void Parse_ValidAddresses(string addr, ushort expectedAddr, byte readFc, byte writeFc)
        {
            var parsed = KeyenceKvAddress.Parse(addr);
            Assert.Equal(expectedAddr, parsed.Address);
            Assert.Equal(readFc, parsed.ReadFunctionCode);
            Assert.Equal(writeFc, parsed.WriteFunctionCode);
        }

        [Fact]
        public void AreaType_Correct()
        {
            Assert.Equal(KeyenceArea.DataMemory, KeyenceKvAddress.Parse("DM0").Area);
            Assert.Equal(KeyenceArea.WordRelay, KeyenceKvAddress.Parse("WR0").Area);
            Assert.Equal(KeyenceArea.KeepRelay, KeyenceKvAddress.Parse("HR0").Area);
            Assert.Equal(KeyenceArea.AuxRelay, KeyenceKvAddress.Parse("AR0").Area);
            Assert.Equal(KeyenceArea.TimerCoil, KeyenceKvAddress.Parse("TC0").Area);
            Assert.Equal(KeyenceArea.CounterCoil, KeyenceKvAddress.Parse("CC0").Area);
            Assert.Equal(KeyenceArea.TimerValue, KeyenceKvAddress.Parse("CM0").Area);
            Assert.Equal(KeyenceArea.CounterValue, KeyenceKvAddress.Parse("TM0").Area);
        }

        [Fact]
        public void IsBitArea_Correct()
        {
            Assert.True(KeyenceKvAddress.Parse("WR0").IsBitArea);
            Assert.True(KeyenceKvAddress.Parse("HR0").IsBitArea);
            Assert.False(KeyenceKvAddress.Parse("DM0").IsBitArea);
            Assert.False(KeyenceKvAddress.Parse("CM0").IsBitArea);
        }

        [Fact]
        public void TryParse_InvalidReturnsNull()
        {
            Assert.Null(KeyenceKvAddress.TryParse(""));
            Assert.Null(KeyenceKvAddress.TryParse("ZZ99"));
        }

        [Fact]
        public void WithOffset()
        {
            var base_ = KeyenceKvAddress.Parse("DM100");
            var offset = base_.WithOffset(10);
            Assert.Equal(110, offset.Address);
        }
    }

    public class KeyenceKvVirtualServerTests
    {
        [Fact]
        public void Server_StartsAndStops()
        {
            using var server = new KeyenceKvVirtualServer(0);
            server.Start();
            Assert.True(server.IsRunning);
            server.Stop();
            Assert.False(server.IsRunning);
        }

        [Fact]
        public void SetGetRegister()
        {
            using var server = new KeyenceKvVirtualServer(0);
            server.SetRegister(100, 0x5678);
            Assert.Equal(0x5678, server.GetRegister(100));
        }
    }
}
