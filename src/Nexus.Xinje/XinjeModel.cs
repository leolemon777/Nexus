namespace Nexus.Xinje
{
    /// <summary>信捷 PLC 区域枚举。</summary>
    public enum XinjeArea
    {
        /// <summary>输出线圈 Y。</summary>
        OutputCoil,
        /// <summary>输入离散量 X（只读）。</summary>
        InputDiscrete,
        /// <summary>内部继电器 M。</summary>
        InternalRelay,
        /// <summary>步进继电器 S。</summary>
        StepRelay,
        /// <summary>特殊线圈 SM。</summary>
        SpecialCoil,
        /// <summary>定时器 T。</summary>
        Timer,
        /// <summary>计数器 C。</summary>
        Counter,
        /// <summary>数据寄存器 D。</summary>
        DataRegister,
        /// <summary>保持寄存器 HD。</summary>
        HoldingRegister,
        /// <summary>特殊寄存器 SD。</summary>
        SpecialRegister,
    }

    /// <summary>信捷 PLC 系列/型号。</summary>
    public enum XinjeModel
    {
        /// <summary>未知型号。</summary>
        Unknown,
        /// <summary>XC3 系列。</summary>
        Xc3,
        /// <summary>XC5 系列。</summary>
        Xc5,
        /// <summary>XC-X 系列。</summary>
        XcX,
        /// <summary>XG3 系列。</summary>
        Xg3,
        /// <summary>XG5 系列。</summary>
        Xg5,
        /// <summary>XL3 系列。</summary>
        Xl3,
        /// <summary>XE3 系列。</summary>
        Xe3,
    }

    /// <summary>信捷地址映射常量。</summary>
    public static class XinjeConstants
    {
        public const ushort D_Base = 0x0000;
        public const ushort HD_Base = 0x8000;
        public const ushort SD_Base = 0xC000;
        public const ushort M_Base = 0x0800;
        public const ushort Y_Base = 0x0000;
        public const ushort X_Base = 0x0000;
        public const ushort T_Base = 0x0600;
        public const ushort C_Base = 0x1000;
        public const ushort SM_Base = 0x1000;
        public const ushort S_Base = 0x0000;

        /// <summary>批量位操作每包最大位数。</summary>
        public const int MaxBitsPerRequest = 2000;
        /// <summary>批量寄存器读取每包最大数量。</summary>
        public const int MaxRegistersRead = 125;
        /// <summary>批量寄存器写入每包最大数量。</summary>
        public const int MaxRegistersWrite = 123;

        /// <summary>默认 Modbus TCP 端口。</summary>
        public const int DefaultPort = 502;
    }
}
