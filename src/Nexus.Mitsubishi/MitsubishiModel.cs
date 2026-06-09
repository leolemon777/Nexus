using System;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 PLC 型号枚举 — 决定 MC 协议帧格式和默认参数。
    /// </summary>
    public enum MitsubishiModel
    {
        /// <summary>Q 系列 (QnA) 3E 帧 — 最常用</summary>
        Qna_3E = 0,
        /// <summary>Q 系列 2E 帧</summary>
        Qna_2E = 1,
        /// <summary>A 系列 3E 帧</summary>
        A_3E = 2,
        /// <summary>A 系列 1E 帧</summary>
        A_1E = 3,
        /// <summary>FX3U 系列</summary>
        FX_3U = 4,
        /// <summary>FX5U 系列</summary>
        FX_5U = 5,
        /// <summary>iQ-R 系列</summary>
        IQ_R = 6,
        /// <summary>iQ-F 系列</summary>
        IQ_F = 7,
        /// <summary>L 系列</summary>
        L_Series = 8,
    }

    /// <summary>MC 协议帧类型。</summary>
    public enum McFrameType
    {
        /// <summary>MC 3E Binary 帧。</summary>
        MC3E_Binary = 0,
        /// <summary>MC 3E ASCII 帧。</summary>
        MC3E_Ascii = 1,
        /// <summary>MC 4E 帧 (SLMP)。</summary>
        MC4E = 2,
        /// <summary>A-1E 兼容帧。</summary>
        A1E = 3,
    }

    /// <summary>SLMP 命令码。</summary>
    public static class SlmpCommands
    {
        /// <summary>批量读取位设备。</summary>
        public const ushort BatchReadBit = 0x0401;
        /// <summary>批量读取字设备。</summary>
        public const ushort BatchReadWord = 0x0402;
        /// <summary>批量写入位设备。</summary>
        public const ushort BatchWriteBit = 0x1401;
        /// <summary>批量写入字设备。</summary>
        public const ushort BatchWriteWord = 0x1402;
        /// <summary>随机读取位设备。</summary>
        public const ushort RandomReadBit = 0x0403;
        /// <summary>随机读取字设备。</summary>
        public const ushort RandomReadWord = 0x0404;
        /// <summary>随机写入位设备。</summary>
        public const ushort RandomWriteBit = 0x1403;
        /// <summary>随机写入字设备。</summary>
        public const ushort RandomWriteWord = 0x1404;
        /// <summary>多长度随机读取。</summary>
        public const ushort RandomReadMultiLength = 0x0406;
        /// <summary>多长度随机写入。</summary>
        public const ushort RandomWriteMultiLength = 0x1406;
        /// <summary>PLC 运行。</summary>
        public const ushort Run = 0x1001;
        /// <summary>PLC 停止。</summary>
        public const ushort Stop = 0x1002;
        /// <summary>读取 PLC 型号。</summary>
        public const ushort ReadType = 0x0101;
        /// <summary>读取 PLC 状态。</summary>
        public const ushort ReadStatus = 0x0102;
    }

    /// <summary>MC 协议常量。</summary>
    public static class McConstants
    {
        /// <summary>默认 MC 协议 TCP 端口。</summary>
        public const int DefaultTcpPort = 6000;

        /// <summary>默认 MC 协议 UDP 端口。</summary>
        public const int DefaultUdpPort = 5551;

        /// <summary>FX5U 默认端口。</summary>
        public const int Fx5uDefaultPort = 6000;

        /// <summary>MC 3E 帧头长度。</summary>
        public const int Mc3EHeaderLength = 11;

        /// <summary>MC 3E Binary 子帧头。</summary>
        public const ushort SubHeader = 0x5000;

        /// <summary>最大批量读取点数（位）。</summary>
        public const int MaxBatchReadBits = 7168;

        /// <summary>最大批量读取点数（字）。</summary>
        public const int MaxBatchReadWords = 960;

        /// <summary>最大批量写入点数（位）。</summary>
        public const int MaxBatchWriteBits = 7168;

        /// <summary>最大批量写入点数（字）。</summary>
        public const int MaxBatchWriteWords = 960;

        /// <summary>最大随机读取地址数。</summary>
        public const int MaxRandomReadAddresses = 192;

        /// <summary>FX3U 最大数据寄存器地址。</summary>
        public const int Fx3uMaxD = 7999;

        /// <summary>FX5U 最大数据寄存器地址。</summary>
        public const int Fx5uMaxD = 32767;
    }

    /// <summary>SLMP 完成码。</summary>
    public static class SlmpErrorCodes
    {
        /// <summary>获取 SLMP 完成码的中文描述。</summary>
        public static string GetDescription(ushort endCode)
        {
            if (endCode == 0x0000) return "正常完成";

            switch (endCode)
            {
                case 0xC001: return "不支持的功能码";
                case 0xC002: return "不支持的数据区域";
                case 0xC003: return "地址超出范围";
                case 0xC004: return "数据长度超出范围";
                case 0xC005: return "写入数据错误";
                case 0xC006: return "PLC 当前模式不支持此操作";
                case 0xC007: return "远程密码错误";
                case 0xC008: return "远程密码未设置";
                case 0xC009: return "远程密码锁定中";
                case 0xC00A: return "模块访问错误";
                case 0xC010: return "通信端口已被占用";
                case 0xC011: return "不支持的数据长度";
                case 0xC012: return "不支持的子功能码";
                case 0xC019: return "C24 连接参数错误";
                case 0xC020: return "帧长度错误";
                case 0xC021: return "帧格式错误";
                case 0xC022: return "无可用通信缓冲区";
                case 0xC023: return "通信超时";
                case 0xC024: return "路由参数错误";
                case 0xC025: return "路由连接失败";
                case 0xC026: return "路由无响应";
                case 0xC030: return "多路径错误";
                case 0xC050: return "标签操作错误";
                case 0xC051: return "标签未找到";
                case 0xC052: return "标签类型不匹配";
                case 0xC060: return "安全认证失败";
                case 0xCF70: return "从站无响应";
                case 0xCF71: return "从站硬件错误";
                case 0xCF72: return "从站忙（运行模式）";
                case 0xCF73: return "从站不支持该操作";
                default: return $"未知错误 (0x{endCode:X4})";
            }
        }
    }
}
