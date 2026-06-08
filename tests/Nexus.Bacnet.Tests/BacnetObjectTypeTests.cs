using Nexus.Bacnet;
using Xunit;

namespace Nexus.Bacnet.Tests
{
    public class BacnetObjectTypeTests
    {
        [Fact]
        public void AnalogInput_HasCorrectValue()
        {
            Assert.Equal(0, (int)BacnetObjectType.AnalogInput);
        }

        [Fact]
        public void Device_HasCorrectValue()
        {
            Assert.Equal(8, (int)BacnetObjectType.Device);
        }

        [Fact]
        public void TrendLog_HasCorrectValue()
        {
            Assert.Equal(20, (int)BacnetObjectType.TrendLog);
        }

        [Fact]
        public void Schedule_HasCorrectValue()
        {
            Assert.Equal(17, (int)BacnetObjectType.Schedule);
        }

        [Fact]
        public void MultiStateInput_HasCorrectValue()
        {
            Assert.Equal(13, (int)BacnetObjectType.MultiStateInput);
        }

        [Fact]
        public void MultiStateOutput_HasCorrectValue()
        {
            Assert.Equal(14, (int)BacnetObjectType.MultiStateOutput);
        }

        [Fact]
        public void MultiStateValue_HasCorrectValue()
        {
            Assert.Equal(19, (int)BacnetObjectType.MultiStateValue);
        }

        [Fact]
        public void BacnetPropertyId_PresentValue()
        {
            Assert.Equal(85u, (uint)BacnetPropertyId.PresentValue);
        }

        [Fact]
        public void BacnetPropertyId_ObjectName()
        {
            Assert.Equal(77u, (uint)BacnetPropertyId.ObjectName);
        }

        [Fact]
        public void BacnetPropertyId_StatusFlags()
        {
            Assert.Equal(111u, (uint)BacnetPropertyId.StatusFlags);
        }

        [Fact]
        public void BacnetPropertyId_ObjectList()
        {
            Assert.Equal(76u, (uint)BacnetPropertyId.ObjectList);
        }

        [Fact]
        public void BacnetApplicationTag_AllTypes()
        {
            Assert.Equal(0, (byte)BacnetApplicationTag.Null);
            Assert.Equal(1, (byte)BacnetApplicationTag.Boolean);
            Assert.Equal(2, (byte)BacnetApplicationTag.Unsigned);
            Assert.Equal(3, (byte)BacnetApplicationTag.Signed);
            Assert.Equal(4, (byte)BacnetApplicationTag.Real);
            Assert.Equal(5, (byte)BacnetApplicationTag.Double);
            Assert.Equal(6, (byte)BacnetApplicationTag.OctetString);
            Assert.Equal(7, (byte)BacnetApplicationTag.CharacterString);
            Assert.Equal(9, (byte)BacnetApplicationTag.Enumerated);
            Assert.Equal(12, (byte)BacnetApplicationTag.ObjectId);
        }
    }
}
