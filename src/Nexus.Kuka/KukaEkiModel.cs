namespace Nexus.Kuka
{
    /// <summary>KUKA 机器人控制器型号。</summary>
    public enum KukaControllerModel
    {
        Unknown,
        KrC2,
        KrC4,
        KrC5,
        KrC4Mini,
        KrC5Micro,
    }

    /// <summary>KUKA EKI 变量数据类型。</summary>
    public enum KukaEkiDataType
    {
        Bool,
        Int8,
        UInt8,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Float32,
        Float64,
        String,
    }

    /// <summary>KUKA 机器人运动参考系。</summary>
    public enum KukaCoordinateSystem
    {
        /// <summary>基坐标系。</summary>
        Base = 0,
        /// <summary>工具坐标系。</summary>
        Tool = 1,
        /// <summary>世界坐标系。</summary>
        World = 2,
        /// <summary>法兰坐标系。</summary>
        Flange = 5,
    }

    /// <summary>KUKA 机器人运行模式。</summary>
    public enum KukaRunMode
    {
        /// <summary>T1 模式（手动低速）。</summary>
        T1 = 1,
        /// <summary>T2 模式（手动高速）。</summary>
        T2 = 2,
        /// <summary>AUT 模式（自动）。</summary>
        Auto = 3,
        /// <summary>AUT EXT 模式（外部自动）。</summary>
        AutoExt = 4,
    }

    /// <summary>KUKA EKI 常量。</summary>
    public static class KukaEkiConstants
    {
        /// <summary>默认 EKI 端口。</summary>
        public const int DefaultPort = 54600;

        /// <summary>XML 声明头。</summary>
        public const string XmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

        /// <summary>读取变量 XML 模板。</summary>
        public const string ReadVarTemplate = "<Robot><Read Var=\"{0}\" /></Robot>";

        /// <summary>写入变量 XML 模板。</summary>
        public const string WriteVarBoolTemplate = "<Robot><Write Var=\"{0}\">{1}</Write></Robot>";
        public const string WriteVarIntTemplate = "<Robot><Write Var=\"{0}\">{1}</Write></Robot>";
        public const string WriteVarFloatTemplate = "<Robot><Write Var=\"{0}\">{1}</Write></Robot>";

        /// <summary>常用 EKI 变量名。</summary>
        public static readonly string[] CommonVariables = new[]
        {
            "$POS_ACT",      // 当前笛卡尔位置
            "$POS_ACT_MES",  // 当前测量位置
            "$AXIS_ACT",     // 当前轴角度
            "$TOOL",         // 当前工具坐标系
            "$BASE",         // 当前基坐标系
            "$PRO_STATE",    // 程序运行状态
            "$OV_PRO",       // 程序覆盖倍率 (0-100)
            "$ROB_STOP",     // 机器人停止状态
            "$MODE_OP",      // 运行模式
            "$PRO_IP",       // 当前程序信息
            "$OUT[1]",       // 数字输出 1
            "$IN[1]",        // 数字输入 1
            "$TIMER[1]",     // 定时器 1
            "$FLAG[1]",      // 标志位 1
        };
    }
}
