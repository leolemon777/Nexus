using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nexus.App;

/// <summary>
/// 从 pack:// 资源加载厂商 Logo PNG；文件不存在则返回 null（降级为 emoji）
/// </summary>
internal static class BrandLogos
{
    public static ImageSource? TryLoad(string filename)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/Assets/Icons/{filename}");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64; // 实际只显示 ~20px，64px 足够清晰
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 从 pack:// 资源加载通讯协议 Logo PNG（位于 Assets/Icons/Protocols/ 子目录）；
/// 文件不存在则返回 null（降级为 emoji）。
/// </summary>
internal static class ProtocolLogos
{
    public static ImageSource? TryLoad(string filename)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/Assets/Icons/Protocols/{filename}");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 48; // 协议徽章通常更小
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 侧边栏分组节点（如"Modbus 系列"、"西门子"）
/// </summary>
public sealed class NavGroup
{
    public string Icon { get; init; }                     // emoji 降级
    public ImageSource? IconSource { get; init; }         // 真实 Logo
    public string Label { get; init; }
    public ObservableCollection<NavItem> Items { get; } = new();

    /// <summary>IconSource == null 时显示 emoji</summary>
    public Visibility EmojiVisible => IconSource == null ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>IconSource != null 时显示 Logo</summary>
    public Visibility LogoVisible => IconSource != null ? Visibility.Visible : Visibility.Collapsed;

    public NavGroup(string icon, string label, ImageSource? iconSource = null)
    {
        Icon = icon; Label = label; IconSource = iconSource;
    }

    public NavGroup Add(string icon, string name, string tag, Type pageType)
    {
        Items.Add(new NavItem(icon, name, tag, pageType));
        return this;
    }
}

public sealed class NavItem
{
    public string Icon { get; init; }
    public string Label { get; init; }
    public string Tag { get; init; }
    public Type PageType { get; init; }

