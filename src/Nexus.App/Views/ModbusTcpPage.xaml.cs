using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class ModbusTcpPage : Page
{
    private ModbusTcpViewModel? _vm;

    public ModbusTcpPage()
    {
        InitializeComponent();
        // WPF Page 由 Frame 反射创建（无构造参数注入），从 DI 容器解析 VM。
        _vm = ((App)Application.Current).Services.GetRequiredService<ModbusTcpViewModel>();
        DataContext = _vm;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // 新日志到达时自动滚到底（_vm 在 ctor 中已注入，必非空）
        _vm!.LogLines.CollectionChanged += OnLogLinesChanged;
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (LogListBox.Items.Count == 0) return;
        var last = LogListBox.Items[LogListBox.Items.Count - 1];
        LogListBox.ScrollIntoView(last);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        Unloaded -= OnPageUnloaded;
        _vm!.LogLines.CollectionChanged -= OnLogLinesChanged;
        _vm!.Dispose();
    }
}
