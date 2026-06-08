using Nexus.Bacnet;
using Xunit;

namespace Nexus.Bacnet.Tests
{
    public class BacnetApduTests
    {
        [Fact]
        public void EncodeWhoIs_ProducesValidApdu()
        {
            byte[] apdu = BacnetApdu.EncodeWhoIs();

            Assert.NotEmpty(apdu);
            Assert.Equal(0x10, apdu[0]);
            Assert.Equal((byte)BacnetUnconfirmedService.WhoIs, apdu[1]);
        }

        [Fact]
        public void EncodeWhoIs_WithLimits()
        {
            byte[] apdu = BacnetApdu.EncodeWhoIs(100, 200);

            Assert.True(apdu.Length > 2);
            Assert.Equal(0x10, apdu[0]);
            Assert.Equal((byte)BacnetUnconfirmedService.WhoIs, apdu[1]);
        }

        [Fact]
        public void EncodeIAm_ProducesValidApdu()
        {
            var deviceId = new BacnetObjectId(BacnetObjectType.Device, 1234);
            byte[] apdu = BacnetApdu.EncodeIAm(deviceId, 1476, BacnetSegmentation.Both, 260);

            Assert.NotEmpty(apdu);
            Assert.Equal(0x10, apdu[0]);
            Assert.Equal((byte)BacnetUnconfirmedService.IAm, apdu[1]);
        }

        [Fact]
        public void EncodeReadProperty_ProducesValidApdu()
        {
            var objectId = new BacnetObjectId(BacnetObjectType.AnalogInput, 1);
            byte[] apdu = BacnetApdu.EncodeReadProperty(1, objectId, BacnetPropertyId.PresentValue);

            Assert.NotEmpty(apdu);
            Assert.Equal(0x00, apdu[0]);
            Assert.Equal(0x05, apdu[1]);
            Assert.Equal(1, apdu[2]);
            Assert.Equal((byte)BacnetConfirmedService.ReadProperty, apdu[3]);
        }

        [Fact]
        public void EncodeReadPropertyMultiple_ProducesValidApdu()
        {
            var refs = new[]
            {
                new BacnetPropertyReference(
                    new BacnetObjectId(BacnetObjectType.AnalogInput, 1),
                    BacnetPropertyId.PresentValue),
                new BacnetPropertyReference(
                    new BacnetObjectId(BacnetObjectType.AnalogInput, 2),
                    BacnetPropertyId.PresentValue)
            };

            byte[] apdu = BacnetApdu.EncodeReadPropertyMultiple(2, refs);

            Assert.NotEmpty(apdu);
            Assert.Equal(0x00, apdu[0]);
            Assert.Equal((byte)BacnetConfirmedService.ReadPropertyMultiple, apdu[3]);
        }

        [Fact]
        public void EncodeWriteProperty_ProducesValidApdu()
        {
            var objectId = new BacnetObjectId(BacnetObjectType.AnalogOutput, 1);
            var value = new BacnetValue(BacnetApplicationTag.Real, 72.5f);

            byte[] apdu = BacnetApdu.EncodeWriteProperty(3, objectId, BacnetPropertyId.PresentValue, value);

            Assert.NotEmpty(apdu);
            Assert.Equal(0x00, apdu[0]);
            Assert.Equal(3, apdu[2]);
            Assert.Equal((byte)BacnetConfirmedService.WriteProperty, apdu[3]);
        }

        [Fact]
        public void EncodeSubscribeCov_ProducesValidApdu()
        {
            var monitoredObject = new BacnetObjectId(BacnetObjectType.AnalogInput, 10);
            byte[] apdu = BacnetApdu.EncodeSubscribeCov(4, 100, monitoredObject, true, 300);

            Assert.NotEmpty(apdu);
            Assert.Equal(0x00, apdu[0]);
            Assert.Equal(4, apdu[2]);
            Assert.Equal((byte)BacnetConfirmedService.SubscribeCOV, apdu[3]);
        }

        [Fact]
        public void EncodeAtomicReadFile_ProducesValidApdu()
        {
            var fileId = new BacnetObjectId(BacnetObjectType.File, 0);
            byte[] apdu = BacnetApdu.EncodeAtomicReadFile(5, fileId, false, 0, 1024);

            Assert.NotEmpty(apdu);
            Assert.Equal((byte)BacnetConfirmedService.AtomicReadFile, apdu[3]);
        }