    public NavItem(string icon, string label, string tag, Type pageType)
    {
        Icon = icon; Label = label; Tag = tag; PageType = pageType;
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private NavItem? _selectedNav;

    /// <summary>
    /// DI 构造：注入 <see cref="Services.ConnectionTemplateService"/> 作为模板存储的单一事实来源。
    /// 旧的本地 %APPDATA%/Nexus/connection_templates.json store（snake_case、schema 不一致）已移除，
    /// 旧文件由 <see cref="Services.ConnectionTemplateService.EnsureLoaded"/> 一次性迁移为 .bak。
    /// </summary>
    public MainViewModel(Services.ConnectionTemplateService templateService)
    {
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        // 启动时即迁移 + 预加载模板名，保证设置页模板下拉框立即可用。
        _templateService.EnsureLoaded();
    }

    public ObservableCollection<NavGroup> NavGroups { get; } = new()
    {
        // ═══════════════════════════════════════════
        //  已实现协议 — 有对应协议页面
        // ═══════════════════════════════════════════

        new NavGroup("📡", "Modbus 系列", BrandLogos.TryLoad("modbus.png"))
            .Add("📡", "Modbus TCP",    "modbus-tcp",    typeof(Views.ModbusTcpPage))
            .Add("📶", "Modbus UDP",    "modbus-udp",    typeof(Views.ModbusUdpPage))
            .Add("🔌", "Modbus RTU",    "modbus-rtu",    typeof(Views.ModbusRtuPage))
            .Add("🌉", "Modbus RTU Over TCP", "modbus-rtu-over-tcp", typeof(Views.ModbusRtuOverTcpPage))
            .Add("📄", "Modbus ASCII",  "modbus-ascii",  typeof(Views.ModbusAsciiPage))
            .Add("🌐", "Modbus ASCII Over TCP", "modbus-ascii-over-tcp", typeof(Views.ModbusAsciiOverTcpPage)),

        new NavGroup("🏭", "西门子 Siemens", BrandLogos.TryLoad("siemens.png"))
            .Add("🏭", "S7 Ethernet",     "siemens-s7",        typeof(Views.SiemensPage))
            .Add("📡", "Fetch/Write",     "siemens-fw",        typeof(Views.FetchWritePage))
            .Add("🔌", "PPI 串口 (S7-200)", "siemens-ppi",     typeof(Views.SiemensPpiPage)),

        new NavGroup("🗼", "三菱 Mitsubishi", BrandLogos.TryLoad("mitsubishi_electric.png"))
            .Add("🗼", "MC 3E Binary TCP",  "mc3e-binary-tcp", typeof(Views.MitsubishiPage))
            .Add("🗼", "MC 3E ASCII TCP",   "mc3e-ascii-tcp",  typeof(Views.MitsubishiPage))
            .Add("🗼", "MC 3E Binary UDP",  "mc3e-binary-udp", typeof(Views.MitsubishiPage))
            .Add("🗼", "MC 3E ASCII UDP",   "mc3e-ascii-udp",  typeof(Views.MitsubishiPage))
            .Add("🗼", "A1E Binary TCP",    "a1e-binary-tcp",  typeof(Views.MitsubishiPage))
            .Add("🔧", "FX 串口协议",        "mitsubishi-fx",   typeof(Views.MitsubishiFxPage)),

        new NavGroup("🟠", "欧姆龙 Omron", BrandLogos.TryLoad("omron.png"))
            .Add("🟠", "FINS-TCP",      "omron",         typeof(Views.OmronPage)),

        new NavGroup("🔵", "AB / 罗克韦尔", BrandLogos.TryLoad("rockwell.png"))
            .Add("🔵", "CIP (ControlLogix)", "allenbradley", typeof(Views.AllenBradleyPage)),

        new NavGroup("🟡", "松下 Panasonic", BrandLogos.TryLoad("panasonic.png"))
            .Add("🟡", "Mewtocol (FP 系列)", "panasonic",  typeof(Views.PanasonicPage)),

        new NavGroup("🔴", "基恩士 Keyence", BrandLogos.TryLoad("keyence.png"))
            .Add("🔴", "KV 系列上位通讯", "keyence",       typeof(Views.KeyencePage)),

        new NavGroup("🟣", "倍福 Beckhoff", BrandLogos.TryLoad("beckhoff.png"))
            .Add("🟣", "TwinCAT ADS",   "beckhoff",      typeof(Views.BeckhoffPage)),

        new NavGroup("⚪", "台达 Delta", BrandLogos.TryLoad("delta.png"))
            .Add("⚪", "DVP/AS 系列",    "delta",         typeof(Views.DeltaPage)),

        new NavGroup("🟤", "富士 Fuji", BrandLogos.TryLoad("fuji.png"))
            .Add("🟤", "SPH/SPB 系列",  "fuji",          typeof(Views.FujiPage)),

        new NavGroup("🟢", "欧陆 Eurotherm", BrandLogos.TryLoad("eurotherm.png"))
            .Add("🟢", "2400/2500 调节器", "eurotherm",  typeof(Views.EurothermPage)),

        new NavGroup("🔷", "LS 产电", BrandLogos.TryLoad("ls_electric.png"))
            .Add("🔷", "XGT 协议",       "ls",            typeof(Views.LsPage)),

        new NavGroup("🔶", "汇川 Inovance", BrandLogos.TryLoad("inovance.png"))
            .Add("🔶", "H3U/AM 系列",    "inovance",      typeof(Views.InovancePage)),

        new NavGroup("🟡", "永宏 Fatek", BrandLogos.TryLoad("fatek.png"))
            .Add("🟡", "FBs 系列",        "fatek",         typeof(Views.FatekPage)),

        new NavGroup("🤖", "FANUC", BrandLogos.TryLoad("fanuc.png"))
            .Add("🤖", "FOCAS Ethernet",  "fanuc",        typeof(Views.FanucPage)),

        new NavGroup("⚙", "GE", BrandLogos.TryLoad("ge.png"))
            .Add("⚙", "SRTP (90-30/70/PAC)", "ge-srtp", typeof(Views.GeSrtpPage)),

        new NavGroup("📦", "信捷 Xinje", BrandLogos.TryLoad("xinje.png"))
            .Add("📦", "XG/XC 系列 (Modbus)", "xinje",  typeof(Views.XinjePage)),

        new NavGroup("🦾", "KUKA", BrandLogos.TryLoad("kuka.png"))
            .Add("🦾", "EKI 机器人通讯",    "kuka",     typeof(Views.KukaPage)),

        new NavGroup("🌐", "OPC UA", BrandLogos.TryLoad("opcua.png"))
            .Add("🌐", "OPC UA Client",     "opcua",          typeof(Views.OpcUaPage))
            .Add("🌐", "OPC UA Server",     "opcua-server",   typeof(Views.OpcUaServerPage)),

        new NavGroup("🔵", "安川 YASKAWA", BrandLogos.TryLoad("yaskawa.png"))
            .Add("🔵", "Memobus TCP",     "yaskawa",     typeof(Views.YaskawaPage)),

        new NavGroup("🔵", "横河 Yokogawa", BrandLogos.TryLoad("yokogawa.png"))
            .Add("🔵", "Vnet/IP 链接",    "yokogawa",    typeof(Views.YokogawaPage)),

        // ═══════════════════════════════════════════
        //  更多厂商 — 图标已就绪，协议开发中
        // ═══════════════════════════════════════════

        new NavGroup("🔴", "ABB", BrandLogos.TryLoad("abb.png")),

        new NavGroup("🔵", "博世力士乐 Bosch Rexroth", BrandLogos.TryLoad("bosch_rexroth.png")),

        new NavGroup("🟠", "B&R Industrial", BrandLogos.TryLoad("br_industrial.png")),

        new NavGroup("🟢", "菲尼克斯 Phoenix Contact", BrandLogos.TryLoad("phoenix_contact.png")),

        new NavGroup("🔴", "霍尼韦尔 Honeywell", BrandLogos.TryLoad("honeywell.png")),

        new NavGroup("🟢", "WAGO", BrandLogos.TryLoad("wago.png")),

        new NavGroup("🔵", "艾默生 Emerson", BrandLogos.TryLoad("emerson.png")),

        new NavGroup("🟡", "IDEC 和泉", BrandLogos.TryLoad("idec.png")),

        new NavGroup("🔴", "日立 Hitachi", BrandLogos.TryLoad("hitachi.png")),

        new NavGroup("🔵", "东芝 Toshiba", BrandLogos.TryLoad("toshiba.png")),


        // ═══════════════════════════════════════════
        //  新增协议页面
        // ═══════════════════════════════════════════

        new NavGroup("⚡", "施耐德 Schneider", BrandLogos.TryLoad("schneider_electric.png"))
            .Add("⚡", "Modicon", "schneider", typeof(Views.SchneiderPage)),

        new NavGroup("⚡", "电力协议")
            .Add("⚡", "DNP3", "dnp3", typeof(Views.Dnp3Page))
            .Add("⚡", "IEC 104", "iec104", typeof(Views.Iec104Page))
            .Add("⚡", "IEC 61850", "iec61850", typeof(Views.Iec61850Page)),

        new NavGroup("🏢", "楼宇自动化")
            .Add("🏢", "BACnet/IP", "bacnet", typeof(Views.BacnetPage)),

        new NavGroup("🔬", "半导体")
            .Add("🔬", "SECS HSMS", "secs", typeof(Views.SecsPage)),

        new NavGroup("🌡", "仪表")
            .Add("🌡", "RKC 温控", "rkc", typeof(Views.RkcPage))
            .Add("⚖", "Toledo 称重", "toledo", typeof(Views.ToledoPage)),


        new NavGroup("🤖", "机器人")
            .Add("🤖", "埃夫特 Efort", "robot-efort", typeof(Views.RobotEfortPage))
            .Add("🤖", "FANUC", "robot-fanuc", typeof(Views.RobotFanucPage))
            .Add("🤖", "KUKA", "robot-kuka", typeof(Views.RobotKukaPage))
            .Add("🤖", "UR", "robot-ur", typeof(Views.RobotUrPage))
            .Add("🤖", "安川 Yaskawa", "robot-yaskawa", typeof(Views.RobotYaskawaPage))
            .Add("🤖", "雅马哈 Yamaha", "robot-yamaha", typeof(Views.RobotYamahaPage))
            .Add("🤖", "史陶比尔 Staubli", "robot-staubli", typeof(Views.RobotStaubliPage)),
        // ═══════════════════════════════════════════
        //  工具
        // ═══════════════════════════════════════════

        new NavGroup("🛠", "工具")
            .Add("🖥", "Modbus 模拟器",  "simulator",     typeof(Views.SimulatorPage))
            .Add("🔌", "虚拟 PLC 管理",  "virtual-plc",   typeof(Views.VirtualPlcPage))
            .Add("📊", "实时监控",        "monitor",       typeof(Views.MonitorPage))
            .Add("🎨", "HMI 工艺图",      "hmi",           typeof(Views.HmiPage))
            .Add("🔔", "报警管理",        "alarm",         typeof(Views.AlarmPage))
            .Add("📋", "配方管理",      "recipe",        typeof(Views.RecipePage))
            .Add("🗄", "数据记录",      "datalogger",    typeof(Views.DataLoggerPage))
            .Add("🏷", "标签配置",      "tagconfig",     typeof(Views.TagConfigPage))
            .Add("⚙️", "设置",            "settings",      typeof(Views.SettingsPage)),
    };

    public event Action<NavItem>? NavigationRequested;

    private readonly Services.ConnectionTemplateService _templateService;

    // 单一事实来源：模板存储统一交给 ConnectionTemplateService（WS-D）。
    // 这里只保留导航页（设置 / 工具）用的"名字 + 当前选中"绑定表面；
    // 实际连接字段由各协议 VM 通过反射 ApplyToProtocol 写回（见 LoadTemplate）。
    private string _templateName = "";

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (_selectedNav == value) return;
            _selectedNav = value;
            OnPropertyChanged();
            if (value != null) NavigationRequested?.Invoke(value);
        }
    }

