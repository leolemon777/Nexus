using System;

namespace Nexus.Yaskawa
{
    /// <summary>Memobus 主功能码（MFC）。</summary>
    public enum MemobusMfc : byte
    {
        /// <summary>标准 Memobus 功能。</summary>
        Standard = 0x20,
        /// <summary>命名区域功能。</summary>
        Named = 0x43,
    }

    /// <summary>Memobus 子功能码（SFC）。</summary>
    public enum MemobusSfc : byte
    {
        /// <summary>读线圈。</summary>
        ReadCoil = 1,
        /// <summary>读离散输入。</summary>
        ReadDiscreteInput = 2,
        /// <summary>读保持寄存器。</summary>
        ReadHoldingRegister = 3,
        /// <summary>读输入寄存器。</summary>
        ReadInputRegister = 4,
        /// <summary>写单线圈。</summary>
        WriteSingleCoil = 5,
        /// <summary>写单个寄存器。</summary>
        WriteSingleRegister = 6,
        /// <summary>写多个寄存器。</summary>
        WriteMultipleRegisters = 0x10,
        /// <summary>扩展读保持寄存器。</summary>
        ExtendedRead = 9,
        /// <summary>扩展读输入寄存器。</summary>
        ExtendedReadInput = 10,
        /// <summary>扩展写保持寄存器。</summary>
        ExtendedWrite = 0x0B,
        /// <summary>随机读寄存器。</summary>
        ReadRandom = 0x0D,
        /// <summary>命名区域位读取。</summary>
        NamedReadBit = 0x41,
        /// <summary>命名区域位写入。</summary>
        NamedWriteBit = 0x42,
        /// <summary>命名区域字读取。</summary>
        NamedReadWord = 0x49,
        /// <summary>命名区域字写入。</summary>
        NamedWriteWord = 0x4B,
    }

    /// <summary>YASKAWA 常量。</summary>
    public static class MemobusConstants
    {
        /// <summary>默认 Memobus TCP 端口（与 Modbus 相同）。</summary>
        public const int DefaultPort = 502;

        /// <summary>外层帧头固定长度。</summary>
        public const int OuterHeaderLength = 12;

        /// <summary>外层帧头标记字节。</summary>
        public const byte OuterHeaderMarker = 0x11;

        /// <summary>内层帧头长度。</summary>
        public const int InnerHeaderLength = 6;

        /// <summary>最大单次读取寄存器数量。</summary>
        public const int MaxReadRegisters = 125;

        /// <summary>最大单次写入寄存器数量。</summary>
        public const int MaxWriteRegisters = 100;

        /// <summary>最大单次读取线圈数量。</summary>
        public const int MaxReadCoils = 2000;

        /// <summary>默认 CPU To 编号。</summary>
        public const byte DefaultCpuTo = 2;

        /// <summary>默认 CPU From 编号。</summary>
        public const byte DefaultCpuFrom = 1;

        /// <summary>SRTP 最大站号。</summary>
        public const int MaxStation = 254;
    }

    /// <summary>Memobus 错误码。</summary>
    public static class MemobusErrorCodes
    {
        /// <summary>非法功能码。</summary>
        public const byte IllegalFunction = 0x01;
        /// <summary>非法数据地址。</summary>
        public const byte IllegalDataAddress = 0x02;
        /// <summary>非法数据值。</summary>
        public const byte IllegalDataValue = 0x03;
        /// <summary>从站设备故障。</summary>
        public const byte SlaveDeviceFailure = 0x40;
        /// <summary>CPU 异常。</summary>
        public const byte CpuError = 0x41;
        /// <summary>无法执行。</summary>
        public const byte CannotExecute = 0x42;

        /// <summary>获取错误码的中文描述。</summary>
        public static string GetDescription(byte errorCode)
        {
            switch (errorCode)
            {
                case 0x00: return "正常完成";
                case IllegalFunction: return "非法功能码";
                case IllegalDataAddress: return "非法数据地址";
                case IllegalDataValue: return "非法数据值";
                case SlaveDeviceFailure: return "从站设备故障";
                case CpuError: return "CPU 异常";
                case CannotExecute: return "无法执行";
                default: return $"未知错误 ({errorCode:X2})";
            }
        }
    }
}
