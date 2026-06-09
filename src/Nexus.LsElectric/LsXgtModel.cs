namespace Nexus.LsElectric
{
    /// <summary>LS XGT 区域枚举。</summary>
    public enum LsXgtArea
    {
        /// <summary>I/O 区域 (P)。</summary>
        IO,
        /// <summary>内部继电器 (M)。</summary>
        InternalRelay,
        /// <summary>链接继电器 (L)。</summary>
        LinkRelay,
        /// <summary>保持继电器 (K)。</summary>
        KeepRelay,
        /// <summary>特殊继电器 (F)。</summary>
        SpecialRelay,
        /// <summary>定时器 (T)。</summary>
        Timer,
        /// <summary>计数器 (C)。</summary>
        Counter,
        /// <summary>数据寄存器 (D)。</summary>
        DataRegister,
        /// <summary>文件寄存器 (N)。</summary>
        FileRegister,
    }

    /// <summary>LS 产电 PLC 型号。</summary>
    public enum LsXgtModel
    {
        /// <summary>未知型号。</summary>
        Unknown,
        /// <summary>XGB 系列。</summary>
        Xgb,
        /// <summary>XBC 系列。</summary>
        Xbc,
        /// <summary>XECS 系列。</summary>
        Xecs,
        /// <summary>XGR 系列。</summary>
        Xgr,
        /// <summary>XEC 系列。</summary>
        Xec,
        /// <summary>XBF 系列。</summary>
        Xbf,
        /// <summary>XBM 系列。</summary>
        Xbm,
        /// <summary>XBC-D32H。</summary>
        XbcD32H,
        /// <summary>XBC-D64H。</summary>
        XbcD64H,
    }

    /// <summary>XGT 帧常量。</summary>
    public static class LsXgtConstants
    {
        /// <summary>ENQ 控制字符。</summary>
        public const byte ENQ = 0x05;
        /// <summary>EOT 控制字符。</summary>
        public const byte EOT = 0x04;
        /// <summary>ACK 控制字符。</summary>
        public const byte ACK = 0x06;
        /// <summary>NAK 控制字符。</summary>
        public const byte NAK = 0x15;

        // 命令码
        public const byte CmdRead = 0x54;
        public const byte CmdWrite = 0x58;
        public const byte CmdRequest = 0x52;
        public const byte CmdControl = 0x63;

        // 数据类型
        public const byte TypeBit = 0x00;
        public const byte TypeByte = 0x01;
        public const byte TypeWord = 0x02;
        public const byte TypeDWord = 0x03;
        public const byte TypeLWord = 0x04;

        // 块类型
        public const ushort BlockContinuous = 0x0000;
        public const ushort BlockRandom = 0x0001;

        /// <summary>帧头固定长度: ENQ(1) + Company(10) + CPUInfo(10) + PLCInfo(6) + Cmd(1) + DataType(1) + Reserve(2) + BlockInfo(2) = 33。</summary>
        public const int FrameHeaderLength = 33;

        /// <summary>默认 XGT 端口。</summary>
        public const int DefaultPort = 2004;

        /// <summary>公司标识字符串。</summary>
        public const string CompanyId = "LSIS-XGT";
    }

    /// <summary>XGT PLC 运行状态。</summary>
    public enum LsXgtPlcStatus
    {
        /// <summary>停止。</summary>
        Stop = 0x00,
        /// <summary>运行。</summary>
        Run = 0x01,
        /// <summary>调试。</summary>
        Debug = 0x02,
        /// <summary>错误。</summary>
        Error = 0x03,
    }

    /// <summary>XGT 控制命令。</summary>
    public enum LsXgtControlMode
    {
        /// <summary>停止 PLC。</summary>
        Stop = 0x00,
        /// <summary>运行 PLC。</summary>
        Run = 0x01,
        /// <summary>重启 PLC。</summary>
        Restart = 0x02,
    }
}
