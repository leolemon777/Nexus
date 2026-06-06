using System.Windows;
using System.Windows.Controls;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class ModbusTcpPage : Page
{
    private readonly ModbusTcpViewModel _vm = new();

    public ModbusTcpPage()
    {
        InitializeComponent();
        DataContext = _vm;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnPageUnloaded;
        _vm.Dispose();
    }
}
