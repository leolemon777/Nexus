using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class AlarmPage : Page
{
    private AlarmViewModel? _vm;

    public AlarmPage()
    {
        InitializeComponent();
        _vm = ((App)Application.Current).Services.GetRequiredService<AlarmViewModel>();
        DataContext = _vm;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnPageUnloaded;
        _vm?.Dispose();
    }
}
