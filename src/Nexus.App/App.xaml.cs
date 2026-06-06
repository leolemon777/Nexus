using System.Windows;

namespace Nexus.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Init("mono", "brutal");   // 默认：极简灰白 × 粗犷直率
        new MainWindow { DataContext = new MainViewModel() }.Show();
    }
}
