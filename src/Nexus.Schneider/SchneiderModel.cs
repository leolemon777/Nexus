using System;

namespace Nexus.Schneider
{
    /// <summary>施耐德 Modicon PLC 型号。</summary>
    public enum SchneiderModel
    {
        /// <summary>M580 (ePAC)。</summary>
        M580,
        /// <summary>M340。</summary>
        M340,
        /// <summary>M221。</summary>
        M221,
        /// <summary>M241。</summary>
        M241,
        /// <summary>M251。</summary>
        M251,
        /// <summary>M262。</summary>
        M262,
        /// <summary>M3x0 系列。</summary>
        M380,
        /// <summary>Premium (TSX)。</summary>
        Premium,
        /// <summary>Quantum。</summary>
        Quantum,
    }

    /// <summary>施耐德 Modicon 地址区域类型。</summary>
    public enum SchneiderArea
    {
        /// <summary>内部位 (%M)。</summary>
        InternalBit,
        /// <summary>内部字 (%MW)。</summary>
        InternalWord,
        /// <summary>输入位 (%I)。</summary>
        InputBit,
        /// <summary>输入字 (%IW)。</summary>
        InputWord,
        /// <summary>输出位 (%Q)。</summary>
        OutputBit,
        /// <summary>输出字 (%QW)。</summary>
        OutputWord,
        /// <summary>系统位 (%S)。</summary>
        SystemBit,
        /// <summary>系统字 (%SW)。</summary>
        SystemWord,
        /// <summary>常量字 (%KW)。</summary>
        ConstantWord,
    }

    /// <summary>施耐德 Modicon 常量。</summary>
    public static class SchneiderConstants
    {
        /// <summary>默认 Modbus TCP 端口。</summary>
        public const int DefaultPort = 502;

        /// <summary>OFs 读取功能码（扩展保持寄存器读取）。</summary>
        public const byte FcReadOfs = 0x68;

        /// <summary>OFs 写入功能码（扩展保持寄存器写入）。</summary>
        public const byte FcWriteOfs = 0x69;

        /// <summary>UNA 读取功能码。</summary>
        public const byte FcReadUna = 0x6A;

        /// <summary>UNA 写入功能码。</summary>
        public const byte FcWriteUna = 0x6B;

        // ── Modicon 地址区域映射到 Modbus FC ──

        /// <summary>%MW 地址映射到标准 Modbus FC03。</summary>
        public const byte Fc03ReadHolding = 0x03;

        /// <summary>%M 地址映射到标准 Modbus FC01。</summary>
        public const byte Fc01ReadCoil = 0x01;

        /// <summary>%I 地址映射到标准 Modbus FC02。</summary>
        public const byte Fc02ReadDiscrete = 0x02;

        /// <summary>%IW 地址映射到标准 Modbus FC04。</summary>
        public const byte Fc04ReadInput = 0x04;

        /// <summary>%Q 地址映射到标准 Modbus FC01。</summary>
        public const byte Fc05WriteCoil = 0x05;

        /// <summary>%QW 地址映射到标准 Modbus FC06。</summary>
        public const byte Fc06WriteRegister = 0x06;

        // ── 最大限制 ──

        /// <summary>最大批量读取寄存器数量。</summary>
        public const int MaxReadRegisters = 125;

        /// <summary>最大批量写入寄存器数量。</summary>
        public const int MaxWriteRegisters = 100;

        /// <summary>最大批量读取位数量。</summary>
        public const int MaxReadBits = 2000;
    }

    /// <summary>施耐德错误码。</summary>
    public static class SchneiderErrorCodes
    {
        /// <summary>获取 Modicon 错误码的中文描述。</summary>
        public static string GetDescription(byte errorCode)
        {
            // Modicon 使用标准 Modbus 错误码 + 自定义扩展
            switch (errorCode)
            {
                case 0x01: return "非法功能码";
                case 0x02: return "非法数据地址";
                case 0x03: return "非法数据值";
                case 0x04: return "从站设备故障";
                case 0x05: return "确认 — 从站已接受请求，正在处理";
                case 0x06: return "从站设备忙";
                case 0x07: return "否定确认 — 从站无法执行";
                case 0x08: return "内存奇偶校验错误";
                case 0x0A: return "网关路径不可用";
                case 0x0B: return "网关目标设备无响应";
                case 0x41: return "Modicon 扩展错误 — 地址范围超限";
                case 0x42: return "Modicon 扩展错误 — 功能码不支持";
                case 0x43: return "Modicon 扩展错误 — 数据长度错误";
                case 0x44: return "Modicon 扩展错误 — 系统忙";
                case 0x45: return "Modicon 扩展错误 — 写入保护";
                case 0x46: return "Modicon 扩展错误 — 语法错误";
                case 0x47: return "Modicon 扩展错误 — 通信错误";
                default: return $"未知错误 ({errorCode:X2})";
            }
        }
    }

    /// <summary>PLC 识别信息。</summary>
    public class SchneiderPlcInfo
    {
        /// <summary>设备类型编码。</summary>
        public ushort DeviceType { get; set; }
        /// <summary>固件版本。</summary>
        public ushort FirmwareVersion { get; set; }
        /// <summary>硬件版本。</summary>
        public ushort HardwareVersion { get; set; }
        /// <summary>状态字。</summary>
        public ushort StatusWord { get; set; }
    }

    /// <summary>PLC 诊断信息。</summary>
    public class SchneiderDiagnostics
    {
        /// <summary>通信错误计数。</summary>
        public ushort CommErrorCount { get; set; }
        /// <summary>CRC 错误计数。</summary>
        public ushort CrcErrorCount { get; set; }
        /// <summary>超时计数。</summary>
        public ushort TimeoutCount { get; set; }
        /// <summary>异常响应计数。</summary>
        public ushort ExceptionCount { get; set; }
        /// <summary>最后错误码。</summary>
        public ushort LastErrorCode { get; set; }
        /// <summary>运行模式（0=Stop, 1=Run, 2=Debug）。</summary>
        public ushort RunMode { get; set; }
    }
}
