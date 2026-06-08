using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.ViewModels;

namespace Nexus.App.Views;

public partial class MonitorPage : Page
{
    private MonitorViewModel? _vm;

    public MonitorPage()
    {
        InitializeComponent();
        _vm = ((App)Application.Current).Services.GetRequiredService<MonitorViewModel>();
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
    }
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NonZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToStartStopConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "⏸ Stop" : "▶ Start";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Color.FromRgb(63, 185, 80) : Color.FromRgb(84, 97, 120);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class QualityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            "Good" => Color.FromRgb(63, 185, 80),
            "Bad" => Color.FromRgb(248, 81, 73),
            _ => Color.FromRgb(210, 153, 34)
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringToColorConverter : IValueConverter
{
    public static readonly StringToColorConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6)
                {
                    byte r = System.Byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                    byte g = System.Byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                    byte b = System.Byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch { }
        }
        return Colors.CornflowerBlue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Color c)
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        return "#58A6FF";
    }
}
