using System;
using System.Text;
using Xunit;
using Nexus.Iec61850;

namespace Nexus.Iec61850.Tests
{
    public class Asn1BerTests
    {
        // ── EncodeInteger ──────────────────────────

        [Fact]
        public void EncodeInteger_Zero()
        {
            byte[] result = Asn1BerCodec.EncodeInteger(0);
            Assert.Equal(0x02, result[0]); // TagInteger
            Assert.Equal(0x01, result[1]); // length = 1
            Assert.Equal(0x00, result[2]); // value = 0
        }

        [Fact]
        public void EncodeInteger_Positive()
        {
            byte[] result = Asn1BerCodec.EncodeInteger(1);
            Assert.Equal(0x02, result[0]);
            Assert.Equal(0x01, result[1]);
            Assert.Equal(0x01, result[2]);
        }

        [Fact]
        public void EncodeInteger_Negative()
        {
            byte[] result = Asn1BerCodec.EncodeInteger(-1);
            Assert.Equal(0x02, result[0]);
            Assert.Equal(0x01, result[1]);
            Assert.Equal(0xFF, result[2]); // -1 in two's complement
        }

        [Fact]
        public void EncodeInteger_LargeValue()
        {
            byte[] result = Asn1BerCodec.EncodeInteger(256);
            Assert.Equal(0x02, result[0]);
            Assert.Equal(0x02, result[1]); // length = 2
            Assert.Equal(0x01, result[2]); // high byte
            Assert.Equal(0x00, result[3]); // low byte
        }

        [Fact]
        public void EncodeInteger_127()
        {
            byte[] result = Asn1BerCodec.EncodeInteger(127);
            Assert.Equal(0x02, result[0]);
            Assert.Equal(0x01, result[1]);
            Assert.Equal(0x7F, result[2]);
        }

        [Fact]
        public void EncodeInteger_128_RequiresTwoBytes()
        {
            byte[] result = Asn1BerCodec.EncodeInteger(128);
            Assert.Equal(0x02, result[0]);
            Assert.Equal(0x02, result[1]); // needs 2 bytes (0x80 has high bit set)
            Assert.Equal(0x00, result[2]);
            Assert.Equal(0x80, result[3]);
        }

        [Fact]
        public void EncodeInteger_Negative128()
        {
            byte[] result = Asn1BerCodec.EncodeInteger(-128);
            Assert.Equal(0x02, result[0]);
            Assert.Equal(0x01, result[1]);
            Assert.Equal(0x80, result[2]);
        }

        // ── EncodeLength ───────────────────────────

        [Fact]
        public void EncodeLength_ShortForm()
        {
            byte[] result = Asn1BerCodec.EncodeLength(0);
            Assert.Single(result);
            Assert.Equal(0x00, result[0]);

            result = Asn1BerCodec.EncodeLength(127);
            Assert.Single(result);
            Assert.Equal(0x7F, result[0]);
        }

        [Fact]
        public void EncodeLength_LongForm_OneByte()
        {
            byte[] result = Asn1BerCodec.EncodeLength(128);
            Assert.Equal(2, result.Length);
            Assert.Equal(0x81, result[0]);
            Assert.Equal(128, result[1]);

            result = Asn1BerCodec.EncodeLength(255);
            Assert.Equal(2, result.Length);
            Assert.Equal(0x81, result[0]);
            Assert.Equal(255, result[1]);
        }

        [Fact]
        public void EncodeLength_LongForm_TwoBytes()
        {
            byte[] result = Asn1BerCodec.EncodeLength(256);
            Assert.Equal(3, result.Length);
            Assert.Equal(0x82, result[0]);
            Assert.Equal(0x01, result[1]);
            Assert.Equal(0x00, result[2]);
        }

        [Fact]
        public void EncodeLength_RoundTrip()
        {
            int[] testValues = { 0, 1, 127, 128, 255, 256, 1000, 65535 };
            foreach (int val in testValues)
            {
                byte[] encoded = Asn1BerCodec.EncodeLength(val);
                int decoded = Asn1BerCodec.DecodeLength(encoded, 0);
                Assert.Equal(val, decoded);
            }
        }

        // ── DecodeLength ───────────────────────────

        [Fact]
        public void DecodeLength_ShortForm()
        {
            Assert.Equal(0, Asn1BerCodec.DecodeLength(new byte[] { 0x00 }, 0));
            Assert.Equal(42, Asn1BerCodec.DecodeLength(new byte[] { 0x2A }, 0));
            Assert.Equal(127, Asn1BerCodec.DecodeLength(new byte[] { 0x7F }, 0));
        }

