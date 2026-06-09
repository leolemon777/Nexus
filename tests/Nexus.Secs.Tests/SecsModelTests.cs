using Xunit;
using Nexus.Secs;

namespace Nexus.Secs.Tests
{
    public class SecsModelTests
    {
        [Fact]
        public void SecsFormatCode_Values()
        {
            Assert.Equal(0x00, (byte)SecsFormatCode.List);
            Assert.Equal(0x08, (byte)SecsFormatCode.Binary);
            Assert.Equal(0x10, (byte)SecsFormatCode.ASCII);
            Assert.Equal(0x19, (byte)SecsFormatCode.Int16);
            Assert.Equal(0x1A, (byte)SecsFormatCode.Int32);
            Assert.Equal(0x21, (byte)SecsFormatCode.UInt16);
            Assert.Equal(0x28, (byte)SecsFormatCode.Float32);
            Assert.Equal(0x29, (byte)SecsFormatCode.Float64);
        }

        [Fact]
        public void HsmsState_Values()
        {
            Assert.Equal(0, (int)HsmsState.NotConnected);
            Assert.Equal(1, (int)HsmsState.NotSelected);
            Assert.Equal(2, (int)HsmsState.Selected);
        }

        [Fact]
        public void SecsConstants_DefaultValues()
        {
            Assert.Equal(5000, SecsConstants.DefaultPort);
            Assert.Equal(14, SecsConstants.HsmsHeaderLength);
            Assert.Equal(45, SecsConstants.DefaultT3Timeout);
        }

        [Fact]
        public void SecsConstants_MessageIds()
        {
            Assert.Equal(0x0101, SecsConstants.S1F1);
            Assert.Equal(0x010E, SecsConstants.S1F14);
            Assert.Equal(0x060B, SecsConstants.S6F11);
        }
    }

    public class SecsDataItemTests
    {
        [Fact]
        public void CreateASCII_RoundTrip()
        {
            var item = SecsDataItem.CreateASCII("Hello SECS");
            Assert.Equal(SecsFormatCode.ASCII, item.Format);
            Assert.Equal("Hello SECS", item.GetASCII());
        }

        [Fact]
        public void CreateInt32_RoundTrip()
        {
            var item = SecsDataItem.CreateInt32(123456);
            Assert.Equal(SecsFormatCode.Int32, item.Format);
            Assert.Equal(123456, item.GetInt32());
        }

        [Fact]
        public void CreateUInt32_RoundTrip()
        {
            var item = SecsDataItem.CreateUInt32(0xFEDCBA98);
            Assert.Equal(0xFEDCBA98u, item.GetUInt32());
        }

        [Fact]
        public void CreateInt16_RoundTrip()
        {
            var item = SecsDataItem.CreateInt16(-1234);
            Assert.Equal(-1234, item.GetInt16());
        }

        [Fact]
        public void CreateUInt16_RoundTrip()
        {
            var item = SecsDataItem.CreateUInt16(50000);
            Assert.Equal(50000, item.GetUInt16());
        }

        [Fact]
        public void CreateFloat32_RoundTrip()
        {
            var item = SecsDataItem.CreateFloat32(3.14f);
            Assert.Equal(3.14f, item.GetFloat32());
        }

        [Fact]
        public void CreateFloat64_RoundTrip()
        {
            var item = SecsDataItem.CreateFloat64(2.718281828);
            Assert.Equal(2.718281828, item.GetFloat64());
        }

        [Fact]
        public void CreateBinary()
        {
            var item = SecsDataItem.CreateBinary(new byte[] { 0x01, 0x02, 0x03 });
            Assert.Equal(SecsFormatCode.Binary, item.Format);
            Assert.Equal(3, item.Count);
        }

        [Fact]
        public void CreateBoolean()
        {
            var item = SecsDataItem.CreateBoolean(new[] { true, false, true });
            Assert.Equal(SecsFormatCode.Boolean, item.Format);
            Assert.Equal(3, item.Count);
        }

        [Fact]
        public void CreateList()
        {
            var list = SecsDataItem.CreateList(
                SecsDataItem.CreateASCII("TEST"),
                SecsDataItem.CreateInt32(42));

            Assert.Equal(SecsFormatCode.List, list.Format);
            Assert.True(list.IsList);
            Assert.Equal(2, list.Count);
            Assert.NotNull(list.Items);
            Assert.Equal("TEST", list.Items[0].GetASCII());
            Assert.Equal(42, list.Items[1].GetInt32());
        }

        [Fact]
        public void CreateEmptyList()
        {
            var list = SecsDataItem.CreateList();
            Assert.True(list.IsList);
            Assert.Equal(0, list.Count);
        }

        [Fact]
        public void NestedList()
        {
            var nested = SecsDataItem.CreateList(
                SecsDataItem.CreateList(
                    SecsDataItem.CreateASCII("inner")));

            Assert.Equal(1, nested.Count);
            Assert.True(nested.Items![0].IsList);
            Assert.Equal("inner", nested.Items[0].Items![0].GetASCII());
        }
    }
}
