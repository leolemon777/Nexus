namespace Nexus.AbbPlc
{
    /// <summary>ABB AC500 PLC 内存区域（IEC 61131-3）。</summary>
    /// <remarks>
    /// AC500 系列（PM571/PM58x/PM59x）原生支持 Modbus TCP Server。
    /// 地址映射为标准 Modbus（无 WAGO 那样的 0x3000 统一偏移）：
    /// <c>%MW</c> 直接映射到 Holding Register，<c>%IW</c> 到 Input Register。
    /// </remarks>
    public enum AbbArea
    {
        /// <summary>保持寄存器 %MW（FC03 读 / FC16 写）。</summary>
        MemoryWord,
        /// <summary>输入寄存器 %IW（FC04 读，只读）。</summary>
        InputWord,
        /// <summary>线圈 %M / %QX（FC01 读 / FC05 写）。</summary>
        Coil,
        /// <summary>离散输入 %IX / %I（FC02 读，只读）。</summary>
        InputBit,
    }
}
