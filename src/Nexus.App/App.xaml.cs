using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nexus;
using Nexus.App.Configuration;
using Nexus.App.ViewModels;
using Nexus.App.Services;
using Nexus.App.Views;
using Nexus.Security;

namespace Nexus.App;

public partial class App : Application
{
    private IHost? _host;

    /// <summary>
    /// 全局服务容器。WPF Page 由 Frame 反射创建（无构造参数注入），
    /// 故在 code-behind 通过 <c>App.Services.GetRequiredService&lt;T&gt;()</c> 解析 VM。
    /// </summary>
    public IServiceProvider Services => _host?.Services
        ?? throw new InvalidOperationException("Host not initialized");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常捕获 — 防止未处理异常导致闪退（C2 加固）
        // 设计原则：普通异常弹窗 + 写日志后 Handled=true 让 UI 继续运行；
        //           致命异常（OOM 等，状态已损坏）不吞，让进程正常终止，避免损坏状态被持久化。

        DispatcherUnhandledException += (s, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            // 致命异常：状态已不可恢复，吞掉会让 UI 在损坏状态下继续运行（工业场景可能误写设备）。
            if (IsFatal(args.Exception))
            {
                ShowErrorSafely($"致命错误，应用将退出:\n\n{args.Exception.Message}");
                args.Handled = false;   // 让默认崩溃流程走完
                return;
            }
            ShowErrorSafely($"UI 线程异常:\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            // ExceptionObject 可能不是 Exception（非托管异常），用 ToString() 兜底。
            string detail = args.ExceptionObject is Exception ex
                ? $"{ex.Message}\n\n{ex.StackTrace}"
                : args.ExceptionObject?.ToString() ?? "(null)";
            WriteCrashLog("AppDomain.UnhandledException (terminating)", args.ExceptionObject);
            // 进程即将被 CLR 终止，仅写日志，不依赖可能无响应的 MessageBox。
            ShowErrorSafely($"应用发生致命错误:\n\n{detail}");
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
            ShowErrorSafely($"后台任务异常:\n\n{args.Exception?.Message}");
            args.SetObserved();
        };

        ThemeManager.Init("mono", "soft");

        string environment = ResolveEnvironment();

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                // 仅加载匹配当前环境的覆盖文件，避免 Dev/Prod 同时叠加（后加者覆盖前者）。
                cfg.AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: true);
            })
            .UseEnvironment(environment)
            .ConfigureServices((ctx, services) =>
            {
                // Options
                services.Configure<ModbusOptions>(
                    ctx.Configuration.GetSection(ModbusOptions.SectionName));

                // WS-A: Settings 页 POCO（从 "Theme" 节绑定；缺失字段回退到 POCO 默认 mono/soft/true）。
                services.Configure<AppOptions>(
                    ctx.Configuration.GetSection(AppOptions.SectionName));

                // ── Wave-1 服务（WS-D 接线）─────────────────────────────────
                // 日志：FileLogger + BufferedLogger + SecretRedactor 组合，注册为 ILogger 单例。
                AppLoggerFactory.ConfigureLogging(services);

                // 写入确认 + 审计门面（ProtocolViewModelBase 经 App.Services 解析）。
                services.AddSingleton<IConfirmationDialog, MessageBoxConfirmationDialog>();
                services.AddSingleton<IWriteAuditSink, WriteAuditSink>();
                services.AddSingleton<IWriteConfirmationService, WriteConfirmationService>();

                // WS-A: Settings 页 ViewModel（Transient —— 每次导航新建，Unloaded 时 Dispose）。
                services.AddTransient<SettingsViewModel>();

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddTransient<ModbusTcpViewModel>();
                services.AddTransient<ModbusUdpViewModel>();
                services.AddTransient<ModbusRtuViewModel>();
                services.AddTransient<ModbusAsciiViewModel>();
                services.AddTransient<ModbusRtuOverTcpViewModel>();
                services.AddTransient<ModbusAsciiOverTcpViewModel>();
                services.AddTransient<SiemensViewModel>();
                services.AddTransient<FetchWriteViewModel>();
                services.AddTransient<SiemensPpiViewModel>();
                services.AddTransient<MitsubishiViewModel>();
                services.AddTransient<MitsubishiFxViewModel>();
                services.AddTransient<OmronViewModel>();
                services.AddTransient<AllenBradleyViewModel>();
                services.AddTransient<PanasonicViewModel>();
                services.AddTransient<KeyenceViewModel>();
                services.AddTransient<BeckhoffViewModel>();
                services.AddTransient<DeltaViewModel>();
                services.AddTransient<FujiViewModel>();
                services.AddTransient<LsXgtViewModel>();
                services.AddTransient<InovanceViewModel>();
                services.AddTransient<EurothermViewModel>();
                services.AddTransient<FatekViewModel>();
                services.AddTransient<FanucViewModel>();
                services.AddTransient<GeSrtpViewModel>();
                services.AddTransient<XinjeViewModel>();
                services.AddTransient<KukaViewModel>();
                services.AddTransient<OpcUaViewModel>();
                services.AddTransient<OpcUaServerViewModel>();
                services.AddTransient<YaskawaViewModel>();
                services.AddTransient<YokogawaViewModel>();
                services.AddTransient<SchneiderViewModel>();
                services.AddTransient<Dnp3ViewModel>();
                services.AddTransient<Iec104ViewModel>();
                services.AddTransient<Iec61850ViewModel>();
                services.AddTransient<BacnetViewModel>();
                services.AddTransient<SecsViewModel>();
                services.AddTransient<RkcViewModel>();
                services.AddTransient<ToledoViewModel>();
                services.AddTransient<RobotEfortViewModel>();
                services.AddTransient<RobotFanucViewModel>();
                services.AddTransient<RobotKukaViewModel>();
                services.AddTransient<RobotUrViewModel>();
                services.AddTransient<RobotYaskawaViewModel>();
                services.AddTransient<RobotYamahaViewModel>();
                services.AddTransient<RobotStaubliViewModel>();
                services.AddSingleton<UserService>();
                services.AddSingleton<SimulatorViewModel>();
                services.AddSingleton<MonitorViewModel>();
                services.AddSingleton<AlarmService>();
                services.AddSingleton<SqliteDataLogger>();
                services.AddSingleton<RecipeService>();
                services.AddSingleton<AlarmViewModel>();
                services.AddSingleton<RecipeViewModel>();
                services.AddSingleton<DataLoggerViewModel>();
                services.AddSingleton<PacketRecorderService>();
                services.AddSingleton<ConnectionTemplateService>();
                services.AddSingleton<DiagnosticBundleService>();
                services.AddSingleton<VirtualPlcManager>();
                services.AddTransient<HmiViewModel>();
                services.AddTransient<TagConfigViewModel>();

                // Views
                services.AddSingleton<MainWindow>();
            })
            .Build();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var mainVm = _host.Services.GetRequiredService<MainViewModel>();
        mainWindow.SetViewModel(mainVm);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }

    // ── 全局异常处理辅助方法 ──────────────────────────

    /// <summary>判断异常是否为"状态已损坏、不应继续运行"的致命异常。</summary>
    private static bool IsFatal(Exception ex)
    {
        // OOM / StackOverflow / 访问违规 / 程序集损坏等。
        // 这些情况下进程状态已不可信，吞掉会让损坏状态被持久化或误写设备。
        // 注：ExecutionEngineException 已废弃且运行时不再抛出，故不再列入。
        return ex is OutOfMemoryException
            || ex is StackOverflowException
            || ex is AccessViolationException
            || ex is InvalidProgramException
            || ex is BadImageFormatException
            || ex is System.Runtime.InteropServices.SEHException;
    }

    /// <summary>安全弹窗 — UI 线程可能已损坏，弹窗本身也可能抛异常，故包 try/catch。</summary>
    private static void ShowErrorSafely(string message)
    {
        try
        {
            MessageBox.Show(message, "Nexus 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // UI 线程已无法响应，忽略；崩溃日志已在 WriteCrashLog 落盘。
        }
    }

    /// <summary>
    /// 把崩溃信息写到应用级 <see cref="ILogger"/>（脱敏后落 nexus.log 滚动序列）。
    /// 三个全局异常入口（DispatcherUnhandledException / AppDomain.UnhandledException /
    /// TaskScheduler.UnobservedTaskException）均经此路径，源标识由调用方传入。
    /// </summary>
    /// <remarks>
    /// 设计要点（WS-B "彻底放弃"原则）：
    /// <list type="bullet">
    /// <item>崩溃可能发生在 DI 容器就绪前，故惰性解析 <c>ILogger</c>，
    /// 失败时回退到 <see cref="AppLoggerFactory.CreateLogger(LoggingOptions)"/> 构造的临时实例。</item>
    /// <item>异常文本先经 <see cref="SecretRedactor.Redact"/> 脱敏（IP / 连接串口令 / Token）。</item>
    /// <item>最外层 try/catch 不删；若 logger 也抛，最终兜底 <c>File.AppendAllText(crash.log)</c>，
    /// 该兜底文本同样先经 <see cref="SecretRedactor.Redact"/>。</item>
    /// </list>
    /// </remarks>
    private static void WriteCrashLog(string source, object? errorObject)
    {
        try
        {
            string detail = errorObject is Exception ex
                ? $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}"
                : errorObject?.ToString() ?? "(null)";
            string safe = SecretRedactor.Redact(detail);
            CrashLogger.Error($"[Crash:{source}] {safe}");
        }
        catch
        {
            // logger 本身异常 — 兜底直写 crash.log（同样脱敏），永不在此抛。
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
                string detail = errorObject is Exception ex
                    ? $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}"
                    : errorObject?.ToString() ?? "(null)";
                string safe = SecretRedactor.Redact(detail);
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]\n{safe}\n{new string('-', 60)}\n";
                File.AppendAllText(path, entry);
            }
            catch
            {
                // 连写日志都失败（磁盘满/无权限）则彻底放弃；不可在此再抛异常。
            }
        }
    }

    /// <summary>
    /// 惰性解析崩溃专用 <see cref="ILogger"/>：优先取 DI 容器里的单例；
    /// 若 host 尚未就绪（早期 OnStartup 异常），回退到
    /// <see cref="AppLoggerFactory.CreateLogger(LoggingOptions)"/> 构造的临时实例。
    /// 该回退实例会更新 <see cref="AppLoggerFactory.BufferedLog"/>，便于事后排查。
    /// </summary>
    private static ILogger? _crashLogger;
    private static ILogger CrashLogger
    {
        get
        {
            if (_crashLogger != null) return _crashLogger;
            try
            {
                // _host 是实例字段；CrashLogger 为静态，经 Current 取当前 App 实例。
                var host = (Current as App)?._host;
                var fromDi = host?.Services.GetService(typeof(ILogger)) as ILogger;
                if (fromDi != null) { _crashLogger = fromDi; return fromDi; }
            }
            catch { /* DI 不可用 — 走回退 */ }
            ILogger fallback = AppLoggerFactory.CreateLogger(new LoggingOptions());
            _crashLogger = fallback;
            return fallback;
        }
    }

    /// <summary>
    /// 解析当前运行环境：优先 <c>DOTNET_ENVIRONMENT</c>，其次 <c>ASPNETCORE_ENVIRONMENT</c>，
    /// 最后 <c>NEXUS_ENVIRONMENT</c>；默认 <c>Development</c>（本地调试友好）。
    /// 仅接受 <c>Development</c> / <c>Production</c> / <c>Staging</c>；其它值回退到 Development。
    /// </summary>
    private static string ResolveEnvironment()
    {
        string env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                     ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                     ?? Environment.GetEnvironmentVariable("NEXUS_ENVIRONMENT")
                     ?? Environments.Development;
        // Environments.* 是 static readonly（非 const），无法用于常量模式匹配，故走字面比较。
        if (env == Environments.Production) return Environments.Production;
        if (env == Environments.Staging) return Environments.Staging;
        return Environments.Development;   // 未知值回退到 Development（本地调试友好）
    }
}
