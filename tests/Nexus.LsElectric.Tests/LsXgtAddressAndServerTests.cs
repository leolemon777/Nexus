using System;
using Xunit;
using Nexus.LsElectric;

namespace Nexus.LsElectric.Tests
{
    public class LsXgtAddressTests
    {
        [Theory]
        [InlineData("P0", 0x00, true)]
        [InlineData("P100", 0x00, true)]
        [InlineData("M0", 0x01, true)]
        [InlineData("M500", 0x01, true)]
        [InlineData("L10", 0x02, true)]
        [InlineData("K50", 0x03, true)]
        [InlineData("F3", 0x04, true)]
        [InlineData("T0", 0x05, false)]
        [InlineData("T100", 0x05, false)]
        [InlineData("C0", 0x06, false)]
        [InlineData("C50", 0x06, false)]
        [InlineData("D0", 0x07, false)]
        [InlineData("D1000", 0x07, false)]
        [InlineData("N100", 0x08, false)]
        public void Parse_ValidAddresses(string addr, byte expectedArea, bool expectedIsBit)
        {
            var parsed = LsXgtAddress.Parse(addr);
            Assert.Equal(expectedArea, parsed.AreaCode);
            Assert.Equal(expectedIsBit, parsed.IsBitArea);
        }

        [Theory]
        [InlineData("d100", 0x07)]
        [InlineData("m50", 0x01)]
        [InlineData("p0", 0x00)]
        public void Parse_CaseInsensitive(string addr, byte expectedArea)
        {
            var parsed = LsXgtAddress.Parse(addr);
            Assert.Equal(expectedArea, parsed.AreaCode);
        }

        [Fact]
        public void WithOffset()
        {
            var base_ = LsXgtAddress.Parse("D100");
            var offset = base_.WithOffset(10);
            Assert.Equal(110, offset.Offset);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Parse_EmptyThrows(string addr)
        {
            Assert.Throws<ArgumentException>(() => LsXgtAddress.Parse(addr));
        }

        [Fact]
        public void TryParse_EmptyReturnsNull()
        {
            Assert.Null(LsXgtAddress.TryParse(""));
        }

        [Fact]
        public void ToString_ContainsInfo()
        {
            var parsed = LsXgtAddress.Parse("D100");
            Assert.Contains("DataRegister", parsed.ToString());
        }

        [Fact]
        public void Area_MatchesEnum()
        {
            Assert.Equal(LsXgtArea.IO, LsXgtAddress.Parse("P0").Area);
            Assert.Equal(LsXgtArea.InternalRelay, LsXgtAddress.Parse("M0").Area);
            Assert.Equal(LsXgtArea.LinkRelay, LsXgtAddress.Parse("L0").Area);
            Assert.Equal(LsXgtArea.KeepRelay, LsXgtAddress.Parse("K0").Area);
            Assert.Equal(LsXgtArea.SpecialRelay, LsXgtAddress.Parse("F0").Area);
            Assert.Equal(LsXgtArea.Timer, LsXgtAddress.Parse("T0").Area);
            Assert.Equal(LsXgtArea.Counter, LsXgtAddress.Parse("C0").Area);
            Assert.Equal(LsXgtArea.DataRegister, LsXgtAddress.Parse("D0").Area);
            Assert.Equal(LsXgtArea.FileRegister, LsXgtAddress.Parse("N0").Area);
        }
    }

    public class LsXgtVirtualServerTests : IDisposable
    {
        private readonly LsXgtVirtualServer _server;
        public LsXgtVirtualServerTests() { _server = new LsXgtVirtualServer(0); _server.Start(); }

        [Fact]
        public void Server_Starts() { Assert.True(_server.IsRunning); }

        [Fact]
        public void SetGetRegister()
        {
            _server.SetRegister(100, 0xABCD);
            Assert.Equal(0xABCD, _server.GetRegister(100));
        }

        [Fact]
        public void SetGetBit()
        {
            _server.SetBit(50, true);
            Assert.True(_server.GetBit(50));
        }

        public void Dispose() { _server?.Stop(); _server?.Dispose(); }
    }
}