        [Fact]
        public void EncodeAtomicWriteFile_ProducesValidApdu()
        {
            var fileId = new BacnetObjectId(BacnetObjectType.File, 0);
            byte[] data = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
            byte[] apdu = BacnetApdu.EncodeAtomicWriteFile(6, fileId, false, 0, data);

            Assert.NotEmpty(apdu);
            Assert.Equal((byte)BacnetConfirmedService.AtomicWriteFile, apdu[3]);
        }

        [Fact]
        public void EncodeSimpleAck_ProducesValidApdu()
        {
            byte[] apdu = BacnetApdu.EncodeSimpleAck(7, BacnetConfirmedService.WriteProperty);

            Assert.Equal(3, apdu.Length);
            Assert.Equal(0x20, apdu[0]);
            Assert.Equal(7, apdu[1]);
            Assert.Equal((byte)BacnetConfirmedService.WriteProperty, apdu[2]);
        }

        [Fact]
        public void DecodeApdu_SimpleAck()
        {
            byte[] data = new byte[] { 0x20, 0x07, (byte)BacnetConfirmedService.WriteProperty };
            var response = BacnetApdu.DecodeApdu(data, 0, data.Length);

            Assert.True(response.IsValid);
            Assert.Equal(BacnetPduType.SimpleAck, response.PduType);
            Assert.Equal(7, response.InvokeId);
            Assert.Equal(BacnetConfirmedService.WriteProperty, response.ConfirmedService);
        }

        [Fact]
        public void DecodeApdu_UnconfirmedRequest()
        {
            var deviceId = new BacnetObjectId(BacnetObjectType.Device, 1234);
            byte[] encoded = BacnetApdu.EncodeIAm(deviceId, 1476, BacnetSegmentation.Both, 260);

            var response = BacnetApdu.DecodeApdu(encoded, 0, encoded.Length);

            Assert.True(response.IsValid);
            Assert.Equal(BacnetPduType.UnconfirmedRequest, response.PduType);
            Assert.Equal((int)BacnetUnconfirmedService.IAm, response.ServiceChoice);
            Assert.True(response.Values.Length >= 4);
        }

        [Fact]
        public void DecodeApdu_Error()
        {
            byte[] data = new byte[]
            {
                0x50,
                0x01,
                (byte)BacnetConfirmedService.ReadProperty,
                0x00, 0x01,
                0x00, 0x33
            };

            var response = BacnetApdu.DecodeApdu(data, 0, data.Length);

            Assert.True(response.IsValid);
            Assert.Equal(BacnetPduType.Error, response.PduType);
        }

        [Fact]
        public void DecodeApdu_Reject()
        {
            byte[] data = new byte[]
            {
                0x60,
                0x01,
                (byte)BacnetRejectReason.MissingRequiredParameter
            };

            var response = BacnetApdu.DecodeApdu(data, 0, data.Length);

            Assert.True(response.IsValid);
            Assert.Equal(BacnetPduType.Reject, response.PduType);
            Assert.Equal(BacnetRejectReason.MissingRequiredParameter, response.RejectReason);
        }

        [Fact]
        public void DecodeApdu_Abort()
        {
            byte[] data = new byte[]
            {
                0x70,
                0x01,
                (byte)BacnetAbortReason.BufferOverflow
            };

            var response = BacnetApdu.DecodeApdu(data, 0, data.Length);

            Assert.True(response.IsValid);
            Assert.Equal(BacnetPduType.Abort, response.PduType);
            Assert.Equal(BacnetAbortReason.BufferOverflow, response.AbortReason);
        }

        [Fact]
        public void BacnetPropertyReference_StoresValues()
        {
            var objId = new BacnetObjectId(BacnetObjectType.AnalogInput, 100);
            var pref = new BacnetPropertyReference(objId, BacnetPropertyId.PresentValue, 5);

            Assert.Equal(objId, pref.ObjectIdentifier);
            Assert.Equal(BacnetPropertyId.PresentValue, pref.PropertyId);
            Assert.Equal(5u, pref.ArrayIndex);
        }

        [Fact]
        public void BacnetValue_ToString()
        {
            var val = new BacnetValue(BacnetApplicationTag.Real, 3.14f);
            string str = val.ToString();

            Assert.Contains("Real", str);
        }
    }
}
