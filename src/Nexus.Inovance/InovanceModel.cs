using System;

namespace Nexus.Inovance
{
    /// <summary>汇川 Easy 系列常量。</summary>
    public static class InovanceConstants
    {
        /// <summary>默认 TCP 端口。</summary>
        public const int DefaultTcpPort = 502;

        /// <summary>EasyNet 协议帧头长度（22 字节）。</summary>
        public const int FrameHeaderLength = 22;

        /// <summary>最大单次读取寄存器数量。</summary>
        public const int MaxReadRegisters = 64;

        /// <summary>最大单次写入寄存器数量。</summary>
        public const int MaxWriteRegisters = 64;

        /// <summary>最大单次读取/写入位数量。</summary>
        public const int MaxBits = 256;

        // ── 命令码 ──

        /// <summary>读取命令码。</summary>
        public const byte CmdRead = 0x01;

        /// <summary>写入命令码。</summary>
        public const byte CmdWrite = 0x02;

        /// <summary>错误标志位（response[8] == 0x0F 表示错误）。</summary>
        public const byte ErrorFlag = 0x0F;

        // ── 各区域最大地址 ──

        /// <summary>D 区域最大地址。</summary>
        public const int MaxDataRegister = 7999;

        /// <summary>M 区域最大地址。</summary>
        public const int MaxAuxiliaryRelay = 9999;

        /// <summary>X 区域最大地址（八进制）。</summary>
        public const int MaxInputOctal = 777;

        /// <summary>Y 区域最大地址（八进制）。</summary>
        public const int MaxOutputOctal = 777;

        /// <summary>S 区域最大地址。</summary>
        public const int MaxStepRelay = 999;

        /// <summary>B 区域最大地址。</summary>
        public const int MaxLinkRelay = 511;

        /// <summary>W 区域最大地址。</summary>
        public const int MaxLinkRegister = 511;

        /// <summary>R 区域最大地址。</summary>
        public const int MaxSystemRegister = 32767;
    }

    /// <summary>汇川 Easy 错误码。</summary>
    public static class InovanceErrorCodes
    {
        /// <summary>获取错误码的中文描述。</summary>
        public static string GetDescription(byte errorCode)
        {
            switch (errorCode)
            {
                case 0x00: return "正常完成";
                case 0x01: return "命令错误 — 不支持的功能码";
                case 0x02: return "地址错误 — 超出范围或非法格式";
                case 0x03: return "数据错误 — 数值超出范围或格式无效";
                case 0x04: return "通信超时";
                case 0x05: return "写入禁止 — PLC 处于运行模式";
                case 0x06: return "PLC 繁忙 — 请稍后重试";
                case 0x07: return "站号错误";
                case 0x08: return "连接被拒绝";
                case 0x0F: return "通用错误 — 服务端返回错误标志";
                case 0x10: return "保护错误 — 程序保护中";
                default: return $"未知错误 ({errorCode:X2})";
            }
        }
    }
}
