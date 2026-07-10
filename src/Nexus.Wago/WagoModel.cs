namespace Nexus.Wago
{
    /// <summary>WAGO PLC 内存区域（IEC 61131-3）。</summary>
    /// <remarks>
    /// WAGO 750 以太网耦合器与 PFC200 控制器原生作为 Modbus TCP Server。
    /// 地址映射基于 WAGO 750 耦合器手册 §4.5.6：所有区域统一从 0x3000 (12288) 起始。
    /// </remarks>
    public enum WagoArea
    {
        /// <summary>保持寄存器 %MW（FC03 读 / FC16 写）。</summary>
        MemoryWord,
        /// <summary>输入寄存器 %IW（FC04 读，只读）。</summary>
        InputWord,
        /// <summary>离散输入 %IX/%IB（FC02 读，只读）。</summary>
        InputBit,
        /// <summary>线圈 %QX/%QB / %M（FC01 读 / FC05 写）。</summary>
        Coil,
    }

    /// <summary>地址解析偏移约定。</summary>
    /// <remarks>
    /// WAGO 750 实测存在两种偏移约定：
    /// <list type="bullet">
    /// <item><description><c>ZeroBased</c>：%MW0 → 寄存器 12288 (0x3000)。官方手册默认。</description></item>
    /// <item><description><c>OneBased</c>：%MW0 → 寄存器 12289 (0x3001)。部分现场实测/旧固件。</description></item>
    /// </list>
    /// </remarks>
    public enum WagoOffsetMode
    {
        /// <summary>%MWn → 0x3000 + n（手册默认）。</summary>
        ZeroBased,
        /// <summary>%MWn → 0x3001 + n（部分实测约定）。</summary>
        OneBased,
    }
}
