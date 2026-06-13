namespace Nexus.LsElectric
{
    /// <summary>LS Electric Cnet 区域枚举。</summary>
    public enum LSCnetArea
    {
        /// <summary>程序存储器 (P)。</summary>
        Program,
        /// <summary>内部继电器 (M)。</summary>
        InternalRelay,
        /// <summary>保持继电器 (K)。</summary>
        KeepRelay,
        /// <summary>定时器 (T)。</summary>
        Timer,
        /// <summary>计数器 (C)。</summary>
        Counter,
        /// <summary>数据寄存器 (D)。</summary>
        DataRegister,
        /// <summary>链接寄存器 (L)。</summary>
        LinkRegister,
        /// <summary>文件寄存器 (N)。</summary>
        FileRegister,
    }

    /// <summary>Cnet 帧常量。</summary>
    public static class LSCnetConstants
    {
        /// <summary>ENQ 控制字符。</summary>
        public const byte ENQ = 0x05;
        /// <summary>STX 控制字符。</summary>
        public const byte STX = 0x02;
        /// <summary>ETX 控制字符。</summary>
        public const byte ETX = 0x03;
        /// <summary>ACK 控制字符。</summary>
        public const byte ACK = 0x06;
        /// <summary>NAK 控制字符。</summary>
        public const byte NAK = 0x15;

        // 命令码 (ASCII)
        /// <summary>读命令 'R' + 'D' = 0x52 0x44。</summary>
        public const string CmdRead = "RD";
        /// <summary>写命令 'W' + 'R' = 0x57 0x52。</summary>
        public const string CmdWrite = "WR";

        // 区域代码
        public const byte AreaProgram = 0x50;      // 'P'
        public const byte AreaInternalRelay = 0x4D; // 'M'
        public const byte AreaKeepRelay = 0x4B;     // 'K'
        public const byte AreaTimer = 0x54;         // 'T'
        public const byte AreaCounter = 0x43;       // 'C'
        public const byte AreaDataRegister = 0x44;  // 'D'
        public const byte AreaLinkRegister = 0x4C;  // 'L'
        public const byte AreaFileRegister = 0x4E;  // 'N'

        /// <summary>默认 Cnet 端口（TCP 模式）。</summary>
        public const int DefaultPort = 2004;
    }
}
