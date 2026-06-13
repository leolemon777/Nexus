namespace Nexus.MegMeet
{
    /// <summary>麦格米特 PLC 数据区域。</summary>
    public enum MegMeetArea
    {
        /// <summary>输入 (X) — 八进制地址，FC02 只读</summary>
        Input,
        /// <summary>输出 (Y) — 八进制地址，FC01 读 / FC05 写</summary>
        Output,
        /// <summary>内部继电器 (M)，FC01 读 / FC05 写</summary>
        InternalRelay,
        /// <summary>特殊继电器 (SM)，FC01 读 / FC05 写</summary>
        SpecialRelay,
        /// <summary>步进继电器 (S)，FC01 读 / FC05 写</summary>
        StepRelay,
        /// <summary>定时器触点 (T)，FC01 读 / FC05 写</summary>
        TimerContact,
        /// <summary>计数器触点 (C)，FC01 读 / FC05 写</summary>
        CounterContact,
        /// <summary>数据寄存器 (D)，FC03 读 / FC06 写</summary>
        DataRegister,
        /// <summary>特殊寄存器 (SD)，FC03 读 / FC06 写</summary>
        SpecialRegister,
        /// <summary>索引寄存器 (Z)，FC03 读 / FC06 写</summary>
        IndexRegister,
        /// <summary>文件寄存器 (R)，FC03 读 / FC06 写</summary>
        FileRegister,
        /// <summary>定时器当前值 (T 字)，FC03 读 / FC06 写</summary>
        TimerValue,
        /// <summary>计数器当前值 (C 字)，FC03 读 / FC06 写</summary>
        CounterValue,
    }
}
