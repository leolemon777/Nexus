using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.Services;

namespace Nexus.App.Views;

public partial class VirtualPlcPage : Page
{
    private VirtualPlcManager? _manager;

    public VirtualPlcPage()
    {
        InitializeComponent();
        _manager = ((App)Application.Current).Services.GetRequiredService<VirtualPlcManager>();
        DataContext = _manager;
    }
}

/// <summary>
/// bool → Visible(true) / Collapsed(false)
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool → Collapsed(true) / Visible(false)
/// </summary>
public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
