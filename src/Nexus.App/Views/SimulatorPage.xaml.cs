using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class SimulatorPage : Page
{
    private SimulatorViewModel? _vm;

    public SimulatorPage()
    {
        InitializeComponent();
        _vm = ((App)Application.Current).Services.GetRequiredService<SimulatorViewModel>();
        DataContext = _vm;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
        => _vm!.LogLines.CollectionChanged += OnLogChanged;

    private void OnLogChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogListBox.Items.Count == 0) return;
        LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        Unloaded -= OnPageUnloaded;
        // Simulator 是 Singleton，不 Dispose
        _vm!.LogLines.CollectionChanged -= OnLogChanged;
    }
}
