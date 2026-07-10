namespace Nexus.Phoenix
{
    /// <summary>Phoenix Contact AXC PLC 内存区域（IEC 61131-3）。</summary>
    /// <remarks>
    /// Phoenix Contact AXC 系列 PLC（AXC F 2152 / AXC F 3152 等，运行 PLCnext Technology）
    /// 原生作为 Modbus TCP Server（PLCnext 工程中配置 Modbus 服务并映射变量）。
    /// 区域映射为标准 Modbus：
    /// <c>%MW</c> 映射到 Holding Register，<c>%IW</c> 映射到 Input Register，
    /// <c>%QX/%M</c> 映射到 Coil，<c>%IX</c> 映射到 Discrete Input。
    /// </remarks>
    public enum PhoenixArea
    {
        /// <summary>保持寄存器 %MW（FC03 读 / FC16 写）。</summary>
        MemoryWord,

        /// <summary>输入寄存器 %IW（FC04 读，只读）。</summary>
        InputWord,

        /// <summary>线圈 %QX / %M（FC01 读 / FC05 写）。</summary>
        Coil,

        /// <summary>离散输入 %IX / %I（FC02 读，只读）。</summary>
        InputBit,
    }
}
