using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nexus.App.Controls;

public partial class TagValueDisplay : UserControl
{
    public TagValueDisplay()
    {
        InitializeComponent();
    }

    // ── Dependency Properties ─────────────────────────────────

    public static readonly DependencyProperty AddressProperty =
        DependencyProperty.Register(nameof(Address), typeof(string),
            typeof(TagValueDisplay), new FrameworkPropertyMetadata("--"));

    public string Address
    {
        get => (string)GetValue(AddressProperty);
        set => SetValue(AddressProperty, value);
    }

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string),
            typeof(TagValueDisplay), new FrameworkPropertyMetadata("--"));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty DataTypeProperty =
        DependencyProperty.Register(nameof(DataType), typeof(string),
            typeof(TagValueDisplay), new FrameworkPropertyMetadata(string.Empty));

    public string DataType
    {
        get => (string)GetValue(DataTypeProperty);
        set => SetValue(DataTypeProperty, value);
    }

    public static readonly DependencyProperty QualityProperty =
        DependencyProperty.Register(nameof(Quality), typeof(string),
            typeof(TagValueDisplay), new FrameworkPropertyMetadata("Good"));

    public string Quality
    {
        get => (string)GetValue(QualityProperty);
        set => SetValue(QualityProperty, value);
    }

    public static readonly DependencyProperty TimestampProperty =
        DependencyProperty.Register(nameof(Timestamp), typeof(string),
            typeof(TagValueDisplay), new FrameworkPropertyMetadata(string.Empty));

    public string Timestamp
    {
        get => (string)GetValue(TimestampProperty);
        set => SetValue(TimestampProperty, value);
    }

    // ── Click to copy ─────────────────────────────────────────

    private void OnCopyClick(object sender, MouseButtonEventArgs e)
    {
        var val = Value;
        if (!string.IsNullOrEmpty(val))
        {
            try
            {
                Clipboard.SetText(val);
            }
            catch
            {
                // Clipboard can fail in some contexts
            }
        }
    }
}
