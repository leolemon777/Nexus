using Nexus.Bacnet;
using Xunit;

namespace Nexus.Bacnet.Tests
{
    public class BacnetObjectIdTests
    {
        [Fact]
        public void ObjectId_RoundTrips()
        {
            var id = new BacnetObjectId(BacnetObjectType.AnalogInput, 42);
            uint packed = id.AsUint32;
            var unpacked = BacnetObjectId.FromUint32(packed);

            Assert.Equal(BacnetObjectType.AnalogInput, unpacked.Type);
            Assert.Equal(42u, unpacked.Instance);
        }

        [Fact]
        public void ObjectId_DeviceType()
        {
            var id = new BacnetObjectId(BacnetObjectType.Device, 1234);
            uint packed = id.AsUint32;

            Assert.Equal(BacnetObjectType.Device, BacnetObjectId.FromUint32(packed).Type);
            Assert.Equal(1234u, BacnetObjectId.FromUint32(packed).Instance);
        }

        [Fact]
        public void ObjectId_Equality()
        {
            var a = new BacnetObjectId(BacnetObjectType.AnalogValue, 1);
            var b = new BacnetObjectId(BacnetObjectType.AnalogValue, 1);
            var c = new BacnetObjectId(BacnetObjectType.AnalogValue, 2);

            Assert.Equal(a, b);
            Assert.True(a == b);
            Assert.True(a != c);
        }

        [Fact]
        public void ObjectId_ToString()
        {
            var id = new BacnetObjectId(BacnetObjectType.BinaryOutput, 7);
            Assert.Equal("BinaryOutput:7", id.ToString());
        }

        [Fact]
        public void ObjectId_MaxInstance()
        {
            var id = new BacnetObjectId(BacnetObjectType.Device, 0x3FFFFF);
            uint packed = id.AsUint32;
            var unpacked = BacnetObjectId.FromUint32(packed);

            Assert.Equal(0x3FFFFFu, unpacked.Instance);
        }
    }
}