        [Fact]
        public void DecodeLength_LongForm()
        {
            Assert.Equal(128, Asn1BerCodec.DecodeLength(new byte[] { 0x81, 0x80 }, 0));
            Assert.Equal(300, Asn1BerCodec.DecodeLength(new byte[] { 0x82, 0x01, 0x2C }, 0));
        }

        [Fact]
        public void GetLengthBytes_ShortForm()
        {
            Assert.Equal(1, Asn1BerCodec.GetLengthBytes(new byte[] { 0x00 }, 0));
            Assert.Equal(1, Asn1BerCodec.GetLengthBytes(new byte[] { 0x7F }, 0));
        }

        [Fact]
        public void GetLengthBytes_LongForm()
        {
            Assert.Equal(2, Asn1BerCodec.GetLengthBytes(new byte[] { 0x81, 0x80 }, 0));
            Assert.Equal(3, Asn1BerCodec.GetLengthBytes(new byte[] { 0x82, 0x01, 0x00 }, 0));
        }

        // ── EncodeSequence ─────────────────────────

        [Fact]
        public void EncodeSequence_Empty()
        {
            byte[] result = Asn1BerCodec.EncodeSequence(new byte[0]);
            Assert.Equal(0x30, result[0]); // TagSequence
            Assert.Equal(0x00, result[1]); // length = 0
        }

        [Fact]
        public void EncodeSequence_Nested()
        {
            byte[] inner = Asn1BerCodec.EncodeInteger(42);
            byte[] outer = Asn1BerCodec.EncodeSequence(inner);
            Assert.Equal(0x30, outer[0]); // TagSequence
            Assert.Equal(inner.Length, outer[1]); // length = inner length
            // Inner data starts at offset 2
            Assert.Equal(0x02, outer[2]); // TagInteger
        }

        // ── EncodeVisibleString ────────────────────

        [Fact]
        public void EncodeVisibleString_Empty()
        {
            byte[] result = Asn1BerCodec.EncodeVisibleString("");
            Assert.Equal(0x1A, result[0]); // VisibleString tag
            Assert.Equal(0x00, result[1]); // length = 0
        }

        [Fact]
        public void EncodeVisibleString_NonEmpty()
        {
            byte[] result = Asn1BerCodec.EncodeVisibleString("test");
            Assert.Equal(0x1A, result[0]);
            Assert.Equal(0x04, result[1]); // length = 4
            Assert.Equal((byte)'t', result[2]);
            Assert.Equal((byte)'e', result[3]);
            Assert.Equal((byte)'s', result[4]);
            Assert.Equal((byte)'t', result[5]);
        }

        // ── EncodeOctetString ──────────────────────

        [Fact]
        public void EncodeOctetString_Empty()
        {
            byte[] result = Asn1BerCodec.EncodeOctetString(new byte[0]);
            Assert.Equal(0x04, result[0]); // TagOctetString
            Assert.Equal(0x00, result[1]); // length = 0
        }

        [Fact]
        public void EncodeOctetString_WithData()
        {
            byte[] data = new byte[] { 0xAA, 0xBB, 0xCC };
            byte[] result = Asn1BerCodec.EncodeOctetString(data);
            Assert.Equal(0x04, result[0]);
            Assert.Equal(0x03, result[1]); // length = 3
            Assert.Equal(0xAA, result[2]);
            Assert.Equal(0xBB, result[3]);
            Assert.Equal(0xCC, result[4]);
        }

        // ── EncodeBoolean ──────────────────────────

        [Fact]
        public void EncodeBoolean_True()
        {
            byte[] result = Asn1BerCodec.EncodeBoolean(true);
            Assert.Equal(0x01, result[0]);
            Assert.Equal(0x01, result[1]);
            Assert.Equal(0xFF, result[2]);
        }

        [Fact]
        public void EncodeBoolean_False()
        {
            byte[] result = Asn1BerCodec.EncodeBoolean(false);
            Assert.Equal(0x01, result[0]);
            Assert.Equal(0x01, result[1]);
            Assert.Equal(0x00, result[2]);
        }

        // ── EncodeTagged ───────────────────────────

        [Fact]
        public void EncodeTagged_ContextSpecific()
        {
            byte[] content = new byte[] { 0x01, 0x02 };
            byte[] result = Asn1BerCodec.EncodeTagged(0xA0, content);
            Assert.Equal(0xA0, result[0]);
            Assert.Equal(0x02, result[1]); // length = 2
            Assert.Equal(0x01, result[2]);
            Assert.Equal(0x02, result[3]);
        }

