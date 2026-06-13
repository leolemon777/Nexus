using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class HmiPage : Page
{
    private HmiViewModel? _vm;

    public HmiPage()
    {
        InitializeComponent();
        _vm = ((App)Application.Current).Services.GetRequiredService<HmiViewModel>();
        DataContext = _vm;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnPageUnloaded;
        _vm?.Dispose();
    }
}
