using System;
using Xunit;
using Nexus.Xinje;

namespace Nexus.Xinje.Tests
{
    public class XinjeAddressTests
    {
        [Theory]
        [InlineData("D100", 100, 0x03, 0x06)]
        [InlineData("D0", 0, 0x03, 0x06)]
        [InlineData("HD100", 0x8000 + 100, 0x03, 0x06)]
        [InlineData("HD0", 0x8000, 0x03, 0x06)]
        [InlineData("SD0", 0xC000, 0x03, 0x06)]
        [InlineData("SD100", 0xC000 + 100, 0x03, 0x06)]
        [InlineData("Y0", 0, 0x01, 0x05)]
        [InlineData("X10", 10, 0x02, 0x00)]
        [InlineData("M100", 0x0800 + 100, 0x01, 0x05)]
        [InlineData("C0", 0x1000, 0x03, 0x06)]
        [InlineData("T0", 0x0600, 0x03, 0x06)]
        [InlineData("S20", 20, 0x01, 0x05)]
        public void Parse_ValidAddresses(string addr, ushort expectedAddr, byte readFc, byte writeFc)
        {
            var parsed = XinjeAddress.Parse(addr);
            Assert.Equal(expectedAddr, parsed.Address);
            Assert.Equal(readFc, parsed.ReadFunctionCode);
            Assert.Equal(writeFc, parsed.WriteFunctionCode);
        }

        [Theory]
        [InlineData("d100", 100)]
        [InlineData("hd100", 0x8000 + 100)]
        [InlineData("sd0", 0xC000)]
        [InlineData("sm100", 0x1000 + 100)]
        public void Parse_CaseInsensitive(string addr, ushort expectedAddr)
        {
            var parsed = XinjeAddress.Parse(addr);
            Assert.Equal(expectedAddr, parsed.Address);
        }

        [Fact]
        public void IsReadOnly_X()
        {
            var x = XinjeAddress.Parse("X0");
            Assert.True(x.IsReadOnly);
            var d = XinjeAddress.Parse("D0");
            Assert.False(d.IsReadOnly);
        }

        [Fact]
        public void IsBitArea_Y()
        {
            var y = XinjeAddress.Parse("Y0");
            Assert.True(y.IsBitArea);
            var d = XinjeAddress.Parse("D0");
            Assert.False(d.IsBitArea);
        }

        [Fact]
        public void WithOffset()
        {
            var base_ = XinjeAddress.Parse("D100");
            var offset = base_.WithOffset(10);
            Assert.Equal(110, offset.Address);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Parse_EmptyThrows(string addr)
        {
            Assert.Throws<ArgumentException>(() => XinjeAddress.Parse(addr));
        }

        [Fact]
        public void TryParse_EmptyReturnsNull()
        {
            Assert.Null(XinjeAddress.TryParse(""));
        }

        [Fact]
        public void ToString_ContainsInfo()
        {
            var parsed = XinjeAddress.Parse("HD100");
            string s = parsed.ToString();
            Assert.Contains("HoldingRegister", s);
        }

        [Fact]
        public void SM_AreaParses()
        {
            var sm = XinjeAddress.Parse("SM50");
            Assert.Equal(0x1000 + 50, sm.Address);
            Assert.Equal(XinjeArea.SpecialCoil, sm.Area);
        }
    }

    public class XinjeVirtualServerTests : IDisposable
    {
        private readonly XinjeVirtualServer _server;

        public XinjeVirtualServerTests()
        {
            _server = new XinjeVirtualServer(0);
            _server.Start();
        }

        [Fact]
        public void Server_StartsAndStops()
        {
            Assert.True(_server.IsRunning);
        }

        [Fact]
        public void SetGetHoldingRegister()
        {
            _server.SetHoldingRegister(100, 0x1234);
            Assert.Equal(0x1234, _server.GetHoldingRegister(100));
        }

        [Fact]
        public void SetGetCoil()
        {
            _server.SetCoil(10, true);
            Assert.True(_server.GetCoil(10));
        }

        public void Dispose() { _server?.Stop(); _server?.Dispose(); }
    }
}
