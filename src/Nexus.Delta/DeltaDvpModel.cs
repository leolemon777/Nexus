namespace Nexus.Delta
{
    /// <summary>台达 PLC 区域枚举。</summary>
    public enum DeltaArea
    {
        /// <summary>输出线圈 Y。</summary>
        OutputCoil,
        /// <summary>输入离散量 X（只读）。</summary>
        InputDiscrete,
        /// <summary>内部继电器 M。</summary>
        InternalRelay,
        /// <summary>定时器线圈 T。</summary>
        TimerCoil,
        /// <summary>计数器线圈 C。</summary>
        CounterCoil,
        /// <summary>步进继电器 S。</summary>
        StepRelay,
        /// <summary>数据寄存器 D。</summary>
        DataRegister,
    }

    /// <summary>台达 DVP 系列 PLC 型号。</summary>
    public enum DeltaDvpModel
    {
        /// <summary>未知型号。</summary>
        Unknown,
        /// <summary>DVP-SV 系列。</summary>
        DvpSv,
        /// <summary>DVP-SV2 系列。</summary>
        DvpSv2,
        /// <summary>DVP-SA 系列。</summary>
        DvpSa,
        /// <summary>DVP-SS 系列。</summary>
        DvpSs,
        /// <summary>DVP-ES2 系列。</summary>
        DvpEs2,
        /// <summary>DVP-ES 系列。</summary>
        DvpEs,
        /// <summary>DVP-EH3 系列。</summary>
        DvpEh3,
        /// <summary>DVP-EH2 系列。</summary>
        DvpEh2,
        /// <summary>AS300 系列。</summary>
        As300,
        /// <summary>ASDA 伺服驱动器。</summary>
        AsdaA2,
        /// <summary>ASDA-A3 伺服驱动器。</summary>
        AsdaA3,
        /// <summary>DVP-PD01 通讯模块。</summary>
        DvpPd01,
    }

    /// <summary>台达 DVP Modbus 地址映射常量。</summary>
    public static class DeltaDvpConstants
    {
        /// <summary>Y 区域基地址。</summary>
        public const ushort Y_Base = 0x0000;
        /// <summary>X 区域基地址。</summary>
        public const ushort X_Base = 0x0000;
        /// <summary>M 区域基地址。</summary>
        public const ushort M_Base = 0x0800;
        /// <summary>T 线圈区域基地址。</summary>
        public const ushort T_CoilBase = 0x0C00;
        /// <summary>C 线圈区域基地址。</summary>
        public const ushort C_CoilBase = 0x1000;
        /// <summary>S 步进继电器基地址。</summary>
        public const ushort S_Base = 0x1000;
        /// <summary>D 数据寄存器基地址。</summary>
        public const ushort D_Base = 0x1000;

        /// <summary>T 定时器当前值寄存器基地址。</summary>
        public const ushort T_RegisterBase = 0x1800;
        /// <summary>C 计数器当前值寄存器基地址。</summary>
        public const ushort C_RegisterBase = 0x1C00;

        /// <summary>批量位操作每包最大位数。</summary>
        public const int MaxBitsPerRequest = 1968;
        /// <summary>批量寄存器读取每包最大数量。</summary>
        public const int MaxRegistersRead = 125;
        /// <summary>批量寄存器写入每包最大数量。</summary>
        public const int MaxRegistersWrite = 123;

        /// <summary>PLC 型号寄存器地址（D1121）。</summary>
        public const ushort PlcModelAddress = 0x1461;
        /// <summary>PLC 型号寄存器数量。</summary>
        public const ushort PlcModelRegisterCount = 10;

        /// <summary>台达 DVP 型号识别字符串。</summary>
        public static readonly string[] KnownModels = new[]
        {
            "DVP-SV", "DVP-SV2", "DVP-SA", "DVP-SS", "DVP-ES2",
            "DVP-ES", "DVP-EH3", "DVP-EH2", "AS300", "ASDA-A2", "ASDA-A3"
        };
    }

    /// <summary>台达 Modbus 异常码。</summary>
    public static class DeltaErrorCodes
    {
        /// <summary>非法功能码。</summary>
        public const byte IllegalFunction = 0x01;
        /// <summary>非法数据地址。</summary>
        public const byte IllegalDataAddress = 0x02;
        /// <summary>非法数据值。</summary>
        public const byte IllegalDataValue = 0x03;
        /// <summary>从站故障。</summary>
        public const byte SlaveDeviceFailure = 0x04;
        /// <summary>确认，从站忙。</summary>
        public const byte Acknowledge = 0x05;
        /// <summary>从站忙，拒绝。</summary>
        public const byte SlaveDeviceBusy = 0x06;

        /// <summary>将异常码转换为中文描述。</summary>
        public static string ToDescription(byte errorCode) => errorCode switch
        {
            0x01 => "非法功能码",
            0x02 => "非法数据地址 — 台达地址可能超出该型号支持范围",
            0x03 => "非法数据值",
            0x04 => "从站设备故障",
            0x05 => "从站确认（忙）",
            0x06 => "从站忙，拒绝请求",
            0x08 => "存储奇偶校验错误",
            0x0A => "网关路径不可用",
            _ => $"未知异常码 0x{errorCode:X2}"
        };
    }
}
