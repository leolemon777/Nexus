using System;

namespace Nexus.Fuji
{
    /// <summary>富士 S-BUS 常量。</summary>
    public static class FujiSphConstants
    {
        /// <summary>默认 TCP 端口。</summary>
        public const int DefaultTcpPort = 18245;

        /// <summary>默认串口波特率。</summary>
        public const int DefaultBaudRate = 9600;

        /// <summary>默认站号。</summary>
        public const byte DefaultStation = 1;

        /// <summary>STX 字符。</summary>
        public const byte STX = 0x02;

        /// <summary>ETX 字符。</summary>
        public const byte ETX = 0x03;

        /// <summary>ACK 字符。</summary>
        public const byte ACK = 0x06;

        /// <summary>NAK 字符。</summary>
        public const byte NAK = 0x15;

        // ── S-BUS 命令码（2 位十六进制） ──

        /// <summary>批量读取。</summary>
        public const string CmdBatchRead = "0A";

        /// <summary>批量写入。</summary>
        public const string CmdBatchWrite = "1A";

        /// <summary>随机读取。</summary>
        public const string CmdRandomRead = "0C";

        /// <summary>随机写入。</summary>
        public const string CmdRandomWrite = "1C";

        /// <summary>位写入。</summary>
        public const string CmdBitWrite = "1B";

        /// <summary>PLC 运行。</summary>
        public const string CmdRun = "20";

        /// <summary>PLC 停止。</summary>
        public const string CmdStop = "21";

        /// <summary>读取 PLC 型号。</summary>
        public const string CmdReadModel = "30";

        // ── 各区域最大地址 ──

        /// <summary>D 区域最大地址。</summary>
        public const int MaxDataRegister = 32767;

        /// <summary>M 区域最大地址。</summary>
        public const int MaxInternalRelay = 4095;

        /// <summary>X 区域最大地址。</summary>
        public const int MaxInput = 2047;

        /// <summary>Y 区域最大地址。</summary>
        public const int MaxOutput = 2047;

        /// <summary>T 区域最大地址。</summary>
        public const int MaxTimer = 511;

        /// <summary>C 区域最大地址。</summary>
        public const int MaxCounter = 511;
    }

    /// <summary>富士 S-BUS 错误码。</summary>
    public static class FujiErrorCodes
    {
        /// <summary>获取错误码的中文描述。</summary>
        public static string GetDescription(string code)
        {
            switch (code)
            {
                case "00": return "正常完成";
                case "01": return "命令错误 — 不支持的功能码";
                case "02": return "地址错误 — 超出范围或非法格式";
                case "03": return "数据错误 — 数值超出范围或格式无效";
                case "04": return "BCC 校验错误";
                case "05": return "通信超时";
                case "06": return "写入禁止 — PLC 处于运行模式";
                case "07": return "站号错误 — 从站编号超出范围";
                case "08": return "PLC 繁忙 — 请稍后重试";
                case "09": return "内存错误 — 内部存储器故障";
                case "0A": return "保护错误 — 程序保护中";
                case "FF": return "通用错误 — 未分类故障";
                default: return $"未知错误 ({code})";
            }
        }
    }
}
