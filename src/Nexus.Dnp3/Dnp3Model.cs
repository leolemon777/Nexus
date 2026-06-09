using System;

namespace Nexus.Dnp3
{
    /// <summary>DNP3 功能码。</summary>
    public enum Dnp3FunctionCode : byte
    {
        /// <summary>确认。</summary>
        Confirm = 0x00,
        /// <summary>读取。</summary>
        Read = 0x01,
        /// <summary>写入。</summary>
        Write = 0x02,
        /// <summary>选择。</summary>
        Select = 0x03,
        /// <summary>操作。</summary>
        Operate = 0x04,
        /// <summary>直接操作。</summary>
        DirectOperate = 0x05,
        /// <summary>直接操作无确认。</summary>
        DirectOperateNoAck = 0x06,
        /// <summary>冻结。</summary>
        Freeze = 0x07,
        /// <summary>冻结确认。</summary>
        FreezeClear = 0x08,
        /// <summary>冻结冻结时间。</summary>
        FreezeAtTime = 0x09,
        /// <summary>冷重启。</summary>
        ColdRestart = 0x0D,
        /// <summary>热重启。</summary>
        WarmRestart = 0x0E,
        /// <summary>初始化数据。</summary>
        InitializeData = 0x10,
        /// <summary>初始化应用。</summary>
        InitializeApplication = 0x12,
        /// <summary>启动应用。</summary>
        StartApplication = 0x13,
        /// <summary>停止应用。</summary>
        StopApplication = 0x14,
        /// <summary>保存配置。</summary>
        SaveConfiguration = 0x15,
        /// <summary>启用非请求数据。</summary>
        EnableUnsolicited = 0x14,
        /// <summary>禁用非请求数据。</summary>
        DisableUnsolicited = 0x15,
        /// <summary>分配类别。</summary>
        AssignClass = 0x16,
        /// <summary>延迟测量。</summary>
        DelayMeasure = 0x17,
        /// <summary>记录当前时间。</summary>
        RecordCurrentTime = 0x18,
        /// <summary>打开文件。</summary>
        OpenFile = 0x19,
        /// <summary>关闭文件。</summary>
        CloseFile = 0x1A,
        /// <summary>删除文件。</summary>
        DeleteFile = 0x1B,
        /// <summary>获取文件信息。</summary>
        GetFileInfo = 0x1C,
        /// <summary>设置文件。</summary>
        SetFile = 0x1D,
        /// <summary>认证请求。</summary>
        AuthenticateFile = 0x1E,
        /// <summary>响应。</summary>
        Response = 0x81,
        /// <summary>非请求响应。</summary>
        UnsolicitedResponse = 0x82,
    }

    /// <summary>DNP3 数据组（对象类型）。</summary>
    public enum Dnp3Group : byte
    {
        /// <summary>二进制输入（静态）。</summary>
        BinaryInput = 1,
        /// <summary>二进制输入事件。</summary>
        BinaryInputEvent = 2,
        /// <summary>双位二进制输入。</summary>
        DoubleBitBinaryInput = 3,
        /// <summary>双位二进制输入事件。</summary>
        DoubleBitBinaryInputEvent = 4,
        /// <summary>二进制输出状态。</summary>
        BinaryOutput = 10,
        /// <summary>二进制输出事件。</summary>
        BinaryOutputEvent = 11,
        /// <summary>计数器输入。</summary>
        Counter = 20,
        /// <summary>计数器输入事件。</summary>
        CounterEvent = 22,
        /// <summary>模拟输入。</summary>
        AnalogInput = 30,
        /// <summary>模拟输入事件。</summary>
        AnalogInputEvent = 32,
        /// <summary>模拟输出状态。</summary>
        AnalogOutput = 40,
        /// <summary>模拟输出事件。</summary>
        AnalogOutputEvent = 42,
        /// <summary>时间偏移。</summary>
        TimeAndDate = 50,
        /// <summary>类别数据。</summary>
        ClassData = 60,
        /// <summary>文件控制。</summary>
        FileControl = 70,
        /// <summary>设备信息。</summary>
        DeviceInformation = 80,
        /// <summary>数据集。</summary>
        DataSet = 85,
        /// <summary>安全统计。</summary>
        SecureAuthentication = 120,
    }

    /// <summary>DNP3 变体号。</summary>
    public enum Dnp3Variation : byte
    {
        /// <summary>二进制输入 — 打包格式。</summary>
        BinaryInputPacked = 0x01,
        /// <summary>二进制输入 — 带时间戳。</summary>
        BinaryInputWithTime = 0x02,
        /// <summary>双位输入 — 打包格式。</summary>
        DoubleBitBinaryPacked = 0x01,
        /// <summary>计数器 — 32 位。</summary>
        Counter32 = 0x01,
        /// <summary>计数器 — 16 位。</summary>
        Counter16 = 0x02,
        /// <summary>模拟输入 — 32 位浮点。</summary>
        AnalogInputFloat32 = 0x04,
        /// <summary>模拟输入 — 16 位整型。</summary>
        AnalogInputInt16 = 0x01,
        /// <summary>模拟输入 — 32 位整型。</summary>
        AnalogInputInt32 = 0x02,
        /// <summary>模拟输入 — 双精度浮点。</summary>
        AnalogInputFloat64 = 0x05,
    }

