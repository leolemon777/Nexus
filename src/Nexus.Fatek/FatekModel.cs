using System;

namespace Nexus.Fatek
{
    /// <summary>Fatek 常量。</summary>
    public static class FatekConstants
    {
        /// <summary>默认 TCP 端口。</summary>
        public const int DefaultTcpPort = 5000;

        /// <summary>默认串口波特率。</summary>
        public const int DefaultBaudRate = 9600;

        /// <summary>默认站号。</summary>
        public const byte DefaultStation = 1;

        /// <summary>STX 字符 (0x02)。</summary>
        public const byte STX = 0x02;

        /// <summary>ETX 字符 (0x03)。</summary>
        public const byte ETX = 0x03;

        /// <summary>单次最大读取寄存器数量。</summary>
        public const int MaxReadRegisters = 64;

        /// <summary>单次最大写入寄存器数量。</summary>
        public const int MaxWriteRegisters = 64;

        /// <summary>单次最大读取/写入位数量。</summary>
        public const int MaxBits = 256;

        // ── 命令代码 ──

        /// <summary>读取单个/多个寄存器或位。</summary>
        public const string CmdRead = "R";

        /// <summary>写入单个/多个寄存器或位。</summary>
        public const string CmdWrite = "W";

        /// <summary>批量位读取。</summary>
        public const string CmdBatchReadBits = "R";

        /// <summary>批量位写入。</summary>
        public const string CmdBatchWriteBits = "W";

        // ── 各区域最大地址 ──

        /// <summary>R 区域最大地址 (内部继电器)。</summary>
        public const int MaxR = 9999;

        /// <summary>X 区域最大地址 (输入)。</summary>
        public const int MaxX = 999;

        /// <summary>Y 区域最大地址 (输出)。</summary>
        public const int MaxY = 999;

        /// <summary>M 区域最大地址 (辅助继电器)。</summary>
        public const int MaxM = 9999;

        /// <summary>D 区域最大地址 (数据寄存器)。</summary>
        public const int MaxD = 3899;

        /// <summary>T 区域最大地址 (定时器)。</summary>
        public const int MaxT = 255;

        /// <summary>C 区域最大地址 (计数器)。</summary>
        public const int MaxC = 255;
    }

    /// <summary>Fatek 错误码。</summary>
    public static class FatekErrorCodes
    {
        /// <summary>正常完成。</summary>
        public const string Success = "0";

        /// <summary>获取 Fatek 错误码的中文描述。</summary>
        public static string GetDescription(string code)
        {
            switch (code)
            {
                case "0": return "正常完成";
                case "1": return "地址错误 — 不支持的区域或超出范围";
                case "2": return "数据错误 — 数据格式或数值无效";
                case "3": return "命令错误 — 不支持的功能码";
                case "4": return "校验错误 — LRC 校验失败";
                case "5": return "通信错误 — 超时或连接中断";
                case "6": return "写入禁止 — PLC 处于运行模式";
                case "7": return "PLC 繁忙 — 请稍后重试";
                case "8": return "站号错误 — 从站编号超出范围";
                default: return $"未知错误 ({code})";
            }
        }
    }
}
