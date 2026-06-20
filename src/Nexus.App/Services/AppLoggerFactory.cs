using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexus;
using Nexus.App.Configuration;

namespace Nexus.App.Services;

/// <summary>
/// 应用级日志工厂：把 <see cref="Nexus.FileLogger"/>（按大小滚动）与
/// <see cref="Nexus.BufferedLogger"/>（环形缓冲，供 Wave 2 日志查看器）组合成
/// <see cref="Nexus.MultiplexLogger"/>，并注入到 DI 容器作为应用唯一 <see cref="ILogger"/>。
/// 所有写出的文本在落盘/入缓冲前先经 <see cref="SecretRedactor.Redact"/> 脱敏。
/// </summary>
/// <remarks>
/// <b>线程安全</b>：底层两个 logger 各自带锁；本类只暴露静态只读单例与一次性配置方法。
/// <b>不阻塞 UI 线程</b>：构造期仅做一次目录创建（很快），文件写入由 FileLogger 内部加锁同步——
/// 日志量本身很小（调试器场景），无需后台队列。
/// </remarks>
public static class AppLoggerFactory
{
    private static readonly object _initSync = new object();
    private static bool _configured;
    private static BufferedLogger? _bufferedLogger;

    /// <summary>
    /// 全局环形缓冲日志器（供 Wave 2 Settings 页日志查看器绑定）。
    /// 在 <see cref="ConfigureLogging(IServiceCollection)"/> 真正解析出实例前为 null；
    /// 调用 <see cref="CreateLogger(LoggingOptions)"/>（DI 注册时会被触发）后即非空。
    /// </summary>
    public static BufferedLogger? BufferedLog => _bufferedLogger;

    /// <summary>
    /// 配置日志：绑定 <see cref="LoggingOptions"/>，注册一个工厂到 DI，
    /// 由 DI 在首次解析 <c>ILogger</c> 时调用 <see cref="CreateLogger(LoggingOptions)"/>
    /// 构建 <see cref="Nexus.FileLogger"/> + <see cref="Nexus.BufferedLogger"/>，
    /// 并包一层 <see cref="SecretRedactor"/>。以 <c>Singleton</c> 形式注册 <c>ILogger</c>。
    /// </summary>
    /// <param name="services">Host <c>ConfigureServices</c> 回调里的 <see cref="IServiceCollection"/>。</param>
    /// <remarks>
    /// 此方法幂等：重复调用只首次生效，避免重复注册 ILogger 工厂。
    /// 由 WS-D 在 <c>App.xaml.cs</c> 的 <c>ConfigureServices</c> 里调用——本类不会自调用。
    /// </remarks>
    public static void ConfigureLogging(IServiceCollection services)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        lock (_initSync)
        {
            if (_configured)
                return;

            services.AddOptions<LoggingOptions>()
                    .BindConfiguration(LoggingOptions.SectionName);

            services.AddSingleton<ILogger>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<LoggingOptions>>().Value;
                return CreateLogger(options);
            });

            _configured = true;
        }
    }

    /// <summary>
    /// 仅供单元测试 / 早期启动（DI 尚未就绪）使用：用给定 <see cref="LoggingOptions"/> 直接构建 logger。
    /// 返回的实例与 DI 解析到的实例<b>不同</b>——生产路径应走 <c>ConfigureLogging</c>。
    /// 该方法会更新 <see cref="BufferedLog"/> 静态字段，便于 Wave 2 在 DI 未就绪的早期也能取到缓冲器。
    /// </summary>
    internal static ILogger CreateLogger(LoggingOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        string dir = string.IsNullOrWhiteSpace(options.Directory)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : options.Directory;
        // 相对路径按 BaseDirectory 解析，避免被当前目录（可能是 System32）意外带入。
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(AppContext.BaseDirectory, dir);

        Directory.CreateDirectory(dir);

        string basePath = Path.Combine(dir,
            string.IsNullOrWhiteSpace(options.FileName) ? "nexus.log" : options.FileName);

        long maxSize = options.MaxFileSizeBytes > 0 ? options.MaxFileSizeBytes : 1 * 1024 * 1024;
        int maxFiles = options.MaxFiles > 0 ? options.MaxFiles : 10;
        int bufCap = options.BufferCapacity > 0 ? options.BufferCapacity : 1000;

        var fileLogger = new FileLogger(basePath, maxSize, maxFiles);
        var buffered = new BufferedLogger(bufCap);
        var minimum = options.MinimumLevel;

        _bufferedLogger = buffered;
        return new RedactingMultiplexer(minimum, fileLogger, buffered);
    }

    /// <summary>
    /// 组合 logger：先按最低级别过滤，再对消息做 <see cref="SecretRedactor.Redact"/>，
    /// 最后分发到 FileLogger + BufferedLogger。包这一层是为了确保即使调用方直接传明文，
    /// 落盘/入缓冲的也一定是脱敏后的文本。
    /// </summary>
    private sealed class RedactingMultiplexer : ILogger
    {
        private readonly LogLevel _minimum;
        private readonly MultiplexLogger _inner;

        public RedactingMultiplexer(LogLevel minimum, params ILogger[] sinks)
        {
            _minimum = minimum;
            _inner = new MultiplexLogger(sinks);
        }

        public void Log(LogLevel level, string message)
        {
            if (level < _minimum) return;
            _inner.Log(level, SafeRedact(message));
        }

        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);

        private static string SafeRedact(string message)
        {
            if (string.IsNullOrEmpty(message)) return message ?? string.Empty;
            try { return SecretRedactor.Redact(message); }
            catch { return message; } // 脱敏失败不应吞掉日志本身
        }
    }
}