    /// <summary>DNP3 传输层常量。</summary>
    public static class Dnp3Constants
    {
        /// <summary>默认 DNP3 TCP 端口。</summary>
        public const int DefaultTcpPort = 20000;

        /// <summary>默认 DNP3 UDP 端口。</summary>
        public const int DefaultUdpPort = 20000;

        /// <summary>默认串口波特率。</summary>
        public const int DefaultBaudRate = 9600;

        /// <summary>数据链路层起始字节 1。</summary>
        public const byte StartByte1 = 0x05;

        /// <summary>数据链路层起始字节 2。</summary>
        public const byte StartByte2 = 0x64;

        /// <summary>数据链路层帧头长度（10 字节）。</summary>
        public const int LinkHeaderLength = 10;

        /// <summary>应用层确认超时（毫秒）。</summary>
        public const int DefaultConfirmTimeout = 5000;

        /// <summary>最大应用层数据块大小 (2048)。</summary>
        public const int MaxAppDataSize = 2048;

        /// <summary>最大用户数据长度 (249)。</summary>
        public const int MaxUserDataLength = 249;

        /// <summary>默认主站地址。</summary>
        public const ushort DefaultMasterAddress = 1;

        /// <summary>默认从站地址。</summary>
        public const ushort DefaultOutstationAddress = 1024;

        // ── 限定词常量 ──

        /// <summary>索引模式 — 无索引。</summary>
        public const byte QualifierNoIndex = 0x00;

        /// <summary>索引模式 — 1 字节起始/停止。</summary>
        public const byte QualifierStartStop1 = 0x00;

        /// <summary>索引模式 — 2 字节起始/停止。</summary>
        public const byte QualifierStartStop2 = 0x01;

        /// <summary>非请求 IIN 位。</summary>
        public const ushort IINUnsolicited = 0x0004;
    }

    /// <summary>DNP3 IIN（内部指示位）标志。</summary>
    [Flags]
    public enum Dnp3IinFlags : ushort
    {
        /// <summary>无标志。</summary>
        None = 0x0000,
        /// <summary>所有站重启。</summary>
        AllStationsRestarted = 0x0001,
        /// <summary>设备重启。</summary>
        DeviceRestart = 0x0002,
        /// <summary>需要时间同步。</summary>
        NeedTime = 0x0004,
        /// <summary>设备有本地更改。</summary>
        LocalControl = 0x0020,
        /// <summary>设备故障。</summary>
        DeviceTrouble = 0x0040,
        /// <summary>支持非请求响应。</summary>
        UnsolicitedResponseSupported = 0x0080,
        /// <summary>数据溢出。</summary>
        DataOverflow = 0x0100,
        /// <summary>请求通信。</summary>
        ConfigCorrupt = 0x0200,
    }

    /// <summary>DNP3 错误码。</summary>
    public static class Dnp3ErrorCodes
    {
        /// <summary>获取 DNP3 内部指示码描述。</summary>
        public static string GetIinDescription(ushort iin)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((iin & 0x0001) != 0) parts.Add("所有站已重启");
            if ((iin & 0x0002) != 0) parts.Add("设备重启");
            if ((iin & 0x0004) != 0) parts.Add("需要时间同步");
            if ((iin & 0x0020) != 0) parts.Add("本地控制");
            if ((iin & 0x0040) != 0) parts.Add("设备故障");
            if ((iin & 0x0080) != 0) parts.Add("支持非请求响应");
            if ((iin & 0x0100) != 0) parts.Add("数据溢出");
            if ((iin & 0x0200) != 0) parts.Add("配置损坏");
            return parts.Count == 0 ? "正常" : string.Join(", ", parts.ToArray());
        }

        /// <summary>获取 DNP3 应用层错误码描述。</summary>
        public static string GetAppErrorDescription(byte errorCode)
        {
            switch (errorCode)
            {
                case 0: return "正常";
                case 1: return "功能码不支持";
                case 2: return "对象未知";
                case 3: return "参数错误 — 限定词不支持";
                case 4: return "数据溢出";
                case 5: return "操作不支持";
                case 6: return "对象只读";
                case 7: return "对象不可用";
                case 8: return "参数错误 — 变体不支持";
                default: return $"未知错误 ({errorCode})";
            }
        }
    }
}
