using System;

namespace Nexus.Robot.Ur
{
    /// <summary>UR 机器人型号。</summary>
    public enum UrModel
    {
        /// <summary>UR3e。</summary>
        UR3e,
        /// <summary>UR5e。</summary>
        UR5e,
        /// <summary>UR10e。</summary>
        UR10e,
        /// <summary>UR16e。</summary>
        UR16e,
        /// <summary>UR3 (旧版)。</summary>
        UR3,
        /// <summary>UR5 (旧版)。</summary>
        UR5,
        /// <summary>UR10 (旧版)。</summary>
        UR10,
    }

    /// <summary>UR 坐标系。</summary>
    public enum UrCoordinateSystem
    {
        /// <summary>基坐标系。</summary>
        Base = 0,
        /// <summary>工具坐标系。</summary>
        Tool = 1,
        /// <summary>自定义坐标系。</summary>
        Custom = 2,
    }

    /// <summary>UR 运行模式。</summary>
    public enum UrRunMode
    {
        /// <summary>停止。</summary>
        Stopped = 0,
        /// <summary>正在运行。</summary>
        Running = 1,
        /// <summary>暂停。</summary>
        Paused = 2,
    }

    /// <summary>UR 常量。</summary>
    public static class UrConstants
    {
        /// <summary>UR Primary Interface 默认端口。</summary>
        public const int PrimaryPort = 30001;

        /// <summary>UR Secondary Interface 默认端口（URScript 命令）。</summary>
        public const int SecondaryPort = 30002;

        /// <summary>UR Real-Time Interface 默认端口。</summary>
        public const int RealTimePort = 30003;

        /// <summary>UR Dashboard Server 端口。</summary>
        public const int DashboardPort = 29999;

        /// <summary>UR 关节数量。</summary>
        public const int JointCount = 6;

        /// <summary>最大 URScript 命令长度。</summary>
        public const int MaxScriptLength = 1024;

        // ── Dashboard 命令 ──

        /// <summary>运行加载的程序。</summary>
        public const string CmdPlay = "play\n";

        /// <summary>暂停程序。</summary>
        public const string CmdPause = "pause\n";

        /// <summary>停止程序。</summary>
        public const string CmdStop = "stop\n";

        /// <summary>关闭安全弹窗。</summary>
        public const string CmdClosePopup = "close popup\n";

        /// <summary>关闭安全弹窗并停止。</summary>
        public const string CmdCloseSafetyPopup = "close safety_popup\n";

        /// <summary>解锁保护停机。</summary>
        public const string CmdUnlockProtectiveStop = "unlock protective stop\n";

        /// <summary>关机。</summary>
        public const string CmdShutdown = "shutdown\n";

        /// <summary>是否正在运行。</summary>
        public const string CmdRunning = "running\n";

        /// <summary>获取机器人模式。</summary>
        public const string CmdRobotMode = "robotmode\n";

        /// <summary>获取加载的程序名。</summary>
        public const string CmdGetLoadedProgram = "get loaded program\n";

        /// <summary>加载程序。</summary>
        public const string CmdLoadProgram = "load ";

        /// <summary>弹出对话框。</summary>
        public const string CmdPopup = "popup ";

        /// <summary>日志消息。</summary>
        public const string CmdAddToLog = "addToLog ";

        /// <summary>刹车释放。</summary>
        public const string CmdBrakeRelease = "brake release\n";

        /// <summary>设置操作模式。</summary>
        public const string CmdSetOperationalMode = "set operational mode ";

        /// <summary>清除操作模式。</summary>
        public const string CmdClearOperationalMode = "clear operational mode\n";

        /// <summary>设置速度。</summary>
        public const string CmdSetSpeed = "set speed ";
    }

    /// <summary>UR 错误码。</summary>
    public static class UrErrorCodes
    {
        /// <summary>获取 Dashboard 响应错误描述。</summary>
        public static string GetDescription(string response)
        {
            if (string.IsNullOrEmpty(response)) return "空响应";

            // Dashboard 响应格式: "关键字: 详细信息"
            if (response.StartsWith("Connected"))
                return "已连接到 Dashboard";
            if (response.Contains("not running"))
                return "程序未运行";
            if (response.Contains("running"))
                return "程序正在运行";
            if (response.Contains("No program running"))
                return "没有程序在运行";
            if (response.Contains("Protective stopped"))
                return "保护停机";
            if (response.Contains("Emergency stopped"))
                return "急停";
            if (response.Contains("offline"))
                return "机器人离线";
            if (response.Contains("Could not"))
                return "操作失败: " + response.Trim();

            return response.Trim();
        }
    }
}
