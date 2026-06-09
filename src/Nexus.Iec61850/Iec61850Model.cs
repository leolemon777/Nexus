using System;

namespace Nexus.Iec61850
{
    /// <summary>IEC 61850 服务类型。</summary>
    public enum IecServiceType
    {
        /// <summary>关联 (Associate)。</summary>
        Associate,
        /// <summary>释放 (Release)。</summary>
        Release,
        /// <summary>获取服务器目录。</summary>
        GetServerDirectory,
        /// <summary>获取逻辑设备目录。</summary>
        GetLogicalDeviceDirectory,
        /// <summary>获取逻辑节点目录。</summary>
        GetLogicalNodeDirectory,
        /// <summary>获取数据定义。</summary>
        GetDataDefinition,
        /// <summary>获取数据值。</summary>
        GetDataValues,
        /// <summary>设置数据值。</summary>
        SetDataValues,
        /// <summary>获取数据目录。</summary>
        GetDataDirectory,
        /// <summary>数据集获取值。</summary>
        GetDataSetValues,
        /// <summary>数据集设置值。</summary>
        SetDataSetValues,
        /// <summary>创建数据集。</summary>
        CreateDataSet,
        /// <summary>删除数据集。</summary>
        DeleteDataSet,
        /// <summary>获取数据集目录。</summary>
        GetDataSetDirectory,
        /// <summary>报告控制块 — 缓存报告。</summary>
        GetBRCBValues,
        /// <summary>报告控制块 — 缓存报告设置。</summary>
        SetBRCBValues,
        /// <summary>报告控制块 — 非缓存报告。</summary>
        GetURCBValues,
        /// <summary>报告控制块 — 非缓存报告设置。</summary>
        SetURCBValues,
        /// <summary>控制 — 选择。</summary>
        Select,
        /// <summary>控制 — 选择带值。</summary>
        SelectWithValue,
        /// <summary>控制 — 取消。</summary>
        Cancel,
        /// <summary>控制 — 操作。</summary>
        Operate,
        /// <summary>控制 — 命令终止。</summary>
        CommandTermination,
        /// <summary>GOOSE 订阅。</summary>
        SubscribeGOOSE,
        /// <summary>GOOSE 发布。</summary>
        PublishGOOSE,
        /// <summary>SV（采样值）订阅。</summary>
        SubscribeSV,
        /// <summary>SV（采样值）发布。</summary>
        PublishSV,
    }

    /// <summary>IEC 61850 功能约束 (FC) — 数据的访问类别。</summary>
    public enum FunctionalConstraint : byte
    {
        /// <summary>状态信息 (ST)。</summary>
        ST = 0x01,
        /// <summary>测量值 (MX)。</summary>
        MX = 0x02,
        /// <summary>设置 (SP)。</summary>
        SP = 0x03,
        /// <summary>替代值 (SV)。</summary>
        SV = 0x04,
        /// <summary>控制 (CF)。</summary>
        CF = 0x05,
        /// <summary>描述 (DC)。</summary>
        DC = 0x06,
        /// <summary>设定参数组 (SG)。</summary>
        SG = 0x07,
        /// <summary>设定组编辑 (SE)。</summary>
        SE = 0x08,
        /// <summary>报告 (SR)。</summary>
        SR = 0x09,
        /// <summary>操作记录 (OR)。</summary>
        OR = 0x0A,
        /// <summary>控制块 (BL)。</summary>
        BL = 0x0B,
        /// <summary>扩展定义 (EX)。</summary>
        EX = 0x0C,
        /// <summary>客户端定义 (CO)。</summary>
        CO = 0x0D,
    }

    /// <summary>IEC 61850 控制模式。</summary>
    public enum IecControlModel
    {
        /// <summary>直接控制（无确认）。</summary>
        DirectWithNormalSecurity = 0,
        /// <summary>SBO 控制（需确认）。</summary>
        SboWithNormalSecurity = 1,
        /// <summary>直接增强控制。</summary>
        DirectWithEnhancedSecurity = 2,
        /// <summary>SBO 增强控制。</summary>
        SboWithEnhancedSecurity = 3,
    }

