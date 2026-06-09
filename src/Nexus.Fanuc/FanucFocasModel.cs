namespace Nexus.Fanuc
{
    /// <summary>FANUC CNC 型号。</summary>
    public enum FanucCncModel
    {
        Unknown,
        Series0iD,
        Series0iF,
        Series16i,
        Series18i,
        Series21i,
        Series30i,
        Series31i,
        Series32i,
        Series35i,
        Series0iMateD,
        Series0iMateF,
        SeriesPMiD,
    }

    /// <summary>FANUC CNC 运行模式枚举。</summary>
    /// <remarks>注意: Client 中已有 FanucCncStatus class 描述运行状态，此处为模式枚举。</remarks>
    public enum FanucRunMode
    {
        /// <summary>急停。</summary>
        EmergencyStop = 0,
        /// <summary>报警。</summary>
        Alarm = 1,
        /// <summary>进给保持。</summary>
        FeedHold = 2,
        /// <summary>暂停。</summary>
        Pause = 3,
        /// <summary>运行中。</summary>
        Running = 4,
        /// <summary>MDI 模式。</summary>
        Mdi = 5,
        /// <summary>JOG 模式。</summary>
        Jog = 6,
        /// <summary>参考点返回。</summary>
        Reference = 7,
        /// <summary>自动模式。</summary>
        Auto = 8,
        /// <summary>手动模式。</summary>
        Manual = 9,
        /// <summary>编辑模式。</summary>
        Edit = 10,
    }

    /// <summary>FANUC FOCAS 常量。</summary>
    public static class FanucFocasConstants
    {
        /// <summary>默认 FOCAS2 端口。</summary>
        public const int DefaultPort = 8193;

        /// <summary>最大轴数。</summary>
        public const int MaxAxes = 32;

        /// <summary>最大主轴数。</summary>
        public const int MaxSpindles = 8;

        /// <summary>最大程序名长度。</summary>
        public const int MaxProgramNameLength = 256;

        /// <summary>最大报警数。</summary>
        public const int MaxAlarms = 255;

        /// <summary>最大读取字符串长度。</summary>
        public const int MaxStringLen = 256;

        // FOCAS2 通用错误码
        public const int EwOk = 0;
        public const int EwFunc = -1;
        public const int EwAxis = -2;
        public const int EwHandle = -3;
        public const int EwNoopt = -4;
        public const int EwProtect = -5;
        public const int EwParam = -6;
        public const int EwBuffer = -7;
        public const int EwStop = -8;
        public const int EwSystem = -9;
        public const int EwDevice = -10;
        public const int EwSocket = -11;
        public const int EwTimeout = -12;
        public const int EwConnect = -13;
        public const int EwNodata = -14;
        public const int EwBus = -15;
        public const int EwWrite = -16;

        /// <summary>将 FOCAS 错误码转为中文描述。</summary>
        public static string ToDescription(int errorCode) => errorCode switch
        {
            0 => "正常完成",
            -1 => "无效函数",
            -2 => "无效轴号",
            -3 => "无效连接句柄",
            -4 => "无选项",
            -5 => "写保护",
            -6 => "参数错误",
            -7 => "缓冲区溢出",
            -8 => "CNC 已停止",
            -9 => "系统错误",
            -10 => "设备错误",
            -11 => "Socket 错误",
            -12 => "通讯超时",
            -13 => "连接失败",
            -14 => "无数据",
            -15 => "总线错误",
            -16 => "写入失败",
            _ => $"未知错误: {errorCode}"
        };
    }

    /// <summary>FANUC 坐标系。</summary>
    public enum FanucCoordinateSystem
    {
        /// <summary>机械坐标。</summary>
        Machine = 0,
        /// <summary>绝对坐标。</summary>
        Absolute = 1,
        /// <summary>相对坐标。</summary>
        Relative = 2,
        /// <summary>距离待走坐标。</summary>
        Distance = 3,
    }

    /// <summary>FANUC 倍率来源。</summary>
    public enum FanucOverrideSource
    {
        /// <summary>进给倍率。</summary>
        FeedOverride = 0,
        /// <summary>快速移动倍率。</summary>
        RapidOverride = 1,
        /// <summary>主轴倍率。</summary>
        SpindleOverride = 2,
        /// <summary>JOG 进给倍率。</summary>
        JogOverride = 3,
    }
}
