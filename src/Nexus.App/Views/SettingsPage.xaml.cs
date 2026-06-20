using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

/// <summary>
/// Settings 页 code-behind —— 仅做 DataContext 接线 + 日志自动滚动 + 生命周期转发。
/// 无业务逻辑（VM 经 DI 解析，与其它协议页同模式）。
/// </summary>
public partial class SettingsPage : Page
{
    private SettingsViewModel? _vm;

    public SettingsPage()
    {
        InitializeComponent();
        // WPF Page 由 Frame 反射创建（无构造参数注入），从 DI 容器解析 VM。
        // DI 容器不可用时（设计时/早期启动）回退到无参构造（其内部走安全默认）。
        try
        {
            _vm = ((App)Application.Current).Services.GetRequiredService<SettingsViewModel>();
        }
        catch
        {
            _vm = new SettingsViewModel();
        }
        DataContext = _vm;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // 日志新增行时自动滚到底（仅可见时才订阅，避免后台空转）。
        if (_vm != null)
        {
            _vm.LogLines.CollectionChanged += OnLogLinesChanged;
            _vm.OnPageActivated();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        Unloaded -= OnPageUnloaded;
        if (_vm != null)
        {
            _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
            _vm.OnPageDeactivated();
        }
    }

    private void OnLogLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
        if (LogListBox.Items.Count == 0) return;
        var last = LogListBox.Items[LogListBox.Items.Count - 1];
        LogListBox.ScrollIntoView(last);
    }
}