        // ── DecodeTag ──────────────────────────────

        [Fact]
        public void DecodeTag_Primitive()
        {
            byte[] data = new byte[] { 0x02, 0x01, 0x05 }; // INTEGER, length=1, value=5
            var tag = Asn1BerCodec.DecodeTag(data, 0);
            Assert.Equal(0x02, tag.Tag);
            Assert.False(tag.IsConstructed);
            Assert.Equal(1, tag.Length);
            Assert.Equal(2, tag.ContentOffset);
        }

        [Fact]
        public void DecodeTag_Constructed()
        {
            byte[] data = new byte[] { 0x30, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05 }; // SEQUENCE
            var tag = Asn1BerCodec.DecodeTag(data, 0);
            Assert.Equal(0x30, tag.Tag);
            Assert.True(tag.IsConstructed);
            Assert.Equal(5, tag.Length);
            Assert.Equal(2, tag.ContentOffset);
        }

        [Fact]
        public void DecodeTag_LongLength()
        {
            byte[] data = new byte[] { 0x30, 0x82, 0x01, 0x00 }; // SEQUENCE, length=256
            var tag = Asn1BerCodec.DecodeTag(data, 0);
            Assert.Equal(0x30, tag.Tag);
            Assert.True(tag.IsConstructed);
            Assert.Equal(256, tag.Length);
            Assert.Equal(4, tag.ContentOffset);
        }

        // ── DecodeInteger ──────────────────────────

        [Fact]
        public void DecodeInteger_Positive()
        {
            long val = Asn1BerCodec.DecodeInteger(new byte[] { 0x05 }, 0, 1);
            Assert.Equal(5, val);
        }

        [Fact]
        public void DecodeInteger_Negative()
        {
            long val = Asn1BerCodec.DecodeInteger(new byte[] { 0xFF }, 0, 1);
            Assert.Equal(-1, val);
        }

        [Fact]
        public void DecodeInteger_Zero()
        {
            long val = Asn1BerCodec.DecodeInteger(new byte[] { 0x00 }, 0, 1);
            Assert.Equal(0, val);
        }

        [Fact]
        public void DecodeInteger_TwoBytes()
        {
            long val = Asn1BerCodec.DecodeInteger(new byte[] { 0x01, 0x00 }, 0, 2);
            Assert.Equal(256, val);
        }

        // ── DecodeVisibleString ────────────────────

        [Fact]
        public void DecodeVisibleString_Normal()
        {
            byte[] data = new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
            string result = Asn1BerCodec.DecodeVisibleString(data, 0, 5);
            Assert.Equal("hello", result);
        }

        [Fact]
        public void DecodeVisibleString_Empty()
        {
            string result = Asn1BerCodec.DecodeVisibleString(Array.Empty<byte>(), 0, 0);
            Assert.Equal("", result);
        }

        // ── DecodeOctetString ──────────────────────

        [Fact]
        public void DecodeOctetString_RoundTrip()
        {
            byte[] original = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            byte[] encoded = Asn1BerCodec.EncodeOctetString(original);
            var tag = Asn1BerCodec.DecodeTag(encoded, 0);
            byte[] decoded = Asn1BerCodec.DecodeOctetString(encoded, tag.ContentOffset, tag.Length);
            Assert.Equal(original, decoded);
        }

        // ── BuildConfirmedRequest ──────────────────

        [Fact]
        public void BuildConfirmedRequest_Format()
        {
            byte[] servicePdu = new byte[] { 0xA4, 0x03, 0x01, 0x02, 0x03 };
            byte[] result = Asn1BerCodec.BuildConfirmedRequest(1, servicePdu);

            // Should be: [0xA0][len][0x02][0x01][0x01][servicePdu]
            Assert.Equal(0xA0, result[0]); // TagConfirmedRequest
            var outerTag = Asn1BerCodec.DecodeTag(result, 0);
            Assert.True(outerTag.IsConstructed);
        }

        [Fact]
        public void BuildConfirmedRequest_RoundTrip()
        {
            byte[] servicePdu = new byte[] { 0xA4, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05 };
            byte[] result = Asn1BerCodec.BuildConfirmedRequest(42, servicePdu);

            var pduInfo = Asn1BerCodec.DecodeMmsPdu(result);
            Assert.Equal(MmsPduType.ConfirmedRequest, pduInfo.PduType);
            Assert.Equal(42, pduInfo.InvokeId);
        }

        // ── BuildConfirmedResponse ─────────────────

