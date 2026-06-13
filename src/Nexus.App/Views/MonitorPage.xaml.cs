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
    {
        _vm!.Initialize();
        _vm.LogLines.CollectionChanged += OnLogChanged;
    }

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

    private void BatchImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title = "批量导入监控地址",
            Width = 480,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = (Brush)FindResource("Brush.Bg")
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "每行一个地址，支持格式：\n  D100\n  D100|温度\n  D100|温度|Float\n也支持逗号或分号分隔。",
            Style = (Style)FindResource("Body"),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var tb = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Style = (Style)FindResource("Input")
        };
        panel.Children.Add(tb);

        var btn = new Button
        {
            Content = "导入",
            Style = (Style)FindResource("Button.Primary"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            IsDefault = true
        };
        btn.Click += (_, _) =>
        {
            _vm!.BatchAddMonitoredAddressesCommand.Execute(tb.Text);
            dlg.Close();
        };
        panel.Children.Add(btn);

        dlg.Content = panel;
        dlg.ShowDialog();
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

public sealed class InvertBoolConverter : IValueConverter
{
    public static readonly InvertBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;
}
