using System;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nexus.App.Configuration;
using Nexus.App.ViewModels;
using Nexus.App.Services;
using Nexus.App.Views;

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
                services.AddTransient<SiemensViewModel>();
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
                services.AddTransient<YaskawaViewModel>();
                services.AddTransient<YokogawaViewModel>();
                services.AddSingleton<SimulatorViewModel>();
                services.AddSingleton<MonitorViewModel>();
                services.AddSingleton<AlarmService>();
                services.AddSingleton<SqliteDataLogger>();
                services.AddSingleton<RecipeService>();
                services.AddSingleton<AlarmViewModel>();
                services.AddSingleton<RecipeViewModel>();
                services.AddSingleton<DataLoggerViewModel>();

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
}
