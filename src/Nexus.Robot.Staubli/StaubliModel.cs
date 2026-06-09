using System;

namespace Nexus.Robot.Staubli
{
    /// <summary>Stäubli 机器人型号。</summary>
    public enum StaubliModel
    {
        /// <summary>TX2-40。</summary>
        TX2_40,
        /// <summary>TX2-60。</summary>
        TX2_60,
        /// <summary>TX2-60L。</summary>
        TX2_60L,
        /// <summary>TX2-90。</summary>
        TX2_90,
        /// <summary>TX2-90L。</summary>
        TX2_90L,
        /// <summary>TX2-160。</summary>
        TX2_160,
        /// <summary>TX2-160L。</summary>
        TX2_160L,
        /// <summary>TS2-40。</summary>
        TS2_40,
        /// <summary>TS2-60。</summary>
        TS2_60,
        /// <summary>TS2-80。</summary>
        TS2_80,
        /// <summary>TS2-100。</summary>
        TS2_100,
        /// <summary>CS8 控制器。</summary>
        CS8,
        /// <summary>CS9 控制器。</summary>
        CS9,
    }

    /// <summary>Stäubli 运动模式。</summary>
    public enum StaubliMotionMode
    {
        /// <summary>关节运动 (joint)。</summary>
        Joint,
        /// <summary>线性运动 (linear)。</summary>
        Linear,
        /// <summary>圆弧运动 (circular)。</summary>
        Circular,
    }

    /// <summary>Stäubli I/O 类型。</summary>
    public enum StaubliIoType
    {
        /// <summary>数字输入。</summary>
        DigitalInput,
        /// <summary>数字输出。</summary>
        DigitalOutput,
        /// <summary>模拟输入。</summary>
        AnalogInput,
        /// <summary>模拟输出。</summary>
        AnalogOutput,
    }

    /// <summary>Stäubli 常量。</summary>
    public static class StaubliConstants
    {
        /// <summary>VAL3 命令端口（CS8/CS9）。</summary>
        public const int CommandPort = 59000;

        /// <summary>UniVal 命令端口。</summary>
        public const int UniValPort = 8080;

        /// <summary>文件传输端口。</summary>
        public const int FileTransferPort = 21;

        /// <summary>关节数量（6 轴）。</summary>
        public const int JointCount = 6;

        /// <summary>最大 VAL3 命令长度。</summary>
        public const int MaxCommandLength = 4096;

        // ── VAL3 命令模板 ──

        /// <summary>移动到目标位姿（线性）。</summary>
        public const string CmdMoveL = "movej";

        /// <summary>关节运动。</summary>
        public const string CmdMoveJ = "movej";

        /// <summary>接近运动。</summary>
        public const string CmdAppro = "appro";

        /// <summary>离开运动。</summary>
        public const string CmdDepart = "depart";

        /// <summary>设置数字输出。</summary>
        public const string CmdSetDio = "set";

        /// <summary>获取数字输入。</summary>
        public const string CmdGetDio = "get";

        /// <summary>停止运动。</summary>
        public const string CmdStop = "stop";

        /// <summary>等待时间。</summary>
        public const string CmdDelay = "delay";

        /// <summary>打开夹爪。</summary>
        public const string CmdOpenGripper = "close";

        /// <summary>关闭夹爪。</summary>
        public const string CmdCloseGripper = "close";

        // ── 响应标识 ──

        /// <summary>成功响应前缀。</summary>
        public const string ResponseOk = "OK";

        /// <summary>错误响应前缀。</summary>
        public const string ResponseError = "ERROR";
    }

    /// <summary>Stäubli 错误码。</summary>
    public static class StaubliErrorCodes
    {
        /// <summary>获取 VAL3 错误描述。</summary>
        public static string GetDescription(string errorCode)
        {
            if (string.IsNullOrEmpty(errorCode)) return "空响应";

            if (errorCode.StartsWith("0") || errorCode.StartsWith("OK")) return "正常完成";
            if (errorCode.Contains("syntax")) return "语法错误 — VAL3 命令格式不正确";
            if (errorCode.Contains("undefined")) return "未定义变量或函数";
            if (errorCode.Contains("limit")) return "运动超限 — 超出关节或工作空间限制";
            if (errorCode.Contains("collision")) return "碰撞检测触发";
            if (errorCode.Contains("emergency")) return "急停激活";
            if (errorCode.Contains("protection")) return "保护停机";
            if (errorCode.Contains("speed")) return "速度超限";
            if (errorCode.Contains("io")) return "I/O 错误 — 指定的 I/O 不存在或不可访问";
            if (errorCode.Contains("not connected")) return "未连接到机器人控制器";
            if (errorCode.Contains("timeout")) return "通信超时";
            if (errorCode.Contains("busy")) return "机器人忙 — 正在执行其他任务";
            if (errorCode.Contains("power")) return "电源未上电";
            if (errorCode.Contains("motor")) return "电机未使能";

            return $"未知错误: {errorCode}";
        }
    }
}
