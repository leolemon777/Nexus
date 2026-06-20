using Nexus;

namespace Nexus.App.Configuration;

/// <summary>
/// 文件 + 缓冲日志相关配置。从 <c>appsettings.json</c> 的 <c>Logging</c> 节绑定。
/// 通过 <c>IOptions&lt;LoggingOptions&gt;</c> 注入到 <see cref="Nexus.App.Services.AppLoggerFactory"/>。
/// </summary>
public sealed class LoggingOptions
{
    public const string SectionName = "Logging";

    /// <summary>
    /// 日志文件所在目录。默认 <c>%LOCALAPPDATA%/Nexus/logs</c>。
    /// 设为相对路径时按 <c>AppContext.BaseDirectory</c> 解析。
    /// </summary>
    public string Directory { get; set; } = ResolveDefaultDirectory();

    /// <summary>日志文件名（不含目录）。默认 <c>nexus.log</c>。</summary>
    public string FileName { get; set; } = "nexus.log";

    /// <summary>单个日志文件最大字节数，超出后滚动。默认 1 MB。</summary>
    public long MaxFileSizeBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>保留的最大滚动文件数（含当前文件）。默认 10。</summary>
    public int MaxFiles { get; set; } = 10;

    /// <summary>环形缓冲日志保留条数（供 Wave 2 日志查看器）。默认 1000。</summary>
    public int BufferCapacity { get; set; } = 1000;

    /// <summary>
    /// 最低记录级别；低于此级别的日志被丢弃。<see cref="LogLevel"/> 顺序：
    /// Debug &lt; Info &lt; Warn &lt; Error。默认 <see cref="LogLevel.Debug"/>（全量）。
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    private static string ResolveDefaultDirectory()
    {
        // %LOCALAPPDATA% 在 Windows 服务/无 profile 场景可能为空，退回 BaseDirectory。
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            return System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
        return System.IO.Path.Combine(localAppData, "Nexus", "logs");
    }
}