        [Fact]
        public void BuildConfirmedResponse_RoundTrip()
        {
            byte[] servicePdu = new byte[] { 0xA4, 0x02, 0x00, 0x00 };
            byte[] result = Asn1BerCodec.BuildConfirmedResponse(99, servicePdu);

            var pduInfo = Asn1BerCodec.DecodeMmsPdu(result);
            Assert.Equal(MmsPduType.ConfirmedResponse, pduInfo.PduType);
            Assert.Equal(99, pduInfo.InvokeId);
        }

        // ── BuildGetDataValuesRequest ──────────────

        [Fact]
        public void BuildGetDataValuesRequest_ProducesValidBER()
        {
            byte[] result = Asn1BerCodec.BuildGetDataValuesRequest("LD0/LLN0.Beh");
            Assert.NotNull(result);
            Assert.True(result.Length > 4);

            // Should start with TagRead (0xA4)
            Assert.Equal(Asn1BerCodec.TagRead, result[0]);

            // Should be decodable as a BER TLV
            var tag = Asn1BerCodec.DecodeTag(result, 0);
            Assert.Equal(Asn1BerCodec.TagRead, tag.Tag);
            Assert.True(tag.IsConstructed);
            Assert.Equal(result.Length - 1 - Asn1BerCodec.GetLengthBytes(result, 1), tag.Length);
        }

        [Fact]
        public void BuildGetDataValuesRequest_ContainsObjectRef()
        {
            string objectRef = "LD0/GGIO1.Ind1.stVal";
            byte[] result = Asn1BerCodec.BuildGetDataValuesRequest(objectRef);

            // The BER-encoded data should contain the object reference bytes
            // Find the VisibleString within the TLV structure
            var outerTag = Asn1BerCodec.DecodeTag(result, 0);
            int pos = outerTag.ContentOffset;
            // Navigate into the SEQUENCE and find the VisibleString
            var seqTag = Asn1BerCodec.DecodeTag(result, pos);
            // The content should contain the object reference
            string content = Encoding.ASCII.GetString(result);
            Assert.Contains("LD0/GGIO1.Ind1.stVal", content);
        }

        // ── BuildSetDataValuesRequest ──────────────

        [Fact]
        public void BuildSetDataValuesRequest_ProducesValidBER()
        {
            byte[] value = new byte[] { 0x01, 0x00 };
            byte[] result = Asn1BerCodec.BuildSetDataValuesRequest("LD0/LLN0.Beh", value);
            Assert.NotNull(result);
            Assert.True(result.Length > 4);
            Assert.Equal(Asn1BerCodec.TagWrite, result[0]);
        }

        // ── BuildGetDirectoryRequest ───────────────

        [Fact]
        public void BuildGetDirectoryRequest_ProducesValidBER()
        {
            byte[] result = Asn1BerCodec.BuildGetDirectoryRequest("LD0");
            Assert.NotNull(result);
            Assert.True(result.Length > 2);
            Assert.Equal(Asn1BerCodec.TagGetNameList, result[0]);
        }

        // ── BuildAssociateRequest ──────────────────

        [Fact]
        public void BuildAssociateRequest_ProducesValidBER()
        {
            byte[] result = Asn1BerCodec.BuildAssociateRequest(0, "1.0.9506.2.1");
            Assert.NotNull(result);
            Assert.True(result.Length > 4);
            // Outer tag should be MMS PDU wrapper
            Assert.Equal(0xA0, result[0]);
        }

        // ── BuildReleaseRequest ────────────────────

        [Fact]
        public void BuildReleaseRequest_ProducesValidBER()
        {
            byte[] result = Asn1BerCodec.BuildReleaseRequest(0);
            Assert.NotNull(result);
            Assert.True(result.Length > 2);
            Assert.Equal(0xA0, result[0]);
        }

        // ── DecodeMmsPdu ───────────────────────────

        [Fact]
        public void DecodeMmsPdu_ConfirmedRequest()
        {
            byte[] servicePdu = new byte[] { 0xA4, 0x02, 0x00, 0x00 };
            byte[] mmsPdu = Asn1BerCodec.BuildConfirmedRequest(7, servicePdu);

            var info = Asn1BerCodec.DecodeMmsPdu(mmsPdu);
            Assert.Equal(MmsPduType.ConfirmedRequest, info.PduType);
            Assert.Equal(7, info.InvokeId);
        }

