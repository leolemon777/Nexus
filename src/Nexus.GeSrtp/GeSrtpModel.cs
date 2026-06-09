using System;

namespace Nexus.GeSrtp
{
    /// <summary>GE SRTP 常量。</summary>
    public static class GeSrtpConstants
    {
        /// <summary>默认 SRTP TCP 端口。</summary>
        public const int DefaultPort = 18245;

        /// <summary>SRTP 帧头长度（8 字节）。</summary>
        public const int FrameHeaderLength = 8;

        /// <summary>最大单次读取寄存器数量。</summary>
        public const int MaxReadRegisters = 128;

        /// <summary>最大单次写入寄存器数量。</summary>
        public const int MaxWriteRegisters = 128;

        /// <summary>最大单次读取离散量数量。</summary>
        public const int MaxReadDiscrete = 2048;

        /// <summary>SRTP ServiceType：读请求。</summary>
        public const byte ServiceTypeRead = 0x01;

        /// <summary>SRTP ServiceType：写请求。</summary>
        public const byte ServiceTypeWrite = 0x02;

        /// <summary>SRTP 子命令：读取日期时间。</summary>
        public const byte SubCmdReadDateTime = 37;

        /// <summary>SRTP 子命令：读取程序名。</summary>
        public const byte SubCmdReadProgramName = 1;

        // ── 各区域最大地址 ──

        /// <summary>%R 最大地址。</summary>
        public const int MaxRegister = 32767;

        /// <summary>%AI 最大地址。</summary>
        public const int MaxAnalogInput = 32767;

        /// <summary>%AQ 最大地址。</summary>
        public const int MaxAnalogOutput = 32767;

        /// <summary>%I 最大地址。</summary>
        public const int MaxDiscreteInput = 32767;

        /// <summary>%Q 最大地址。</summary>
        public const int MaxDiscreteOutput = 32767;

        /// <summary>%M 最大地址。</summary>
        public const int MaxSystemMemory = 32767;

        /// <summary>%T 最大地址。</summary>
        public const int MaxTimer = 32767;
    }

    /// <summary>GE SRTP 错误码。</summary>
    public static class GeSrtpErrorCodes
    {
        /// <summary>成功。</summary>
        public const byte Success = 0x00;

        /// <summary>获取错误码的中文描述。</summary>
        public static string GetDescription(byte errorCode)
        {
            switch (errorCode)
            {
                case 0x00: return "正常完成";
                case 0x01: return "无效的服务类型";
                case 0x02: return "无效的内存类型";
                case 0x03: return "无效的偏移地址";
                case 0x04: return "无效的数据长度";
                case 0x05: return "PLC 处于保护模式 — 拒绝写入";
                case 0x06: return "通信超时";
                case 0x07: return "PLC 繁忙 — 请稍后重试";
                case 0x08: return "连接被拒绝";
                case 0x09: return "校验错误";
                case 0x0A: return "功能未实现";
                case 0x0B: return "系统错误 — 内部故障";
                case 0x0C: return "PLC 处于 STOP 模式";
                case 0x0D: return "地址越界 — 超出最大范围";
                default: return $"未知错误 ({errorCode:X2})";
            }
        }
    }
}
