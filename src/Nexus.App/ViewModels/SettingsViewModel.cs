using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using Nexus;
using Nexus.App.Configuration;
using Nexus.App.Services;

namespace Nexus.App.ViewModels;

/// <summary>
/// Settings 页 ViewModel — 聚合应用级可配置项：版本信息、主题（配色/款式）、写入确认开关、
/// 日志查看器（环形缓冲快照）、连接模板只读列表。
/// <para>
/// <b>主题持久化策略（WS-A 关键约束）</b>：用户的选择写到
/// <c>%APPDATA%/Nexus/settings.json</c>（用户私有，不污染 app-owned 的 appsettings.json）；
/// 启动时由 <see cref="LoadUserTheme"/> 读取并应用到 <see cref="ThemeManager"/>。
/// <b>默认主题永远为 mono/soft</b>：文件缺失、JSON 解析失败、或"重置默认"时一律回退到 mono/soft，
/// 任何代码路径都不得把默认改成其它配色/款式。
/// </para>
/// <para>
/// <b>写入确认开关</b>：当前分支上写入确认由静态 <see cref="WriteConfirmationService.IsEnabled"/>
/// 承载（全局单值）。本 VM 的 <c>WriteConfirmEnabled</c> 属性与之双向同步：变更即写静态属性，
/// 构造期读取静态属性作为初值。WS-D 落地 <c>IWriteConfirmationService</c> 后，可把此处替换为
/// 构造注入的服务实例（保持属性签名不变，XAML 无需改动）。
/// </para>
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    // ── 受保护的应用默认主题：任何分支都不得修改这两个常量 ──────────
    public const string DefaultColor = "mono";
    public const string DefaultForm = "soft";

    private static readonly string UserSettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Nexus");

    private static readonly string UserSettingsPath = Path.Combine(UserSettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Dispatcher _dispatcher;
    private readonly ConnectionTemplateService _templates;
    private readonly IWriteConfirmationService? _writeConfirm;
    private readonly DispatcherTimer _logRefreshTimer;
    private bool _disposed;

    /// <summary>
    /// 构造。所有依赖经 DI 注入；构造期仅做同步读取，不阻塞 UI。
    /// 单测/设计时可直接 new（默认依赖会走安全回退）。
    /// </summary>
    /// <param name="appOptions">从 "Theme" 节绑定的应用选项（可为 null —— 设计时/单测）。</param>
    /// <param name="templates">连接模板服务（可为 null —— 内部 new 一个）。</param>
    /// <param name="writeConfirm">写入确认服务（WS-D DI 门面；可为 null —— 设计时/单测回退 true）。</param>
    public SettingsViewModel(
        IOptions<AppOptions>? appOptions,
        ConnectionTemplateService? templates,
        IWriteConfirmationService? writeConfirm)
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _templates = templates ?? new ConnectionTemplateService();
        _writeConfirm = writeConfirm;

        // 版本：从入口程序集的 InformationalVersion 读取（MinVer 驱动），不硬编码字面量。
        AppVersion = ResolveAppVersion();

        // 主题候选：直接暴露 ThemeManager 的静态列表（运行时不变）。
        AvailableColors = ThemeManager.AvailableColors;
        AvailableForms = ThemeManager.AvailableForms;

        // 当前主题：以 ThemeManager 为运行时唯一事实来源；构造期同步加载用户选择并应用。
        // 注意：ThemeManager.Init("mono","soft") 已在 App.OnStartup 执行过，这里仅在用户
        // 之前选过非默认主题时覆盖——LoadUserTheme 内部保证缺失/失败时保留 mono/soft。
        LoadUserTheme();
        SelectedColor = ThemeManager.CurrentColor;
        SelectedForm = ThemeManager.CurrentForm;

        // 写入确认开关：以注入的 IWriteConfirmationService 为运行时事实来源（WS-D DI 门面）。
        // AppOptions.WriteConfirmEnabled 仅作启动期种子；服务不可用时回退 true（工业安全默认启用）。
        bool seed = appOptions?.Value.WriteConfirmEnabled ?? _writeConfirm?.IsConfirmationEnabled ?? true;
        WriteConfirmEnabled = seed;
        if (_writeConfirm != null)
            _writeConfirm.IsConfirmationEnabled = seed;

        // 模板：只读视图，惰性加载磁盘模板（Load 幂等）。
        try { _templates.Load(); }
        catch { /* 模板加载失败不阻塞 Settings 页 */ }
        foreach (var t in _templates.Templates)
            Templates.Add(new TemplateRow(t.Name, t.Protocol));

        // 日志查看器：1.5s 节流的 DispatcherTimer 拉取缓冲快照，非阻塞，无 .Result/.Wait。
        _logRefreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1500),
            DispatcherPriority.Background,
            (_, _) => RefreshLogSnapshot(),
            _dispatcher)
        {
            // 仅在页面可见时跑（由 Loaded/Unloaded 切换 IsEnabled），避免后台空转。
            IsEnabled = false
        };
        RefreshLogSnapshot();
    }

    // ── 无参/可选依赖构造：设计时 & 早期回退 ──────────────────────
    public SettingsViewModel() : this(null, null, null) { }

    // ═══════════════════════ 绑定属性 ═══════════════════════

    /// <summary>应用版本（MinVer 驱动，反射读取）。形如 "1.0.0" 或 "1.0.0+sha"。</summary>
    [ObservableProperty] private string _appVersion = "0.0.0";

    /// <summary>可选配色列表（来自 ThemeManager）。</summary>
    public string[] AvailableColors { get; }

    /// <summary>可选款式列表（来自 ThemeManager）。</summary>
    public string[] AvailableForms { get; }

    /// <summary>当前选中的配色。两向绑定；变更即应用并持久化。</summary>
    [ObservableProperty] private string _selectedColor = DefaultColor;

    /// <summary>当前选中的款式。两向绑定；变更即应用并持久化。</summary>
    [ObservableProperty] private string _selectedForm = DefaultForm;

    /// <summary>写入确认弹窗开关（与静态 WriteConfirmationService.IsEnabled 双向同步）。</summary>
    [ObservableProperty] private bool _writeConfirmEnabled = true;

    /// <summary>日志查看器（只读，定时刷新自 BufferedLogger 快照）。每行已脱敏。</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>已保存的连接模板（只读视图：名称 + 协议）。</summary>
    public ObservableCollection<TemplateRow> Templates { get; } = new();

    // ═══════════════════════ 命令 ═══════════════════════

    /// <summary>手动刷新日志快照。</summary>
    [RelayCommand]
    private void RefreshLog()
    {
        RefreshLogSnapshot();
    }

    /// <summary>清空日志缓冲（不影响磁盘滚动文件）。</summary>
    [RelayCommand]
    private void ClearLog()
    {
        InvokeOnBufferedLog("Clear");
        LogLines.Clear();
    }

    /// <summary>重置主题到应用默认（mono/soft）并持久化。</summary>
    [RelayCommand]
    private void ResetTheme()
    {
        ApplyTheme(DefaultColor, DefaultForm);
        // 同步绑定属性（防止 SelectedColor 已是非法值时的 UI 不同步）。
        SelectedColor = DefaultColor;
        SelectedForm = DefaultForm;
        SaveUserTheme();
    }

    // ═══════════════════════ 局部变更应用 ═══════════════════════

    // CommunityToolkit.Mvvm 生成 OnSelectedColorChanged / OnSelectedFormChanged / OnWriteConfirmEnabledChanged。
    // 显式 partial 方法挂接实时应用 + 持久化，避免在 setter 里手写 INPC。

    partial void OnSelectedColorChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        ApplyTheme(color: value, form: null);
        SaveUserTheme();
    }

    partial void OnSelectedFormChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        ApplyTheme(color: null, form: value);
        SaveUserTheme();
    }

    partial void OnWriteConfirmEnabledChanged(bool value)
    {
        // 同步到注入的写入确认服务（WS-D DI 门面）；服务不可用时（设计时）仅保留绑定属性。
        if (_writeConfirm != null)
            _writeConfirm.IsConfirmationEnabled = value;
    }

    /// <summary>
    /// 把配色/款式应用到 <see cref="ThemeManager"/>（null 表示保持当前）。
    /// 捕获异常：主题字典可能因资源损坏加载失败，失败不破坏 UI（保留上一个有效主题）。
    /// </summary>
    private static void ApplyTheme(string? color, string? form)
    {
        try
        {
            if (!string.IsNullOrEmpty(color))
                ThemeManager.ApplyColor(color);
            if (!string.IsNullOrEmpty(form))
                ThemeManager.ApplyForm(form);
        }
        catch
        {
            // 主题字典加载失败 — 保留 ThemeManager 内部上一个有效状态，不向上抛。
        }
    }

    // ═══════════════════════ 用户主题持久化 ═══════════════════════

    /// <summary>
    /// 启动期：从 <c>%APPDATA%/Nexus/settings.json</c> 读取用户上次的主题选择并应用。
    /// 文件缺失 / JSON 解析失败 / 字段非法 时一律保留 mono/soft（即 ThemeManager 当前状态），
    /// 不抛异常、不写错误主题。
    /// </summary>
    private static void LoadUserTheme()
    {
        try
        {
            if (!File.Exists(UserSettingsPath)) return;
            var json = File.ReadAllText(UserSettingsPath);
            var doc = JsonSerializer.Deserialize<UserSettingsFile>(json, JsonOpts);
            if (doc == null) return;

            // 仅当 ThemeManager 列表中存在该配色/款式时才应用，防止用户手改 JSON 注入无效值。
            string color = doc.Color ?? string.Empty;
            string form = doc.Form ?? string.Empty;
            if (!string.IsNullOrEmpty(color) && Array.IndexOf(ThemeManager.AvailableColors, color) >= 0)
                ApplyTheme(color: color, form: null);
            if (!string.IsNullOrEmpty(form) && Array.IndexOf(ThemeManager.AvailableForms, form) >= 0)
                ApplyTheme(color: null, form: form);
        }
        catch
        {
            // 读取失败 — 保留默认 mono/soft，不向上抛。
        }
    }

    /// <summary>
    /// 把当前 ThemeManager 的配色/款式持久化到 <c>%APPDATA%/Nexus/settings.json</c>。
    /// 写失败静默（磁盘只读/无权限），下次启动回退到默认 mono/soft——不影响 UI。
    /// </summary>
    private static void SaveUserTheme()
    {
        try
        {
            Directory.CreateDirectory(UserSettingsDir);
            var file = new UserSettingsFile
            {
                Color = ThemeManager.CurrentColor,
                Form = ThemeManager.CurrentForm
            };
            var json = JsonSerializer.Serialize(file, JsonOpts);
            File.WriteAllText(UserSettingsPath, json);
        }
        catch
        {
            // 持久化失败不破坏当前会话；运行时主题已应用，仅下次启动回退到默认。
        }
    }

    // ═══════════════════════ 日志快照拉取 ═══════════════════════

    /// <summary>
    /// 日志可用性提示：当前构建是否存在全局缓冲日志器。
    /// <c>"available"</c> = 已发现 <c>AppLoggerFactory.BufferedLog</c>；其它值时为说明文本。
    /// 用于在 UI 上告知用户日志查看器的运行状态。
    /// </summary>
    [ObservableProperty] private string _logAvailability = "checking…";

    /// <summary>
    /// 把全局缓冲日志器的快照同步到 <see cref="LogLines"/>。
    /// 在 UI 线程执行（由 DispatcherTimer / 命令调用）。
    /// <para>
    /// <b>反射发现</b>：本分支可能尚未合入 WS-D 的 <c>AppLoggerFactory</c>。为避免硬依赖，
    /// 通过反射按类型名 <c>Nexus.App.Services.AppLoggerFactory</c> + 属性 <c>BufferedLog</c>
    /// 发现实例。WS-D 合入后无需改本文件即可点亮；当前分支返回空列表 + 状态说明。
    /// </para>
    /// </summary>
    private void RefreshLogSnapshot()
    {
        if (_disposed) return;
        object? buffered = ResolveBufferedLogger();
        if (buffered == null)
        {
            LogAvailability = "当前构建未注册全局缓冲日志器（WS-D 未合入）。每协议页仍各自维护本地日志。";
            if (LogLines.Count > 0) LogLines.Clear();
            return;
        }

        LogAvailability = "available";

        System.Collections.Generic.List<string>? snapshotStrings = InvokeGetSnapshot(buffered);
        if (snapshotStrings == null) return; // 快照失败不破坏列表。

        // 增量替换：仅当尾行变化或数量不一致时重建，避免每 tick 触发 CollectionChanged 抖动。
        if (LogLines.Count == snapshotStrings.Count &&
            (snapshotStrings.Count == 0 || LogLines[LogLines.Count - 1] == snapshotStrings[snapshotStrings.Count - 1]))
            return;

        LogLines.Clear();
        foreach (var line in snapshotStrings)
            LogLines.Add(line);
    }

    /// <summary>惰性缓存反射发现的 <c>AppLoggerFactory</c> 类型（未找到则缓存 <c>null</c>，避免重复反射）。</summary>
    private static Type? _cachedLoggerFactoryType;
    private static bool _loggerFactoryResolved;
    private static readonly object _loggerFactoryLock = new object();

    /// <summary>
    /// 反射解析 <c>Nexus.App.Services.AppLoggerFactory</c> 的静态 <c>BufferedLog</c> 属性值。
    /// 未找到类型 / 属性 / 取值异常均返回 null（缓存结果，避免每次刷新都反射）。
    /// </summary>
    private static object? ResolveBufferedLogger()
    {
        // 双检锁 + 已解析标志：首次解析后无锁快速路径。
        if (_loggerFactoryResolved && _cachedLoggerFactoryType == null) return null;
        if (!_loggerFactoryResolved)
        {
            lock (_loggerFactoryLock)
            {
                if (!_loggerFactoryResolved)
                {
                    _cachedLoggerFactoryType = Type.GetType(
                        "Nexus.App.Services.AppLoggerFactory, Nexus.App",
                        throwOnError: false);
                    _loggerFactoryResolved = true;
                }
            }
        }

        var type = _cachedLoggerFactoryType;
        if (type == null) return null;
        try
        {
            var prop = type.GetProperty("BufferedLog", BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在缓冲日志器实例上反射调用 <c>GetSnapshot()</c>，返回每行的 <c>ToString()</c> 文本列表。</summary>
    private static System.Collections.Generic.List<string>? InvokeGetSnapshot(object buffered)
    {
        try
        {
            var method = buffered.GetType().GetMethod("GetSnapshot", Type.EmptyTypes);
            if (method == null) return null;
            var raw = method.Invoke(buffered, null) as System.Collections.IEnumerable;
            if (raw == null) return null;
            var list = new System.Collections.Generic.List<string>();
            foreach (var item in raw)
                list.Add(item?.ToString() ?? string.Empty);
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在缓冲日志器实例上反射调用无参方法（如 <c>Clear</c>）。失败静默。</summary>
    private static void InvokeOnBufferedLog(string methodName)
    {
        try
        {
            var buffered = ResolveBufferedLogger();
            if (buffered == null) return;
            var method = buffered.GetType().GetMethod(methodName, Type.EmptyTypes);
            method?.Invoke(buffered, null);
        }
        catch
        {
            // 清空失败不破坏 UI。
        }
    }

    // ═══════════════════════ 版本反射 ═══════════════════════

    /// <summary>
    /// 从入口程序集反射读取版本。优先 <c>AssemblyInformationalVersionAttribute</c>
    /// （MinVer 注入，含 prerelease/sha 后缀）；缺失时回退到 <c>AssemblyVersion</c>；
    /// 全部失败时返回占位 "0.0.0"（绝不硬编码业务版本字面量）。
    /// </summary>
    private static string ResolveAppVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(App).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                // InformationalVersion 可能含 git metadata（如 "1.0.0+abc"）；只保留 + 前。
                string v = info.InformationalVersion.Trim();
                int plus = v.IndexOf('+');
                return plus > 0 ? v.Substring(0, plus) : v;
            }
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    // ═══════════════════════ 生命周期 ═══════════════════════

    /// <summary>页面 Loaded 时开启定时刷新（由 code-behind 调用）。</summary>
    public void OnPageActivated()
    {
        _logRefreshTimer.IsEnabled = true;
        RefreshLogSnapshot();
    }

    /// <summary>页面 Unloaded 时停止定时刷新（由 code-behind 调用）。</summary>
    public void OnPageDeactivated()
    {
        _logRefreshTimer.IsEnabled = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logRefreshTimer.Stop();
    }

    // ═══════════════════════ 内部类型 ═══════════════════════

    /// <summary>连接模板只读行（名称 + 协议）。</summary>
    public sealed class TemplateRow
    {
        public string Name { get; }
        public string Protocol { get; }
        public TemplateRow(string name, string protocol)
        {
            Name = name;
            Protocol = protocol;
        }
    }

    /// <summary>%APPDATA%/Nexus/settings.json 的序列化结构。</summary>
    private sealed class UserSettingsFile
    {
        [JsonPropertyName("color")]
        public string? Color { get; set; }

        [JsonPropertyName("form")]
        public string? Form { get; set; }
    }
}
