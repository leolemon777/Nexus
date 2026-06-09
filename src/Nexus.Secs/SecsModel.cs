using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Secs
{
    /// <summary>SECS II 数据项格式码。</summary>
    public enum SecsFormatCode : byte
    {
        List = 0x00,
        Binary = 0x08,
        Boolean = 0x09,
        ASCII = 0x10,
        JIS8 = 0x11,
        Int8 = 0x18,
        Int16 = 0x19,
        Int32 = 0x1A,
        Int64 = 0x1B,
        UInt8 = 0x20,
        UInt16 = 0x21,
        UInt32 = 0x22,
        UInt64 = 0x23,
        Float32 = 0x28,
        Float64 = 0x29,
    }

    /// <summary>HSMS 连接状态。</summary>
    public enum HsmsState
    {
        NotConnected,
        NotSelected,
        Selected,
    }

    /// <summary>HSMS P-Type（消息类型）。</summary>
    public enum HsmsPType : byte
    {
        /// <summary>数据消息。</summary>
        DataMessage = 0,
        /// <summary>选择请求。</summary>
        SelectReq = 1,
        /// <summary>选择响应。</summary>
        SelectRsp = 2,
        /// <summary>去连接请求。</summary>
        DeselectReq = 3,
        /// <summary>去连接响应。</summary>
        DeselectRsp = 4,
        /// <summary>链接测试请求。</summary>
        LinkTestReq = 5,
        /// <summary>链接测试响应。</summary>
        LinkTestRsp = 6,
        /// <summary>拒绝。</summary>
        RejectReq = 7,
        /// <summary>独立处理请求。</summary>
        SeparateReq = 9,
    }

    /// <summary>HSMS 拒绝原因码。</summary>
    public enum HsmsRejectReason : byte
    {
        NotRecognized = 1,
        NotSelectable = 3,
        NotAvailable = 5,
        EntityNotRecognized = 6,
        NotInControl = 7,
        TransactionTimerOverflow = 8,
    }

    /// <summary>SECS 消息头（10 字节）。</summary>
    public struct SecsMessageHeader
    {
        /// <summary>会话 ID（2 字节）。</summary>
        public ushort SessionId;
        /// <summary>系统字节 / 事务 ID（4 字节）。</summary>
        public uint SystemBytes;
        /// <summary>消息 Stream。</summary>
        public byte Stream;
        /// <summary>消息 Function。</summary>
        public byte Function;
        /// <summary>是否需要回复（P-Type=W-bit）。</summary>
        public bool WaitForReply;
        /// <summary>设备 ID。</summary>
        public ushort DeviceId;

        /// <summary>SECS II 消息 ID (Stream * 256 + Function)。</summary>
        public int MessageId => Stream << 8 | Function;
    }

    /// <summary>HSMS 常量。</summary>
    public static class SecsConstants
    {
        /// <summary>HSMS 消息头长度（10 字节 + 4 字节系统字节）。</summary>
        public const int HsmsHeaderLength = 14;

        /// <summary>HSMS 消息长度字段长度。</summary>
        public const int HsmsLengthFieldLength = 4;

        /// <summary>默认 HSMS 端口。</summary>
        public const int DefaultPort = 5000;

        /// <summary>默认 T3 回复超时（秒）。</summary>
        public const int DefaultT3Timeout = 45;

        /// <summary>默认 T5 连接间隔（秒）。</summary>
        public const int DefaultT5Interval = 10;

        /// <summary>默认 T6 控制超时（秒）。</summary>
        public const int DefaultT6Timeout = 5;

        /// <summary>默认 T7 非活动超时（秒）。</summary>
        public const int DefaultT7Timeout = 10;

        /// <summary>默认 T8 网络字符超时（秒）。</summary>
        public const int DefaultT8Timeout = 5;

        /// <summary>最大 SECS 消息长度。</summary>
        public const int MaxMessageLength = 0x00FFFFFF;

        // 常用 SECS II 消息 ID
        public const int S1F1 = 0x0101; // Are You There
        public const int S1F2 = 0x0102; // On Line Data
        public const int S1F13 = 0x010D; // Establish Communication
        public const int S1F14 = 0x010E; // Establish Communication Ack
        public const int S2F13 = 0x020D; // Equipment Constant Request
        public const int S2F14 = 0x020E; // Equipment Constant Data
        public const int S2F15 = 0x020F; // New Equipment Constant Send
        public const int S2F16 = 0x0210; // New Equipment Constant Ack
        public const int S5F1 = 0x0501; // Alarm Report Send
        public const int S5F2 = 0x0502; // Alarm Report Ack
        public const int S6F11 = 0x060B; // Event Report Send
        public const int S6F12 = 0x060C; // Event Report Ack
    }

    /// <summary>SECS II 数据项。</summary>
    public sealed class SecsDataItem
    {
        /// <summary>格式码。</summary>
        public SecsFormatCode Format { get; }
        /// <summary>数据字节。</summary>
        public byte[] RawData { get; }
        /// <summary>子项列表（仅 List 类型）。</summary>
        public List<SecsDataItem>? Items { get; }

        private SecsDataItem(SecsFormatCode format, byte[] data, List<SecsDataItem>? items)
        {
            Format = format;
            RawData = data;
            Items = items;
        }

        /// <summary>创建 List 数据项。</summary>
        public static SecsDataItem CreateList(params SecsDataItem[] items)
            => new SecsDataItem(SecsFormatCode.List, Array.Empty<byte>(), new List<SecsDataItem>(items));

        /// <summary>创建 ASCII 数据项。</summary>
        public static SecsDataItem CreateASCII(string value)
            => new SecsDataItem(SecsFormatCode.ASCII, System.Text.Encoding.ASCII.GetBytes(value), null);

        /// <summary>创建 Binary 数据项。</summary>
        public static SecsDataItem CreateBinary(byte[] value)
            => new SecsDataItem(SecsFormatCode.Binary, value, null);

        /// <summary>创建 Boolean 数据项。</summary>
        public static SecsDataItem CreateBoolean(bool[] values)
        {
            var data = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) data[i] = values[i] ? (byte)1 : (byte)0;
            return new SecsDataItem(SecsFormatCode.Boolean, data, null);
        }

        /// <summary>创建 Int32 数据项。</summary>
        public static SecsDataItem CreateInt32(int value)
            => new SecsDataItem(SecsFormatCode.Int32, BitConverter.GetBytes(value), null);

        /// <summary>创建 UInt32 数据项。</summary>
        public static SecsDataItem CreateUInt32(uint value)
            => new SecsDataItem(SecsFormatCode.UInt32, BitConverter.GetBytes(value), null);

        /// <summary>创建 Int16 数据项。</summary>
        public static SecsDataItem CreateInt16(short value)
            => new SecsDataItem(SecsFormatCode.Int16, BitConverter.GetBytes(value), null);

        /// <summary>创建 UInt16 数据项。</summary>
        public static SecsDataItem CreateUInt16(ushort value)
            => new SecsDataItem(SecsFormatCode.UInt16, BitConverter.GetBytes(value), null);

        /// <summary>创建 Float32 数据项。</summary>
        public static SecsDataItem CreateFloat32(float value)
            => new SecsDataItem(SecsFormatCode.Float32, BitConverter.GetBytes(value), null);

        /// <summary>创建 Float64 数据项。</summary>
        public static SecsDataItem CreateFloat64(double value)
            => new SecsDataItem(SecsFormatCode.Float64, BitConverter.GetBytes(value), null);

        /// <summary>获取 ASCII 字符串值。</summary>
        public string GetASCII() => System.Text.Encoding.ASCII.GetString(RawData);

        /// <summary>获取 Int32 值。</summary>
        public int GetInt32() => BitConverter.ToInt32(RawData, 0);

        /// <summary>获取 UInt32 值。</summary>
        public uint GetUInt32() => BitConverter.ToUInt32(RawData, 0);

        /// <summary>获取 Int16 值。</summary>
        public short GetInt16() => BitConverter.ToInt16(RawData, 0);

        /// <summary>获取 UInt16 值。</summary>
        public ushort GetUInt16() => BitConverter.ToUInt16(RawData, 0);

        /// <summary>获取 Float32 值。</summary>
        public float GetFloat32() => BitConverter.ToSingle(RawData, 0);

        /// <summary>获取 Float64 值。</summary>
        public double GetFloat64() => BitConverter.ToDouble(RawData, 0);

        /// <summary>数据项数量（List 为子项数，其他为 RawData 长度）。</summary>
        public int Count => Format == SecsFormatCode.List ? (Items?.Count ?? 0) : RawData.Length;

        /// <summary>是否为 List 类型。</summary>
        public bool IsList => Format == SecsFormatCode.List;
    }
}
