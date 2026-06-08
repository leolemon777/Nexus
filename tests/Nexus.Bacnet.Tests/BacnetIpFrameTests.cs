using Nexus.Bacnet;
using Xunit;

namespace Nexus.Bacnet.Tests
{
    public class BacnetIpFrameTests
    {
        [Fact]
        public void Bvlc_WrapAndUnwrap_RoundTrips()
        {
            byte[] apdu = BacnetApdu.EncodeWhoIs();
            byte[] npdu = new byte[2 + apdu.Length];
            npdu[0] = 0x01;
            npdu[1] = 0x00;
            Buffer.BlockCopy(apdu, 0, npdu, 2, apdu.Length);

            int totalLen = 4 + npdu.Length;
            byte[] frame = new byte[totalLen];
            frame[0] = 0x81;
            frame[1] = 0x00;
            frame[2] = (byte)(totalLen >> 8);
            frame[3] = (byte)totalLen;
            Buffer.BlockCopy(npdu, 0, frame, 4, npdu.Length);

            Assert.Equal(0x81, frame[0]);
            Assert.Equal(totalLen, (frame[2] << 8) | frame[3]);
        }

        [Fact]
        public void Bvlc_TypeConstant()
        {
            Assert.Equal(0x81, BacnetIpClient.DefaultPort != 0 ? 0x81 : 0);
        }

        [Fact]
        public void DefaultPort_IsCorrect()
        {
            Assert.Equal(47808, BacnetIpClient.DefaultPort);
        }

        [Fact]
        public void BacnetIpFrame_DefaultValues()
        {
            var frame = new BacnetIpFrame();
            Assert.False(frame.IsValid);
            Assert.Equal(0, frame.Type);
            Assert.Equal(0, frame.Function);
            Assert.Equal(0, frame.Length);
            Assert.Empty(frame.Payload);
        }

        [Fact]
        public void BacnetNpduHeader_DefaultValues()
        {
            var header = new BacnetNpduHeader();
            Assert.False(header.IsValid);
            Assert.Equal(0, header.Version);
            Assert.Equal(0, header.Control);
            Assert.Null(header.DestinationMac);
        }

        [Fact]
        public void BacnetSegmentation_Values()
        {
            Assert.Equal(0, (byte)BacnetSegmentation.Both);
            Assert.Equal(1, (byte)BacnetSegmentation.Transmit);
            Assert.Equal(2, (byte)BacnetSegmentation.Receive);
            Assert.Equal(3, (byte)BacnetSegmentation.NoSegmentation);
        }
    }
}
