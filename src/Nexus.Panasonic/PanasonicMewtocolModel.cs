namespace Nexus.Panasonic
{
    /// <summary>松下 FP 系列 PLC 区域枚举。</summary>
    public enum PanasonicArea
    {
        /// <summary>数据寄存器 DT。</summary>
        DataRegister,
        /// <summary>保持数据寄存器 LDT。</summary>
        KeepRegister,
        /// <summary>文件寄存器 FLT。</summary>
        FileRegister,
        /// <summary>特殊数据寄存器 SDT。</summary>
        SpecialRegister,
        /// <summary>输出 Y。</summary>
        OutputCoil,
        /// <summary>输入 X（只读）。</summary>
        InputDiscrete,
        /// <summary>内部继电器 R。</summary>
        InternalRelay,
        /// <summary>特殊继电器。</summary>
        SpecialRelay,
        /// <summary>定时器线圈 T。</summary>
        TimerCoil,
        /// <summary>计数器线圈 C。</summary>
        CounterCoil,
        /// <summary>定时器当前值 SV。</summary>
        TimerValue,
        /// <summary>计数器当前值 EV。</summary>
        CounterValue,
    }

    /// <summary>松下 FP 系列 PLC 型号。</summary>
    public enum PanasonicFpModel
    {
        Unknown,
        Fp0,
        Fp0R,
        FpSigma,
        FpX0,
        FpXH,
        Fp2,
        Fp2Sh,
        Fp3,
        Fp5,
        Fp10,
        Fp10Sh,
        Fp2C,
        FpMe,
    }

    /// <summary>Mewtocol 通讯常量。</summary>
    public static class PanasonicMewtocolConstants
    {
        /// <summary>STX 控制字符。</summary>
        public const byte STX = 0x02;
        /// <summary>ETX 控制字符。</summary>
        public const byte ETX = 0x03;
        /// <summary>ENQ 控制字符。</summary>
        public const byte ENQ = 0x05;
        /// <summary>ACK 控制字符。</summary>
        public const byte ACK = 0x06;
        /// <summary>NAK 控制字符。</summary>
        public const byte NAK = 0x15;
        /// <summary>EOT 控制字符。</summary>
        public const byte EOT = 0x04;
        /// <summary>CR 控制字符。</summary>
        public const byte CR = 0x0D;

        /// <summary>Mewtocol 帧头标记。</summary>
        public const string HeaderFlag = "%";

        /// <summary>读取命令标识。</summary>
        public const string CmdRead = "RCS";   // 单点读取
        public const string CmdReadMulti = "RCC"; // 多点读取（位）
        public const string CmdReadWord = "RD";   // 单字读取
        public const string CmdReadWordMulti = "RDW"; // 多字读取

        /// <summary>写入命令标识。</summary>
        public const string CmdWrite = "WCS";  // 单点写入
        public const string CmdWriteMulti = "WCC"; // 多点写入（位）
        public const string CmdWriteWord = "WD";   // 单字写入
        public const string CmdWriteWordMulti = "WDW"; // 多字写入

        /// <summary>默认 TCP 端口。</summary>
        public const int DefaultTcpPort = 9094;
        /// <summary>默认串口波特率。</summary>
        public const int DefaultBaudRate = 9600;
    }

    /// <summary>Mewtocol 错误码。</summary>
    public static class PanasonicErrorCodes
    {
        public const string NormalCompletion = "";
        public const string UndefinedCommand = "!";
        public const string NotSupported = "\"";
        public const string Busy = "#";
        public const string CommunicationError = "$";
        public const string UnitAddressError = "%";
        public const string LrcError = "&";
        public const string DataLengthError = "'";
        public const string WriteNotPermitted = "(";
        public const string AddressError = ")";
        public const string DataError = "*";

        /// <summary>将错误码转为中文描述。</summary>
        public static string ToDescription(string errorCode) => errorCode switch
        {
            "" => "正常完成",
            "!" => "未定义命令",
            "\"" => "不支持的命令",
            "#" => "PLC 忙（运行模式）",
            "$" => "通讯错误",
            "%" => "单元地址错误",
            "&" => "LRC 校验错误",
            "'" => "数据长度错误",
            "(" => "写入不允许（PLC 处于运行状态）",
            ")" => "地址错误 — 超出范围",
            "*" => "数据值错误",
            _ => $"未知错误: {errorCode}"
        };
    }
}
