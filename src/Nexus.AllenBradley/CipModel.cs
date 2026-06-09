using System;

namespace Nexus.AllenBradley
{
    /// <summary>Allen-Bradley PLC 型号。</summary>
    public enum AbPlcModel
    {
        /// <summary>ControlLogix 5570。</summary>
        ControlLogix5570,
        /// <summary>ControlLogix 5580。</summary>
        ControlLogix5580,
        /// <summary>CompactLogix 5370。</summary>
        CompactLogix5370,
        /// <summary>CompactLogix 5380。</summary>
        CompactLogix5380,
        /// <summary>CompactLogix 5480。</summary>
        CompactLogix5480,
        /// <summary>MicroLogix 1400。</summary>
        MicroLogix1400,
        /// <summary>MicroLogix 1100。</summary>
        MicroLogix1100,
        /// <summary>Micro850。</summary>
        Micro850,
        /// <summary>Micro800 (820/850/870)。</summary>
        Micro800,
        /// <summary>PLC-5。</summary>
        PLC5,
        /// <summary>SLC 500。</summary>
        SLC500,
    }

    /// <summary>CIP 数据类型码。</summary>
    public enum CipDataType : ushort
    {
        /// <summary>布尔型 (BOOL)。</summary>
        Bool = 0x00C1,
        /// <summary>8 位有符号整数 (SINT)。</summary>
        Sint = 0x00C2,
        /// <summary>16 位有符号整数 (INT)。</summary>
        Int = 0x00C3,
        /// <summary>32 位有符号整数 (DINT)。</summary>
        Dint = 0x00C4,
        /// <summary>64 位有符号整数 (LINT)。</summary>
        Lint = 0x00C5,
        /// <summary>8 位无符号整数 (USINT)。</summary>
        Usint = 0x00C6,
        /// <summary>16 位无符号整数 (UINT)。</summary>
        Uint = 0x00C7,
        /// <summary>32 位无符号整数 (UDINT)。</summary>
        Udint = 0x00C8,
        /// <summary>64 位无符号整数 (ULINT)。</summary>
        Ulint = 0x00C9,
        /// <summary>32 位浮点 (REAL)。</summary>
        Real = 0x00CA,
        /// <summary>64 位浮点 (LREAL)。</summary>
        Lreal = 0x00CB,
        /// <summary>字符串。</summary>
        String = 0x00D0,
        /// <summary>字节 (BYTE)。</summary>
        Byte = 0x00D1,
        /// <summary>字 (WORD)。</summary>
        Word = 0x00D2,
        /// <summary>双字 (DWORD)。</summary>
        Dword = 0x00D3,
        /// <summary>结构体。</summary>
        Struct = 0x02A0,
    }

    /// <summary>CIP 服务码。</summary>
    public enum CipService : byte
    {
        /// <summary>读取标签 (Get Attribute All)。</summary>
        Read = 0x4C,
        /// <summary>读取标签分片。</summary>
        ReadFragmented = 0x52,
        /// <summary>写入标签 (Set Attribute All)。</summary>
        Write = 0x4D,
        /// <summary>写入标签分片。</summary>
        WriteFragmented = 0x53,
        /// <summary>多服务请求。</summary>
        MultipleService = 0x0A,
        /// <summary>获取属性列表。</summary>
        GetAttributeList = 0x03,
        /// <summary>设置属性列表。</summary>
        SetAttributeList = 0x04,
        /// <summary>转发打开 (Forward Open)。</summary>
        ForwardOpen = 0x54,
        /// <summary>转发关闭 (Forward Close)。</summary>
        ForwardClose = 0x4E,
    }

    /// <summary>ENIP 命令码。</summary>
    public enum EnipCommand : ushort
    {
        /// <summary>NOP。</summary>
        Nop = 0x0001,
        /// <summary>列出身份。</summary>
        ListIdentity = 0x0063,
        /// <summary>列出接口。</summary>
        ListInterfaces = 0x0064,
        /// <summary>列出服务。</summary>
        ListServices = 0x0065,
        /// <summary>注册会话。</summary>
        RegisterSession = 0x0065,
        /// <summary>取消注册会话。</summary>
        UnregisterSession = 0x0066,
        /// <summary>发送 RRData。</summary>
        SendRRData = 0x006F,
        /// <summary>发送 UNIT 数据。</summary>
        SendUnitData = 0x0070,
    }

    /// <summary>CIP / EtherNet/IP 常量。</summary>
    public static class CipConstants
    {
        /// <summary>默认 EtherNet/IP 端口。</summary>
        public const int DefaultPort = 44818;

        /// <summary>默认 CIP 显式端口 (注册端口)。</summary>
        public const int DefaultImplicitPort = 2222;

        /// <summary>ENIP 头长度 (24 字节)。</summary>
        public const int EnipHeaderLength = 24;

        /// <summary>CIP 连接头长度。</summary>
        public const int CipConnectionHeaderLength = 6;

        /// <summary>默认最大 PDU 大小 (ControlLogix)。</summary>
        public const int DefaultMaxPduSize = 508;

        /// <summary>最大标签名长度。</summary>
        public const int MaxTagNameLength = 82;

        /// <summary>符号段类型标识 (0x91)。</summary>
        public const byte SymbolicSegmentType = 0x91;

        /// <summary>扩展符号段标识 (0x91 + 0x00)。</summary>
        public const byte ExtendedSymbolMarker = 0x00;

        // ── CIP 状态码 ──

        /// <summary>成功。</summary>
        public const byte StatusSuccess = 0x00;

        /// <summary>连接失败。</summary>
        public const byte StatusConnectionFailure = 0x01;

        /// <summary>资源不可用。</summary>
        public const byte StatusResourceUnavailable = 0x02;

        /// <summary>路径目的地未知。</summary>
        public const byte StatusPathDestinationUnknown = 0x03;

        /// <summary>路径段错误。</summary>
        public const byte StatusPathSegmentError = 0x04;

        /// <summary>标签不存在。</summary>
        public const byte StatusTagNotFound = 0x04;
    }

    /// <summary>CIP 错误码。</summary>
    public static class CipErrorCodes
    {
        /// <summary>获取 CIP 扩展状态码的中文描述。</summary>
        public static string GetDescription(byte status, byte extendedStatus = 0)
        {
            switch (status)
            {
                case 0x00: return "成功";
                case 0x01: return "连接失败";
                case 0x02: return "资源不足或不可用";
                case 0x03: return "参数值无效";
                case 0x04: return "路径段错误 — 标签不存在或路径不正确";
                case 0x05: return "路径目的地未知";
                case 0x06: return "部分传输 — 仅完成部分操作";
                case 0x07: return "连接丢失";
                case 0x08: return "服务不支持";
                case 0x09: return "属性数据错误";
                case 0x0A: return "属性列表错误";
                case 0x0B: return "状态冲突 — 当前状态不允许此操作";
                case 0x0C: return "属性不支持 — 标签只读";
                case 0x0D: return "命令已排队";
                case 0x0E: return "属性不支持设置";
                case 0x0F: return "属性不支持获取";
                case 0x10: return "属性列表不支持";
                case 0x11: return "忙 — 正在处理上一个请求";
                default: return $"未知错误 (0x{status:X2}, ext=0x{extendedStatus:X2})";
            }
        }
    }
}
