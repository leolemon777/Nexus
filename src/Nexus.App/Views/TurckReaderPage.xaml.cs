using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class TurckReaderPage : Page
{
    private TurckReaderViewModel? _vm;

    public TurckReaderPage()
    {
        InitializeComponent();
        _vm = ((App)Application.Current).Services.GetRequiredService<TurckReaderViewModel>();
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
        _vm!.LogLines.CollectionChanged -= OnLogChanged;
        _vm.Dispose();
    }
}
