using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Iec61850
{
    /// <summary>
    /// 简化的 ASN.1 BER 编解码器 — 覆盖 MMS 协议所需的 BER 子集。
    /// <para>仅支持 MMS 常用的标签类型，不实现完整 ASN.1 库。</para>
    /// </summary>
    public static class Asn1BerCodec
    {
        // ── BER Tag numbers used by MMS ──

        public const byte TagBoolean = 0x01;
        public const byte TagInteger = 0x02;
        public const byte TagBitString = 0x03;
        public const byte TagOctetString = 0x04;
        public const byte TagNull = 0x05;
        public const byte TagObjectId = 0x06;
        public const byte TagUtf8String = 0x0C;
        public const byte TagSequence = 0x30;
        public const byte TagSet = 0x31;
        public const byte TagApplication = 0x60;

        // Context-specific tags for MMS PDUs
        public const byte TagConfirmedRequest = 0xA0;
        public const byte TagConfirmedResponse = 0xA1;
        public const byte TagConfirmedError = 0xA2;
        public const byte TagUnconfirmed = 0xA3;
        public const byte TagReject = 0xA4;

        // MMS Confirmed-Request service choices (context-specific)
        public const byte TagGetNameList = 0xA0;
        public const byte TagRead = 0xA4;
        public const byte TagWrite = 0xA5;
        public const byte TagGetVariableAccessAttributes = 0xA2;
        public const byte TagDefineNamedVariable = 0xA3;
        public const byte TagDeleteNamedVariableAccess = 0xA6;

        // MMS Confirmed-Response service choices
        public const byte TagGetNameListResponse = 0xA0;
        public const byte TagReadResponse = 0xA4;
        public const byte TagWriteResponse = 0xA5;

        // ═══════════════════════════════════════════
        //  Encode — 基础 BER 编码
        // ═══════════════════════════════════════════

        /// <summary>编码 BER Integer。</summary>
        public static byte[] EncodeInteger(long value)
        {
            byte[] raw = EncodeIntegerRaw(value);
            byte[] result = new byte[2 + raw.Length];
            result[0] = TagInteger;
            result[1] = (byte)raw.Length;
            Buffer.BlockCopy(raw, 0, result, 2, raw.Length);
            return result;
        }

        /// <summary>编码 BER OctetString。</summary>
        public static byte[] EncodeOctetString(byte[] data)
        {
            if (data == null) data = Array.Empty<byte>();
            byte[] lenBytes = EncodeLength(data.Length);
            byte[] result = new byte[1 + lenBytes.Length + data.Length];
            result[0] = TagOctetString;
            Buffer.BlockCopy(lenBytes, 0, result, 1, lenBytes.Length);
            if (data.Length > 0)
                Buffer.BlockCopy(data, 0, result, 1 + lenBytes.Length, data.Length);
            return result;
        }

        /// <summary>编码 BER VisibleString (IA5String)。</summary>
        public static byte[] EncodeVisibleString(string value)
        {
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? "");
            byte[] lenBytes = EncodeLength(strBytes.Length);
            // VisibleString tag = 0x1A
            byte[] result = new byte[1 + lenBytes.Length + strBytes.Length];
            result[0] = 0x1A;
            Buffer.BlockCopy(lenBytes, 0, result, 1, lenBytes.Length);
            if (strBytes.Length > 0)
                Buffer.BlockCopy(strBytes, 0, result, 1 + lenBytes.Length, strBytes.Length);
            return result;
        }

        /// <summary>编码 BER UTF8String。</summary>
        public static byte[] EncodeUtf8String(string value)
        {
            byte[] strBytes = Encoding.UTF8.GetBytes(value ?? "");
            byte[] lenBytes = EncodeLength(strBytes.Length);
            byte[] result = new byte[1 + lenBytes.Length + strBytes.Length];
            result[0] = TagUtf8String;
            Buffer.BlockCopy(lenBytes, 0, result, 1, lenBytes.Length);
            if (strBytes.Length > 0)
                Buffer.BlockCopy(strBytes, 0, result, 1 + lenBytes.Length, strBytes.Length);
            return result;
        }

        /// <summary>编码 BER Boolean。</summary>
        public static byte[] EncodeBoolean(bool value)
        {
            return new byte[] { TagBoolean, 0x01, (byte)(value ? 0xFF : 0x00) };
        }

        /// <summary>编码 BER Null。</summary>
        public static byte[] EncodeNull()
        {
            return new byte[] { TagNull, 0x00 };
        }

        /// <summary>编码 BER Sequence (constructed)。</summary>
        public static byte[] EncodeSequence(byte[] content)
        {
            return EncodeTagged(TagSequence, content);
        }

        /// <summary>编码带标签的 BER TLV（constructed）。</summary>
        public static byte[] EncodeTagged(byte tag, byte[] content)
        {
            if (content == null) content = Array.Empty<byte>();
            byte[] lenBytes = EncodeLength(content.Length);
            byte[] result = new byte[1 + lenBytes.Length + content.Length];
            result[0] = tag;
            Buffer.BlockCopy(lenBytes, 0, result, 1, lenBytes.Length);
            if (content.Length > 0)
                Buffer.BlockCopy(content, 0, result, 1 + lenBytes.Length, content.Length);
            return result;
        }

        /// <summary>编码 BER ObjectId (OID)。</summary>
        public static byte[] EncodeObjectId(byte[] oidValue)
        {
            if (oidValue == null) oidValue = Array.Empty<byte>();
            byte[] lenBytes = EncodeLength(oidValue.Length);
            byte[] result = new byte[1 + lenBytes.Length + oidValue.Length];
            result[0] = TagObjectId;
            Buffer.BlockCopy(lenBytes, 0, result, 1, lenBytes.Length);
            if (oidValue.Length > 0)
                Buffer.BlockCopy(oidValue, 0, result, 1 + lenBytes.Length, oidValue.Length);
            return result;
        }

        // ═══════════════════════════════════════════
        //  Encode — MMS PDU 构建
        // ═══════════════════════════════════════════

        /// <summary>构建 MMS Confirmed-Request PDU。</summary>
        public static byte[] BuildConfirmedRequest(ushort invokeId, byte[] servicePdu)
        {
            byte[] invokeIdTlv = EncodeInteger(invokeId);
            byte[] content = Concat(invokeIdTlv, servicePdu);
            return EncodeTagged(TagConfirmedRequest, content);
        }

        /// <summary>构建 MMS Confirmed-Response PDU。</summary>
        public static byte[] BuildConfirmedResponse(ushort invokeId, byte[] servicePdu)
        {
            byte[] invokeIdTlv = EncodeInteger(invokeId);
            byte[] content = Concat(invokeIdTlv, servicePdu);
            return EncodeTagged(TagConfirmedResponse, content);
        }

        /// <summary>构建 MMS Associate-Request PDU。</summary>
        public static byte[] BuildAssociateRequest(ushort invokeId, string applicationContext)
        {
            // MMS Initiate-RequestPDU (tag 0x28) containing:
            //   localDetailCalling (context 0) = proposedMaxPduSize
            //   proposedMaxServCalling (context 1)
            //   proposedDataStructureNestingLevel (context 3)

            byte[] localDetail = EncodeTagged(0x80, EncodeIntegerRaw(65000));
            byte[] maxServCalling = EncodeTagged(0x81, EncodeIntegerRaw(10));
            byte[] maxServCalled = EncodeTagged(0x82, EncodeIntegerRaw(10));
            byte[] nestingLevel = EncodeTagged(0x83, new byte[] { 0x05 });

            byte[] initiateContent = Concat(localDetail, Concat(maxServCalling, Concat(maxServCalled, nestingLevel)));
            byte[] initiatePdu = EncodeTagged(0x28, initiateContent);

            // Wrap in MMS PDU: [context 0] initiateRequest
            byte[] mmsPdu = EncodeTagged(0xA0, initiatePdu);
            return mmsPdu;
        }

        /// <summary>构建 MMS Release-Request PDU。</summary>
        public static byte[] BuildReleaseRequest(ushort invokeId)
        {
            // ReleaseRequestPDU (tag 0x29) with optional reason
            byte[] releaseContent = EncodeInteger(0); // reason = normal
            byte[] releasePdu = EncodeTagged(0x29, releaseContent);
            return EncodeTagged(0xA0, releasePdu);
        }

        /// <summary>构建 MMS GetDataValues 请求 PDU。</summary>
        public static byte[] BuildGetDataValuesRequest(string objectRef)
        {
            // Read service (choice 4):
            //   variableAccessSpecification ::= listOfVariable
            //     SEQUENCE OF SEQUENCE { variableSpecification, listOfData }

            byte[] varSpec = EncodeTagged(0x80, EncodeVisibleString(objectRef));
            byte[] listOfData = EncodeTagged(0x80, new byte[0]); // empty = all attributes
            byte[] varAccessItem = EncodeSequence(Concat(varSpec, listOfData));
            byte[] listOfVariable = EncodeSequence(varAccessItem);
            byte[] readSpec = EncodeTagged(0xA0, listOfVariable);

            return EncodeTagged(TagRead, readSpec);
        }

        /// <summary>构建 MMS SetDataValues 请求 PDU。</summary>
        public static byte[] BuildSetDataValuesRequest(string objectRef, byte[] value)
        {
            // Write service (choice 5):
            //   listOfData SEQUENCE OF SEQUENCE { variableSpecification, listOfData }

            byte[] varSpec = EncodeTagged(0x80, EncodeVisibleString(objectRef));
            byte[] dataValue = EncodeTagged(0x80, value); // context-specific Data
            byte[] varAccessItem = EncodeSequence(Concat(varSpec, dataValue));
            byte[] listOfVariable = EncodeSequence(varAccessItem);
            byte[] writeSpec = EncodeTagged(0xA0, listOfVariable);

            return EncodeTagged(TagWrite, writeSpec);
        }

        /// <summary>构建 MMS GetDirectory 请求 PDU。</summary>
        public static byte[] BuildGetDirectoryRequest(string directory)
        {
            // getNameList (choice 0)
            byte[] objectId = EncodeTagged(0x80, EncodeVisibleString(directory));
            byte[] continueAfter = EncodeTagged(0x81, new byte[0]); // empty = start from beginning
            byte[] getNameListContent = Concat(objectId, continueAfter);

            return EncodeTagged(TagGetNameList, getNameListContent);
        }

        // ═══════════════════════════════════════════
        //  Encode — 长度编码
        // ═══════════════════════════════════════════

        /// <summary>编码 BER 长度字段（确定形式）。</summary>
        public static byte[] EncodeLength(int length)
        {
            if (length < 0x80)
            {
                return new byte[] { (byte)length };
            }
            else if (length <= 0xFF)
            {
                return new byte[] { 0x81, (byte)length };
            }
            else if (length <= 0xFFFF)
            {
                return new byte[] { 0x82, (byte)(length >> 8), (byte)length };
            }
            else
            {
                return new byte[] { 0x83, (byte)(length >> 16), (byte)(length >> 8), (byte)length };
            }
        }

        /// <summary>解码 BER 长度字段。</summary>
        public static int DecodeLength(byte[] data, int offset)
        {
            if (data == null || offset >= data.Length)
                throw new ArgumentException("数据不足");

            byte first = data[offset];
            if (first < 0x80)
                return first;

            int numBytes = first & 0x7F;
            if (numBytes == 0)
                throw new ArgumentException("不定长度形式不支持");

            int length = 0;
            for (int i = 0; i < numBytes; i++)
            {
                if (offset + 1 + i >= data.Length)
                    throw new ArgumentException("长度字段数据不足");
                length = (length << 8) | data[offset + 1 + i];
            }
            return length;
        }

        /// <summary>获取长度字段占用的字节数。</summary>
        public static int GetLengthBytes(byte[] data, int offset)
        {
            if (data == null || offset >= data.Length)
                return 0;

            byte first = data[offset];
            if (first < 0x80)
                return 1;

            int numBytes = first & 0x7F;
            return 1 + numBytes;
        }

        // ═══════════════════════════════════════════
        //  Decode — BER 解码
        // ═══════════════════════════════════════════

        /// <summary>解码 BER Tag + Length，返回 BerTag 结构。</summary>
        public static BerTag DecodeTag(byte[] data, int offset)
        {
            if (data == null || offset >= data.Length)
                throw new ArgumentException("数据不足");

            byte tagByte = data[offset];
            bool isConstructed = (tagByte & 0x20) != 0;

            int lengthBytes = GetLengthBytes(data, offset + 1);
            int contentLength = DecodeLength(data, offset + 1);

            return new BerTag
            {
                Tag = tagByte,
                IsConstructed = isConstructed,
                Length = contentLength,
                ContentOffset = offset + 1 + lengthBytes
            };
        }

        /// <summary>解码 BER Integer 值。</summary>
        public static long DecodeInteger(byte[] data, int offset, int length)
        {
            if (length == 0) return 0;

            long value = (data[offset] & 0x80) != 0 ? -1L : 0L;
            for (int i = 0; i < length; i++)
            {
                value = (value << 8) | data[offset + i];
            }
            return value;
        }

        /// <summary>从 BER TLV 中解码 Integer。</summary>
        public static long DecodeIntegerTlv(byte[] data, int offset)
        {
            var tag = DecodeTag(data, offset);
            if (tag.Tag != TagInteger)
                throw new ArgumentException($"期望 Integer tag (0x02), 实际 0x{tag.Tag:X2}");
            return DecodeInteger(data, tag.ContentOffset, tag.Length);
        }

        /// <summary>解码 BER VisibleString。</summary>
        public static string DecodeVisibleString(byte[] data, int offset, int length)
        {
            if (length == 0) return "";
            return Encoding.ASCII.GetString(data, offset, length);
        }

        /// <summary>从 BER TLV 中解码 VisibleString。</summary>
        public static string DecodeVisibleStringTlv(byte[] data, int offset)
        {
            var tag = DecodeTag(data, offset);
            return DecodeVisibleString(data, tag.ContentOffset, tag.Length);
        }

        /// <summary>解码 BER OctetString。</summary>
        public static byte[] DecodeOctetString(byte[] data, int offset, int length)
        {
            byte[] result = new byte[length];
            if (length > 0)
                Buffer.BlockCopy(data, offset, result, 0, length);
            return result;
        }

        /// <summary>从 BER TLV 中解码 OctetString。</summary>
        public static byte[] DecodeOctetStringTlv(byte[] data, int offset)
        {
            var tag = DecodeTag(data, offset);
            return DecodeOctetString(data, tag.ContentOffset, tag.Length);
        }

        /// <summary>解码 BER Boolean。</summary>
        public static bool DecodeBoolean(byte[] data, int offset, int length)
        {
            if (length == 0) return false;
            return data[offset] != 0x00;
        }

        // ═══════════════════════════════════════════
        //  Decode — MMS PDU 解析
        // ═══════════════════════════════════════════

        /// <summary>解析 MMS PDU，提取 PDU 类型和 InvokeId。</summary>
        public static MmsPduInfo DecodeMmsPdu(byte[] data)
        {
            if (data == null || data.Length < 4)
                throw new ArgumentException("MMS PDU 数据不足");

            var outerTag = DecodeTag(data, 0);
            MmsPduType pduType;
            switch (outerTag.Tag)
            {
                case TagConfirmedRequest: pduType = MmsPduType.ConfirmedRequest; break;
                case TagConfirmedResponse: pduType = MmsPduType.ConfirmedResponse; break;
                case TagConfirmedError: pduType = MmsPduType.ConfirmedError; break;
                case TagUnconfirmed: pduType = MmsPduType.Unconfirmed; break;
                case TagReject: pduType = MmsPduType.Reject; break;
                default: pduType = MmsPduType.Unknown; break;
            }

            ushort invokeId = 0;
            int pos = outerTag.ContentOffset;
            if (pos < data.Length)
            {
                var idTag = DecodeTag(data, pos);
                if (idTag.Tag == TagInteger)
                {
                    invokeId = (ushort)DecodeInteger(data, idTag.ContentOffset, idTag.Length);
                }
            }

            return new MmsPduInfo
            {
                PduType = pduType,
                InvokeId = invokeId,
                ContentOffset = outerTag.ContentOffset,
                ContentLength = outerTag.Length
            };
        }

        // ═══════════════════════════════════════════
        //  辅助方法
        // ═══════════════════════════════════════════

        private static byte[] EncodeIntegerRaw(long value)
        {
            if (value == 0) return new byte[] { 0x00 };

            int bytesNeeded = 1;
            long tmp = value;
            while (tmp > 127 || tmp < -128)
            {
                bytesNeeded++;
                tmp >>= 8;
            }

            byte[] result = new byte[bytesNeeded];
            for (int i = bytesNeeded - 1; i >= 0; i--)
            {
                result[i] = (byte)(value & 0xFF);
                value >>= 8;
            }
            return result;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }
    }

    /// <summary>BER Tag 解码结果。</summary>
    public struct BerTag
    {
        public byte Tag;
        public bool IsConstructed;
        public int Length;
        public int ContentOffset;
    }

    /// <summary>MMS PDU 解析结果。</summary>
    public class MmsPduInfo
    {
        public MmsPduType PduType { get; set; }
        public ushort InvokeId { get; set; }
        public int ContentOffset { get; set; }
        public int ContentLength { get; set; }
    }
}
