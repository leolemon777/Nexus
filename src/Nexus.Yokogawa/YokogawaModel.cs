using System;

namespace Nexus.Yokogawa
{
    /// <summary>横河 PLC 型号。</summary>
    public enum YokogawaModel
    {
        /// <summary>FA-M3 系列。</summary>
        FAM3,
        /// <summary>FA-M3 Rangefree。</summary>
        FAM3RangeFree,
        /// <summary>Vnet/IP 控制器。</summary>
        VnetIP,
        /// <summary>STARDOM 自主控制器。</summary>
        Stardom,
        /// <summary>FCN (Field Control Node)。</summary>
        FCN,
        /// <summary>FCJ (Field Control Junction)。</summary>
        FCJ,
        /// <summary>CENTUM VP。</summary>
        CentumVP,
    }

    /// <summary>横河 Vnet/IP 常量。</summary>
    public static class YokogawaConstants
    {
        /// <summary>默认 TCP 端口。</summary>
        public const int DefaultPort = 10001;

        /// <summary>Vnet/IP 帧头长度。</summary>
        public const int FrameHeaderLength = 28;

        /// <summary>最大单次读取寄存器数量。</summary>
        public const int MaxReadRegisters = 512;

        /// <summary>最大单次写入寄存器数量。</summary>
        public const int MaxWriteRegisters = 512;

        /// <summary>最大单次读取继电器数量。</summary>
        public const int MaxReadRelays = 4096;

        /// <summary>最大单次写入继电器数量。</summary>
        public const int MaxWriteRelays = 4096;

        // ── 数据代码 ──

        /// <summary>D 寄存器数据代码。</summary>
        public const int DataCodeD = 4;
        /// <summary>B 寄存器数据代码。</summary>
        public const int DataCodeB = 2;
        /// <summary>F 寄存器数据代码。</summary>
        public const int DataCodeF = 6;
        /// <summary>R 寄存器数据代码。</summary>
        public const int DataCodeR = 18;
        /// <summary>V 寄存器数据代码。</summary>
        public const int DataCodeV = 22;
        /// <summary>Z 寄存器数据代码。</summary>
        public const int DataCodeZ = 26;
        /// <summary>W 寄存器数据代码。</summary>
        public const int DataCodeW = 23;
        /// <summary>TN 寄存器数据代码（定时器当前值）。</summary>
        public const int DataCodeTN = 33;
        /// <summary>CN 寄存器数据代码（计数器当前值）。</summary>
        public const int DataCodeCN = 49;

        /// <summary>X 继电器数据代码。</summary>
        public const int DataCodeX = 24;
        /// <summary>Y 继电器数据代码。</summary>
        public const int DataCodeY = 25;
        /// <summary>I 继电器数据代码。</summary>
        public const int DataCodeI = 9;
        /// <summary>E 继电器数据代码。</summary>
        public const int DataCodeE = 5;
        /// <summary>M 继电器数据代码。</summary>
        public const int DataCodeM = 13;
        /// <summary>T 继电器数据代码。</summary>
        public const int DataCodeT = 20;
        /// <summary>C 继电器数据代码。</summary>
        public const int DataCodeC = 3;
        /// <summary>L 继电器数据代码。</summary>
        public const int DataCodeL = 12;
    }

    /// <summary>横河错误码。</summary>
    public static class YokogawaErrorCodes
    {
        /// <summary>获取错误码的中文描述。</summary>
        public static string GetDescription(int errorCode)
        {
            switch (errorCode)
            {
                case 0: return "正常完成";
                case 1: return "命令格式错误";
                case 2: return "数据代码错误 — 不支持的区域";
                case 3: return "地址错误 — 超出范围";
                case 4: return "数据长度错误";
                case 5: return "写入数据错误 — 数值无效";
                case 6: return "PLC 处于保护模式";
                case 7: return "通信超时";
                case 8: return "PLC 繁忙";
                case 9: return "系统错误";
                case 10: return "连接被拒绝";
                case 11: return "不支持的功能码";
                case 12: return "校验错误";
                case 13: return "地址越界";
                default: return $"未知错误 ({errorCode})";
            }
        }
    }
}
