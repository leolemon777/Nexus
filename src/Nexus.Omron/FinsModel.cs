using System;

namespace Nexus.Omron
{
    /// <summary>
    /// FINS 内存区域代码。
    /// </summary>
    public enum FinsMemoryArea : byte
    {
        /// <summary>Core I/O 区域。</summary>
        CIO = 0xB0,

        /// <summary>工作继电器区域。</summary>
        WR = 0xB1,

        /// <summary>保持继电器区域。</summary>
        HR = 0xB2,

        /// <summary>辅助继电器区域。</summary>
        AR = 0xB3,

        /// <summary>数据存储区域（16 位字）。</summary>
        DM = 0x82,

        /// <summary>扩展数据存储区域（分 bank）。</summary>
        EM = 0x98,

        /// <summary>定时器当前值。</summary>
        TimerPV = 0x91,

        /// <summary>定时器标志。</summary>
        TimerFlags = 0x92,

        /// <summary>计数器当前值。</summary>
        CounterPV = 0xA1,

        /// <summary>计数器标志。</summary>
        CounterFlags = 0xA2,
    }

    /// <summary>欧姆龙 PLC 型号。</summary>
    public enum OmronModel
    {
        /// <summary>CJ2M。</summary>
        CJ2M,
        /// <summary>CJ2H。</summary>
        CJ2H,
        /// <summary>CP1H。</summary>
        CP1H,
        /// <summary>CP1L。</summary>
        CP1L,
        /// <summary>CP1E。</summary>
        CP1E,
        /// <summary>NJ501 (NJ 系列)。</summary>
        NJ501,
        /// <summary>NJ101 (NJ 系列)。</summary>
        NJ101,
        /// <summary>NX1P2 (NX 系列)。</summary>
        NX1P2,
        /// <summary>NX102 (NX 系列)。</summary>
        NX102,
        /// <summary>NX202 (NX 系列)。</summary>
        NX202,
        /// <summary>CS1G。</summary>
        CS1G,
        /// <summary>CS1H。</summary>
        CS1H,
        /// <summary>CQ2MH。</summary>
        CQ2MH,
    }

    /// <summary>
    /// FINS 命令码。
    /// </summary>
    internal static class FinsCommandCode
    {
        /// <summary>内存区域读取。</summary>
        public const ushort MemoryAreaRead = 0x0101;

        /// <summary>内存区域写入。</summary>
        public const ushort MemoryAreaWrite = 0x0102;

        /// <summary>内存区域填充。</summary>
        public const ushort MemoryAreaFill = 0x0103;

        /// <summary>控制器数据读取。</summary>
        public const ushort ControllerRead = 0x0501;

        /// <summary>控制器状态读取。</summary>
        public const ushort ControllerStatusRead = 0x0601;

        /// <summary>时间读取。</summary>
        public const ushort TimeRead = 0x0701;

        /// <summary>时间写入。</summary>
        public const ushort TimeWrite = 0x0702;

        /// <summary>远程运行。</summary>
        public const ushort Run = 0x0401;

        /// <summary>远程停止。</summary>
        public const ushort Stop = 0x0402;

        /// <summary>连接数据发送（循环）。</summary>
        public const ushort CycleSend = 0x0201;

        /// <summary>多地址读取 (NX/NJ)。</summary>
        public const ushort MultiRead = 0x0401;

        /// <summary>多地址写入 (NX/NJ)。</summary>
        public const ushort MultiWrite = 0x1401;
    }

    /// <summary>FINS 常量。</summary>
    public static class FinsConstants
    {
        /// <summary>默认 FINS TCP 端口。</summary>
        public const int DefaultTcpPort = 9600;

        /// <summary>默认 FINS UDP 端口。</summary>
        public const int DefaultUdpPort = 9600;

        /// <summary>FINS 帧头长度（10 字节）。</summary>
        public const int FinsHeaderLength = 10;

        /// <summary>FINS TCP 握手头长度。</summary>
        public const int TcpHandshakeHeaderLength = 12;

        /// <summary>最大单次读取字数。</summary>
        public const int MaxReadWords = 500;

        /// <summary>最大单次写入字数。</summary>
        public const int MaxWriteWords = 500;

        /// <summary>NX/NJ 标签访问最大名称长度。</summary>
        public const int MaxTagNameLength = 256;
    }

    /// <summary>
    /// FINS 结束码（响应状态）。
    /// </summary>
    internal static class FinsEndCode
    {
        public const ushort Success = 0x0000;
        public const ushort CommandNotSupported = 0x0001;
        public const ushort NotReady = 0x0002;
        public const ushort RoutingError = 0x0003;
        public const ushort ParameterError = 0x0201;
        public const ushort DataLengthError = 0x0202;
        public const ushort MemoryAreaError = 0x0301;
        public const ushort AddressRangeError = 0x0302;
        public const ushort AddressOverflow = 0x0303;
        public const ushort WriteProtected = 0x0304;
        public const ushort Aborted = 0x0401;

        /// <summary>将结束码转换为可读消息。</summary>
        public static string ToMessage(ushort endCode)
        {
            return endCode switch
            {
                0x0000 => "成功",
                0x0001 => "不支持的命令",
                0x0002 => "未就绪",
                0x0003 => "路由错误",
                0x0201 => "参数错误",
                0x0202 => "数据长度错误",
                0x0301 => "内存区域错误",
                0x0302 => "地址范围错误",
                0x0303 => "地址溢出",
                0x0304 => "写保护",
                0x0401 => "中止",
                _ => $"未知错误 (0x{endCode:X4})"
            };
        }
    }

    /// <summary>FINS 设备发现结果。</summary>
    public sealed class FinsDiscoveredDevice
    {
        /// <summary>设备网络地址。</summary>
        public byte NetworkAddress { get; set; }
        /// <summary>设备节点号。</summary>
        public byte NodeNumber { get; set; }
        /// <summary>设备单元号。</summary>
        public byte UnitNumber { get; set; }
        /// <summary>控制器型号。</summary>
        public string ControllerModel { get; set; } = string.Empty;
    }
}