    /// <summary>IEC 61850 报告触发选项。</summary>
    [Flags]
    public enum ReportTriggerOptions : ushort
    {
        /// <summary>无触发。</summary>
        None = 0x0000,
        /// <summary>数据变化。</summary>
        DataChanged = 0x0001,
        /// <summary>质量变化。</summary>
        QualityChanged = 0x0002,
        /// <summary>数据更新。</summary>
        DataUpdate = 0x0004,
        /// <summary>完整性扫描。</summary>
        Integrity = 0x0008,
        /// <summary>总召唤。</summary>
        GeneralInterrogation = 0x0010,
    }

    /// <summary>IEC 61850 常量。</summary>
    public static class Iec61850Constants
    {
        /// <summary>MMS 默认 TCP 端口。</summary>
        public const int DefaultMmsPort = 102;

        /// <summary>GOOSE 默认组播端口。</summary>
        public const int DefaultGoosePort = 102;

        /// <summary>ISO COTP 默认 TSEL 长度。</summary>
        public const int DefaultTselLength = 2;

        /// <summary>ISO presentation 默认 PSEL。</summary>
        public const string DefaultPsel = "00000001";

        /// <summary>ISO session 默认 SSEL。</summary>
        public const string DefaultSsel = "0001";

        /// <summary>MMS ASN.1 应用上下文名称。</summary>
        public const string MmsApplicationContext = "1.3.6.1.4.1.1";

        /// <summary>最大 MMS PDU 大小。</summary>
        public const int MaxMmsPduSize = 65000;

        /// <summary>默认报告间隔（毫秒）。</summary>
        public const int DefaultReportInterval = 1000;

        /// <summary>默认完整性扫描间隔（毫秒）。</summary>
        public const int DefaultIntegrityInterval = 5000;

        /// <summary>GOOSE 以太网类型 (0x88B8)。</summary>
        public const ushort GooseEtherType = 0x88B8;

        /// <summary>SV 以太网类型 (0x88BA)。</summary>
        public const ushort SvEtherType = 0x88BA;
    }

    /// <summary>IEC 61850 错误码。</summary>
    public static class Iec61850ErrorCodes
    {
        /// <summary>获取 IEC 61850 服务错误描述。</summary>
        public static string GetServiceErrorDescription(int errorCode)
        {
            switch (errorCode)
            {
                case 0: return "正常完成";
                case 1: return "参数不匹配 — 请求参数与对象定义不符";
                case 2: return "对象不存在 — 指定的逻辑节点或数据对象不存在";
                case 3: return "对象受限 — 访问被安全策略拒绝";
                case 4: return "对象不支持该服务";
                case 5: return "参数值无效";
                case 6: return "对象被修改 — 并发修改冲突";
                case 7: return "服务不支持 — 该 IED 不支持请求的服务";
                case 8: return "对象已存在 — 创建重复对象";
                case 9: return "对象状态冲突 — 对象状态不允许该操作";
                case 10: return "锁定 — 对象被其他客户端锁定";
                case 11: return "控制已选择 — SBO 控制已被其他客户端选中";
                case 12: return "控制被阻塞 — 控制操作被测试模式阻止";
                case 13: return "控制被拒绝 — 控制命令被现场设备拒绝";
                case 14: return "控制正在执行 — 上一个控制命令尚未完成";
                case 15: return "控制等待 — 控制等待中";
                case 16: return "文件不存在 — 指定文件不存在";
                case 17: return "文件被使用 — 文件正在被其他进程使用";
                case 18: return "文件太大 — 文件超过传输限制";
                case 19: return "连接丢失 — 与 IED 的通信连接断开";
                case 20: return "认证失败 — 安全认证未通过";
                default: return $"未知错误 ({errorCode})";
            }
        }
    }
}
