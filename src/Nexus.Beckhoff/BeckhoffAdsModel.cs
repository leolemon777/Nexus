namespace Nexus.Beckhoff
{
    /// <summary>ADS 命令码。</summary>
    public enum AdsCommand : ushort
    {
        ReadDeviceInfo = 0x0001,
        Read = 0x0002,
        Write = 0x0003,
        ReadState = 0x0004,
        WriteControl = 0x0005,
        AddDeviceNotification = 0x0006,
        DeleteDeviceNotification = 0x0007,
        DeviceNotification = 0x0008,
        ReadWrite = 0x0009,
    }

    /// <summary>ADS 数据类型。</summary>
    public enum AdsDataType : uint
    {
        Bit = 0x0001,
        Int8 = 0x0010,
        UInt8 = 0x0011,
        Int16 = 0x0002,
        UInt16 = 0x0003,
        Int32 = 0x0004,
        UInt32 = 0x0005,
        Int64 = 0x0006,
        UInt64 = 0x0007,
        Float32 = 0x0008,
        Float64 = 0x0009,
        String = 0x001E,
        WString = 0x001F,
        Array = 0x0020,
        Struct = 0x0021,
        BigType = 0x0030,
    }

    /// <summary>ADS 状态码。</summary>
    public enum AdsErrorCode : uint
    {
        NoError = 0,
        InternalError = 0x0001,
        NoTarget = 0x0002,
        TargetNotFound = 0x0003,
        InvalidHandle = 0x0004,
        InvalidIndexGroup = 0x0005,
        InvalidIndexOffset = 0x0006,
        ReadAccessDenied = 0x0007,
        WriteAccessDenied = 0x0008,
        ParameterAccessDenied = 0x0009,
        InvalidParameterSize = 0x000A,
        InvalidParameterValues = 0x000B,
        NotificationAlreadyRegistered = 0x000C,
        NotificationNotFound = 0x000D,
        NotificationClientNotRegistered = 0x000E,
        NoMoreHandles = 0x000F,
        SizeMismatch = 0x0010,
        InvalidDataLength = 0x0011,
        InvalidDataType = 0x0012,
        InvalidData = 0x0013,
        TargetQueryRequired = 0x0014,
        SymbolNotFound = 0x0015,
        SymbolVersionInvalid = 0x0016,
        ProcessingInProgress = 0x0017,
        NoProcessingResources = 0x0018,
        NoMoreSymbols = 0x0019,
        WatchdogTimeout = 0x001A,
        InvalidSymbolVersion = 0x001B,
    }

    /// <summary>TwinCAT ADS 端口常量。</summary>
    public static class BeckhoffAdsConstants
    {
        /// <summary>AMS NetId 占用字节数。</summary>
        public const int AmsNetIdLength = 6;

        /// <summary>AMS 端口号。</summary>
        public const ushort PortTc2Plc = 801;
        public const ushort PortTc3Plc = 851;
        public const ushort PortTc2SystemService = 10000;
        public const ushort PortTc3SystemService = 10000;
        public const ushort PortTc2Io = 810;
        public const ushort PortTc3Io = 852;
        public const ushort PortTc2NC = 820;
        public const ushort PortTc3NC = 853;

        /// <summary>ADS TCP 头部长度。</summary>
        public const int TcpHeaderLength = 6;  // 2 bytes magic + 4 bytes length

        /// <summary>AMS 头部长度。</summary>
        public const int AmsHeaderLength = 32;

        /// <summary>默认 AMS 端口。</summary>
        public const int DefaultPort = 48898;  // ADS default TCP port

        /// <summary>Symbol Index Group。</summary>
        public const uint SymbolIndexGroup = 0xF003;

        /// <summary>Symbol Index Offset。</summary>
        public const uint SymbolIndexOffset = 0x0000;

        /// <summary>Symbol Size Entry Index Group。</summary>
        public const uint SymbolSizeIndexGroup = 0xF006;
    }

    /// <summary>TwinCAT PLC 型号（控制器型号）。</summary>
    public enum BeckhoffPlcModel
    {
        Unknown,
        Tc2,
        Tc3,
        Tc25,
        Cx5010,
        Cx5020,
        Cx5130,
        Cx5140,
        Cx9020,
        Cx2040,
    }
}