        [Fact]
        public void DecodeMmsPdu_ConfirmedResponse()
        {
            byte[] servicePdu = new byte[] { 0xA4, 0x02, 0x00, 0x00 };
            byte[] mmsPdu = Asn1BerCodec.BuildConfirmedResponse(15, servicePdu);

            var info = Asn1BerCodec.DecodeMmsPdu(mmsPdu);
            Assert.Equal(MmsPduType.ConfirmedResponse, info.PduType);
            Assert.Equal(15, info.InvokeId);
        }

        [Fact]
        public void DecodeMmsPdu_InvalidData()
        {
            Assert.Throws<ArgumentException>(() => Asn1BerCodec.DecodeMmsPdu(new byte[] { 0x01 }));
        }

        // ── Integer round-trip ─────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-1)]
        [InlineData(127)]
        [InlineData(-128)]
        [InlineData(256)]
        [InlineData(-256)]
        [InlineData(32767)]
        [InlineData(-32768)]
        public void EncodeDecodeInteger_RoundTrip(long value)
        {
            byte[] encoded = Asn1BerCodec.EncodeInteger(value);
            var tag = Asn1BerCodec.DecodeTag(encoded, 0);
            long decoded = Asn1BerCodec.DecodeInteger(encoded, tag.ContentOffset, tag.Length);
            Assert.Equal(value, decoded);
        }

        // ── OctetString round-trip ─────────────────

        [Fact]
        public void EncodeDecodeOctetString_RoundTrip()
        {
            byte[] original = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
            byte[] encoded = Asn1BerCodec.EncodeOctetString(original);
            var tag = Asn1BerCodec.DecodeTag(encoded, 0);
            byte[] decoded = Asn1BerCodec.DecodeOctetString(encoded, tag.ContentOffset, tag.Length);
            Assert.Equal(original, decoded);
        }

        // ── VisibleString round-trip ───────────────

        [Fact]
        public void EncodeDecodeVisibleString_RoundTrip()
        {
            string original = "LD0/LLN0.Beh";
            byte[] encoded = Asn1BerCodec.EncodeVisibleString(original);
            var tag = Asn1BerCodec.DecodeTag(encoded, 0);
            string decoded = Asn1BerCodec.DecodeVisibleString(encoded, tag.ContentOffset, tag.Length);
            Assert.Equal(original, decoded);
        }

        // ── Tag constants ──────────────────────────

        [Fact]
        public void TagConstants_Correct()
        {
            Assert.Equal(0x01, Asn1BerCodec.TagBoolean);
            Assert.Equal(0x02, Asn1BerCodec.TagInteger);
            Assert.Equal(0x04, Asn1BerCodec.TagOctetString);
            Assert.Equal(0x05, Asn1BerCodec.TagNull);
            Assert.Equal(0x30, Asn1BerCodec.TagSequence);
            Assert.Equal(0xA0, Asn1BerCodec.TagConfirmedRequest);
            Assert.Equal(0xA1, Asn1BerCodec.TagConfirmedResponse);
        }

        // ── Null encoding ─────────────────────────

        [Fact]
        public void EncodeNull_Format()
        {
            byte[] result = Asn1BerCodec.EncodeNull();
            Assert.Equal(0x05, result[0]); // TagNull
            Assert.Equal(0x00, result[1]); // length = 0
        }

        // ── MmsPduType enum ────────────────────────

        [Fact]
        public void MmsPduType_AllDefined()
        {
            Assert.True(Enum.IsDefined(typeof(MmsPduType), MmsPduType.ConfirmedRequest));
            Assert.True(Enum.IsDefined(typeof(MmsPduType), MmsPduType.ConfirmedResponse));
            Assert.True(Enum.IsDefined(typeof(MmsPduType), MmsPduType.ConfirmedError));
            Assert.True(Enum.IsDefined(typeof(MmsPduType), MmsPduType.Unconfirmed));
            Assert.True(Enum.IsDefined(typeof(MmsPduType), MmsPduType.Reject));
            Assert.True(Enum.IsDefined(typeof(MmsPduType), MmsPduType.Unknown));
        }

        [Fact]
        public void MmsServiceType_AllDefined()
        {
            Assert.True(Enum.IsDefined(typeof(MmsServiceType), MmsServiceType.GetNameList));
            Assert.True(Enum.IsDefined(typeof(MmsServiceType), MmsServiceType.Read));
            Assert.True(Enum.IsDefined(typeof(MmsServiceType), MmsServiceType.Write));
        }

        [Fact]
        public void CotpClass_AllDefined()
        {
            Assert.True(Enum.IsDefined(typeof(CotpClass), CotpClass.Class0));
            Assert.True(Enum.IsDefined(typeof(CotpClass), CotpClass.Class4));
        }
    }
}