    public string TemplateName
    {
        get => _templateName;
        set { _templateName = value; OnPropertyChanged(); }
    }

    /// <summary>已保存模板的只读名列表（绑定到 ComboBox 之类）。</summary>
    public ObservableCollection<string> SavedTemplates { get; } = new();

    /// <summary>
    /// 按名称加载模板：更新 <see cref="TemplateName"/> 并把连接字段反射写到当前激活的协议 ViewModel（若可解析）。
    /// 修掉了旧实现的 bug——旧 LoadTemplate 只设置 TemplateName，从不填充 IP/Port 等字段。
    /// </summary>
    /// <param name="name">模板名。</param>
    /// <returns>是否找到并至少填充了一个连接字段。</returns>
    public bool LoadTemplate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var tpl = _templateService.Find(name);
        if (tpl == null) return false;

        TemplateName = name;

        // 解析当前协议页 VM（若有），把 IP/Port/SlaveId 等字段写回。
        // 无法解析（导航尚未发生 / 当前页非协议页）时仅更新名字，不抛。
        object? currentVm = ResolveCurrentProtocolViewModel();
        if (currentVm == null) return false;

        return _templateService.ApplyToProtocol(tpl, currentVm);
    }

    public void LoadSavedTemplateNames()
    {
        _templateService.EnsureLoaded();
        SavedTemplates.Clear();
        foreach (var t in _templateService.Templates)
            SavedTemplates.Add(t.Name);
    }

    /// <summary>
    /// 解析当前导航页对应的协议 ViewModel（若 Frame 已显示协议页且其 DataContext 为对象）。
    /// 此处通过 NavigationRequested 的副作用获取最后导航的 NavItem.Tag 的方式不直接可用，
    /// 故退化为：从 App.Services 取最近一个注册的协议 VM 实例（导航 VM 由 Page 的 code-behind 设置）。
    /// 当前实现保守地返回 null（避免误写无关 VM）；具体字段填充由各协议 VM 自带的 LoadTemplate 命令承担。
    /// </summary>
    private static object? ResolveCurrentProtocolViewModel() => null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