// ============================================================================
// == WS-D 专用：crash-log 重写规格（不要把本块当代码执行） ====================
// ============================================================================
//
// 背景：当前 App.xaml.cs 的 `WriteCrashLog(source, errorObject)`（~190-205 行）
// 直接 `File.AppendAllText(AppContext.BaseDirectory + "crash.log", entry)`：
//   - 写到安装目录（Program Files 下无写权限 → 静默失败）
//   - 明文落盘（含堆栈里的连接串、IP、password=... 等）
//   - 与结构化日志脱节，日志查看器看不到崩溃
//
// 重写要求（由 WS-D 在 App.xaml.cs 落地）：
//
// 1. 删除 WriteCrashLog 内部的 File.AppendAllText 调用。
//
// 2. 经由应用级 ILogger 写崩溃。崩溃可能发生在 DI 容器完全就绪前（OnStartup 早期），
//    需要回退实例：
//
//        private static ILogger? _crashLogger;
//        private static ILogger CrashLogger =>
//            _crashLogger ??= (_host?.Services.GetService(typeof(ILogger)) as ILogger)
//                              ?? AppLoggerFactory.CreateLogger(new LoggingOptions());
//
//    注意：AppLoggerFactory.CreateLogger 是 internal，需要 WS-D 在同程序集内调用（App.xaml.cs
//    与 AppLoggerFactory 同属 Nexus.App，可见性 OK）。
//
// 3. 把 detail 文本经 SecretRedactor.Redact 后再传给 logger：
//
//        string detail = errorObject is Exception ex
//            ? $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}"
//            : errorObject?.ToString() ?? "(null)";
//        string safe = Nexus.App.Services.SecretRedactor.Redact(detail);
//        CrashLogger.Error($"[Crash:{source}] {safe}");
//
// 4. 保留最外层 try/catch：CrashLogger 内部 FileLogger 已吞异常，但 Redact /
//    早期 logger 构造仍可能抛；最外层 try/catch 不能删（"彻底放弃"原则）。
//    catch 块可再 fallback 到原 AppendAllText(BaseDirectory, "crash.log") 作为最后兜底——
//    但该 fallback 文本也应先经 SecretRedactor.Redact。
//
// 5. 不要再单独维护 "crash.log" 文件名——它现已合入 nexus.log 滚动文件序列。
//    事后排查时直接到 %LOCALAPPDATA%/Nexus/logs/nexus.log.* 查 [ERROR][Crash:...] 行。
//
// 6. DispatcherUnhandledException / AppDomain.UnhandledException /
//    TaskScheduler.UnobservedTaskException 三个入口都走同一个 CrashLogger.Error，
//    源标识分别保留 "DispatcherUnhandledException" /
//    "AppDomain.UnhandledException (terminating)" / "TaskScheduler.UnobservedTaskException"。
//
// 7. IsFatal / ShowErrorSafely 逻辑不变——只动 WriteCrashLog。
// ============================================================================
