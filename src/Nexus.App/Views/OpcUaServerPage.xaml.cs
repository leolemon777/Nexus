using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class OpcUaServerPage : Page
{
    private OpcUaServerViewModel? _vm;

    public OpcUaServerPage()
    {
        InitializeComponent();
        // WPF Page 由 Frame 反射创建（无构造参数注入），从 DI 容器解析 VM。
        // VM 是瞬态，必须在页面 Unloaded 时 Dispose，否则其持有的 OPC UA 服务器
        // （监听 4840 端口、后台连接）会泄漏到下一次导航（C3 修复）。
        _vm = ((App)Application.Current).Services.GetRequiredService<OpcUaServerViewModel>();
        DataContext = _vm;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnPageUnloaded;
        _vm?.Dispose();
    }
}

public sealed class BooleanInverterConverter : IValueConverter
{
    public static readonly BooleanInverterConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;
}
