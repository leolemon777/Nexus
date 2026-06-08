using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Nexus.App.Controls;

public partial class ConnectionStatusIndicator : UserControl
{
    private DispatcherTimer? _durationTimer;
    private DateTime _connectedSince;

    public ConnectionStatusIndicator()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── Dependency Properties ─────────────────────────────────

    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(string),
            typeof(ConnectionStatusIndicator), new FrameworkPropertyMetadata("Disconnected", OnStatusChanged));

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public static readonly DependencyProperty EndpointProperty =
        DependencyProperty.Register(nameof(Endpoint), typeof(string),
            typeof(ConnectionStatusIndicator), new FrameworkPropertyMetadata(string.Empty));

    public string Endpoint
    {
        get => (string)GetValue(EndpointProperty);
        set => SetValue(EndpointProperty, value);
    }

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string),
            typeof(ConnectionStatusIndicator), new FrameworkPropertyMetadata(string.Empty, OnStatusTextChanged));

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    // ── Property changed callbacks ────────────────────────────

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ConnectionStatusIndicator indicator)
            indicator.UpdateStatus();
    }

    private static void OnStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ConnectionStatusIndicator indicator)
            indicator.StatusTextBlock.Text = e.NewValue as string ?? string.Empty;
    }

    // ── Logic ─────────────────────────────────────────────────

    private void UpdateStatus()
    {
        StatusTextBlock.Text = !string.IsNullOrEmpty(StatusText) ? StatusText : Status;
        EndpointTextBlock.Text = Endpoint;

        if (Status == "Connected")
        {
            _connectedSince = DateTime.Now;
            StartDurationTimer();
        }
        else
        {
            StopDurationTimer();
            DurationTextBlock.Text = string.Empty;
        }
    }

    private void StartDurationTimer()
    {
        StopDurationTimer();
        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _connectedSince;
            DurationTextBlock.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
        };
        _durationTimer.Start();
    }

    private void StopDurationTimer()
    {
        if (_durationTimer != null)
        {
            _durationTimer.Stop();
            _durationTimer = null;
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopDurationTimer();
    }
}
