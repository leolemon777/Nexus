using System;

namespace Nexus.Siemens
{
    /// <summary>
    /// 西门子 PLC 型号枚举。
    /// </summary>
    public enum SiemensPLCS
    {
        /// <summary>S7-200</summary>
        S7_200 = 0,
        /// <summary>S7-200Smart</summary>
        S7_200Smart = 1,
        /// <summary>S7-300</summary>
        S7_300 = 2,
        /// <summary>S7-400</summary>
        S7_400 = 3,
        /// <summary>S7-1200</summary>
        S7_1200 = 4,
        /// <summary>S7-1500</summary>
        S7_1500 = 5,
        /// <summary>S7-1200 (S7 Plus / TIA Portal 优化)</summary>
        S7_1200Plus = 6,
        /// <summary>S7-1500 (S7 Plus / TIA Portal 优化)</summary>
        S7_1500Plus = 7,
        /// <summary>LOGO!</summary>
        LOGO = 8,
    }

    /// <summary>
    /// S7 数据区类型（Variable Type）。
    /// </summary>
    public enum S7Area : byte
    {
        PE = 0x81,  // 输入区 I
        PA = 0x82,  // 输出区 Q
        MK = 0x83,  // 中间存储区 M
        DB = 0x84,  // 数据块 DB
        CT = 0x1C,  // 计数器
        TM = 0x1D,  // 定时器
        V = 0x85,   // V 存储区 (S7-200/SMART)
    }

    /// <summary>S7 数据类型码。</summary>
    public enum S7DataType : byte
    {
        /// <summary>位。</summary>
        Bit = 0x01,
        /// <summary>字节（8 位）。</summary>
        Byte = 0x02,
        /// <summary>字（16 位）。</summary>
        Word = 0x04,
        /// <summary>双字（32 位）。</summary>
        DInt = 0x06,
        /// <summary>实数（32 位浮点）。</summary>
        Real = 0x08,
        /// <summary>计数器。</summary>
        Counter = 0x1C,
        /// <summary>定时器。</summary>
        Timer = 0x1D,
    }

    /// <summary>S7 常量。</summary>
    public static class S7Constants
    {
        /// <summary>默认 S7 端口。</summary>
        public const int DefaultPort = 102;

        /// <summary>TPKT 头长度。</summary>
        public const int TpktHeaderLength = 4;

        /// <summary>COTP 连接请求头长度。</summary>
        public const int CotpCrLength = 11;

        /// <summary>COTP 数据头长度。</summary>
        public const int CotpDataLength = 3;

        /// <summary>S7 协议头长度。</summary>
        public const int S7HeaderLength = 10;

        /// <summary>默认 PDU 大小。</summary>
        public const ushort DefaultPduSize = 240;

        /// <summary>S7-1200/1500 最大 PDU。</summary>
        public const ushort MaxPduSize_1200 = 960;

        /// <summary>最大单个请求中的地址项数。</summary>
        public const int MaxAddressItems = 19;

        // ── S7 Message Type ──

        /// <summary>作业请求。</summary>
        public const byte MsgJob = 0x01;
        /// <summary>确认/ACK。</summary>
        public const byte MsgAck = 0x02;
        /// <summary>响应数据。</summary>
        public const byte MsgAckData = 0x03;
        /// <summary>用户数据。</summary>
        public const byte MsgUserData = 0x07;
    }

    /// <summary>S7 错误码。</summary>
    public static class S7ErrorCodes
    {
        /// <summary>获取 S7 错误类/码组合的中文描述。</summary>
        public static string GetDescription(byte errorClass, byte errorCode)
        {
            // 协议级错误
            if (errorClass == 0x00)
                return "无错误";

            // S7 通信错误 (error class 0x81)
            if (errorClass == 0x81)
            {
                switch (errorCode)
                {
                    case 0x00: return "应用关系错误";
                    case 0x01: return "对象定义错误";
                    case 0x02: return "无可用资源";
                    case 0x03: return "服务不支持";
                    case 0x04: return "服务不受对象支持";
                    case 0x05: return "对象访问错误";
                    case 0x06: return "参数接受 — 仅部分执行";
                    case 0x07: return "未找到请求的对象";
                    default: return $"S7 通信错误 (0x81:{errorCode:X2})";
                }
            }

            // 数据传输错误 (error class 0x82)
            if (errorClass == 0x82)
            {
                switch (errorCode)
                {
                    case 0x01: return "不正确的变量地址";
                    case 0x02: return "不正确的传输大小";
                    case 0x04: return "数据长度不匹配";
                    case 0x05: return "数据类型无效";
                    default: return $"数据传输错误 (0x82:{errorCode:X2})";
                }
            }

            // 功能错误 (error class 0x85)
            if (errorClass == 0x85)
            {
                switch (errorCode)
                {
                    case 0x00: return "无效的 PDU";
                    case 0x01: return "PDU 太长";
                    case 0x03: return "不支持的参数";
                    default: return $"功能错误 (0x85:{errorCode:X2})";
                }
            }

            // S7 Plus 优化块访问错误 (error class 0x87)
            if (errorClass == 0x87)
            {
                switch (errorCode)
                {
                    case 0x01: return "对象不存在";
                    case 0x02: return "对象不可访问（保护级别）";
                    case 0x04: return "地址无效";
                    case 0x05: return "类型不匹配";
                    case 0x06: return "对象状态不一致";
                    default: return $"S7 Plus 错误 (0x87:{errorCode:X2})";
                }
            }

            return $"未知错误 (0x{errorClass:X2}:0x{errorCode:X2})";
        }
    }
}
