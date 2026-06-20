namespace Nexus.App.Configuration;

/// <summary>
/// 应用级可选配置。从 <c>appsettings.json</c> 的 <c>Theme</c> 节绑定。
/// <para>
/// 设计要点（WS-A）：仅承载 <b>启动期种子值</b>，用户在 Settings 页的实时选择会覆盖到
/// <c>%APPDATA%/Nexus/settings.json</c>（见 <see cref="ViewModels.SettingsViewModel"/>），
/// 并以 <see cref="ThemeManager"/> 的 <c>CurrentColor</c>/<c>CurrentForm</c> 为运行时唯一事实来源。
/// 因此本 POCO 的默认值必须等于受保护的默认主题（mono / soft）——任何分支都不得修改此默认。
/// </para>
/// <para>
/// 注：<c>appsettings.json</c> 当前 <c>Theme</c> 节使用 <c>Palette</c>/<c>Accent</c> 键
/// （WS-D 留下）。Microsoft.Extensions.Options 按属性名（大小写不敏感）绑定，<c>Color</c>/
/// <c>Form</c> 与之不匹配时会保留 POCO 默认值 mono/soft——这正是我们想要的安全行为，
/// 故刻意保留本属性名以匹配 SettingsViewModel 内部对 ThemeManager 的调用约定。
/// </para>
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "Theme";

    /// <summary>
    /// 启动期默认配色名。必须保持 <c>"mono"</c>（受保护的应用默认主题）。
    /// 运行时实际值以 <see cref="ThemeManager.CurrentColor"/> 为准。
    /// </summary>
    public string Color { get; set; } = "mono";

    /// <summary>
    /// 启动期默认款式名。必须保持 <c>"soft"</c>（受保护的应用默认主题）。
    /// 运行时实际值以 <see cref="ThemeManager.CurrentForm"/> 为准。
    /// </summary>
    public string Form { get; set; } = "soft";

    /// <summary>
    /// 启动期是否启用写入确认弹窗。默认开启（工业安全语义：默认拒绝、全程可追溯）。
    /// 运行时由 Settings 页的开关经由 <c>IWriteConfirmationService.IsConfirmationEnabled</c> 覆盖。
    /// </summary>
    public bool WriteConfirmEnabled { get; set; } = true;
}
