using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((ctx, services) =>
            {
                // Options
                services.Configure<ModbusOptions>(
                    ctx.Configuration.GetSection(ModbusOptions.SectionName));

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

    /// <summary>把崩溃信息写入 crash.log（应用所在目录），供事后排查。</summary>
    private static void WriteCrashLog(string source, object? errorObject)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            string detail = errorObject is Exception ex
                ? $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}"
                : errorObject?.ToString() ?? "(null)";
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]\n{detail}\n{new string('-', 60)}\n";
            File.AppendAllText(path, entry);
        }
        catch
        {
            // 连写日志都失败（磁盘满/无权限）则彻底放弃；不可在此再抛异常。
        }
    }
}
