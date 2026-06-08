using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nexus.App.Services;

namespace Nexus.App.Controls
{
    public partial class RealtimeChart : UserControl
    {
        private const double YAxisWidth = 60;
        private const double XAxisHeight = 28;
        private const double TopPadding = 8;
        private const double RightPadding = 12;
        private const double GridLineDash = 4;

        private double _timeWindowSeconds = 60;
        private double _panOffsetSeconds;
        private bool _isPanning;
        private Point _panStart;
        private double _panStartOffset;
        private readonly DispatcherTimer _refreshTimer;

        private ObservableCollection<MonitoredAddress>? _addresses;

        public RealtimeChart()
        {
            InitializeComponent();
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _refreshTimer.Tick += (_, _) => InvalidateVisual();

            SizeChanged += (_, _) => InvalidateVisual();
            MouseWheel += OnMouseWheel;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            MouseMove += OnMouseMove;
            Loaded += (_, _) => _refreshTimer.Start();
            Unloaded += (_, _) => _refreshTimer.Stop();
        }

        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(ObservableCollection<MonitoredAddress>),
                typeof(RealtimeChart), new PropertyMetadata(null, OnDataChanged));

        public ObservableCollection<MonitoredAddress>? Data
        {
            get => (ObservableCollection<MonitoredAddress>?)GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public static readonly DependencyProperty TimeWindowSecondsProperty =
            DependencyProperty.Register(nameof(TimeWindowSeconds), typeof(double),
                typeof(RealtimeChart), new PropertyMetadata(60.0, OnTimeWindowChanged));

        public double TimeWindowSeconds
        {
            get => (double)GetValue(TimeWindowSecondsProperty);
            set => SetValue(TimeWindowSecondsProperty, value);
        }

        private static void OnTimeWindowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var chart = (RealtimeChart)d;
            chart._timeWindowSeconds = Math.Max(5, Math.Min(3600, (double)e.NewValue));
            chart.InvalidateVisual();
        }

        private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var chart = (RealtimeChart)d;
            if (e.OldValue is ObservableCollection<MonitoredAddress> old)
                old.CollectionChanged -= chart.OnAddressesChanged;
            chart._addresses = e.NewValue as ObservableCollection<MonitoredAddress>;
            if (chart._addresses != null)
                chart._addresses.CollectionChanged += chart.OnAddressesChanged;
            chart.InvalidateVisual();
        }

        private void OnAddressesChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => InvalidateVisual();

        // ── Mouse interaction ────────────────────────────

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            var pos = e.GetPosition(this);
            double chartWidth = ActualWidth - YAxisWidth - RightPadding;
            if (chartWidth <= 0) return;

            double mouseRatio = Math.Clamp((pos.X - 0) / chartWidth, 0, 1);
            double oldWindow = _timeWindowSeconds;
            _timeWindowSeconds = Math.Max(5, Math.Min(3600, _timeWindowSeconds * factor));

            double windowDelta = _timeWindowSeconds - oldWindow;
            _panOffsetSeconds += mouseRatio * windowDelta;

            e.Handled = true;
            InvalidateVisual();
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);
            double chartWidth = ActualWidth - YAxisWidth - RightPadding;
            if (pos.X > chartWidth || pos.Y > ActualHeight - XAxisHeight) return;

            _isPanning = true;
            _panStart = pos;
            _panStartOffset = _panOffsetSeconds;
            CaptureMouse();
            Cursor = Cursors.ScrollWE;
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            _isPanning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var pos = e.GetPosition(this);
            double chartWidth = ActualWidth - YAxisWidth - RightPadding;
            if (chartWidth <= 0) return;

            double pixelsPerSecond = chartWidth / _timeWindowSeconds;
            double dx = pos.X - _panStart.X;
            _panOffsetSeconds = _panStartOffset - dx / pixelsPerSecond;

            InvalidateVisual();
        }

        // ── Rendering ────────────────────────────────────

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var addresses = _addresses;
            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 50 || h < 50 || addresses == null || addresses.Count == 0)
            {
                RenderEmptyState(dc, w, h);
                return;
            }

            double chartLeft = YAxisWidth;
            double chartRight = w - RightPadding;
            double chartTop = TopPadding;
            double chartBottom = h - XAxisHeight;
            double chartWidth = chartRight - chartLeft;
            double chartHeight = chartBottom - chartTop;
            if (chartWidth <= 0 || chartHeight <= 0) return;

            var now = DateTime.Now;
            double windowEnd = -_panOffsetSeconds;
            double windowStart = windowEnd - _timeWindowSeconds;

            // Background
            var bgBrush = TryFindResource("Brush.Bg") as SolidColorBrush ?? Brushes.Black;
            dc.DrawRectangle(bgBrush, null, new Rect(0, 0, w, h));

            // Calculate global Y range
            double globalMin = double.MaxValue;
            double globalMax = double.MinValue;
            var snapshots = new List<(MonitoredAddress addr, List<DataPoint> points)>();

            foreach (var addr in addresses)
            {
                var pts = addr.GetSnapshot();
                snapshots.Add((addr, pts));
                addr.GetRange(out double aMin, out double aMax);
                if (aMin < globalMin) globalMin = aMin;
                if (aMax > globalMax) globalMax = aMax;
            }

            if (globalMin >= globalMax)
            {
                globalMin = -1;
                globalMax = 1;
            }

            double yRange = globalMax - globalMin;
            if (yRange < 1e-9) yRange = 1;
            double yPadding = yRange * 0.08;
            globalMin -= yPadding;
            globalMax += yPadding;
            yRange = globalMax - globalMin;

            // Chart area clip
            dc.PushClip(new RectangleGeometry(new Rect(chartLeft, chartTop, chartWidth, chartHeight)));

            // Grid lines
            var gridBrush = TryFindResource("Brush.Line") as SolidColorBrush ?? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            var gridPen = new Pen(gridBrush, 0.5);
            gridPen.Freeze();

            int yGridCount = 5;
            for (int i = 0; i <= yGridCount; i++)
            {
                double ratio = (double)i / yGridCount;
                double y = chartBottom - ratio * chartHeight;
                dc.DrawLine(gridPen, new Point(chartLeft, y), new Point(chartRight, y));
            }

            // X grid lines (time markers)
            double secondsPerGrid = CalcNiceInterval(_timeWindowSeconds);
            double firstGridTime = Math.Ceiling(windowStart / secondsPerGrid) * secondsPerGrid;
            var textBrush = TryFindResource("Brush.Text3") as SolidColorBrush ?? Brushes.Gray;
            var typeface = new Typeface("Segoe UI");

            for (double t = firstGridTime; t <= windowEnd; t += secondsPerGrid)
            {
                double ratio = (t - windowStart) / _timeWindowSeconds;
                double x = chartLeft + ratio * chartWidth;
                dc.DrawLine(gridPen, new Point(x, chartTop), new Point(x, chartBottom));
            }

            // Draw data series
            for (int si = 0; si < snapshots.Count; si++)
            {
                var (addr, points) = snapshots[si];
                if (points.Count < 2) continue;

                var color = ParseColor(addr.SeriesColor);
                var pen = new Pen(new SolidColorBrush(color), 1.5);
                pen.Freeze();

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    bool started = false;
                    Point prev = default;

                    for (int i = 0; i < points.Count; i++)
                    {
                        var dp = points[i];
                        double secondsAgo = (now - dp.Time).TotalSeconds;
                        if (secondsAgo < windowStart || secondsAgo > windowEnd) continue;

                        double xRatio = (secondsAgo - windowStart) / _timeWindowSeconds;
                        double x = chartLeft + xRatio * chartWidth;
                        double yRatio = (dp.Value - globalMin) / yRange;
                        double y = chartBottom - yRatio * chartHeight;

                        var pt = new Point(x, y);
                        if (!started)
                        {
                            ctx.BeginFigure(pt, false, false);
                            started = true;
                        }
                        else
                        {
                            ctx.LineTo(pt, true, false);
                        }
                        prev = pt;
                    }
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }

            dc.Pop();

            // Y axis labels
            for (int i = 0; i <= yGridCount; i++)
            {
                double ratio = (double)i / yGridCount;
                double y = chartBottom - ratio * chartHeight;
                double value = globalMin + ratio * yRange;

                var ft = new FormattedText(
                    FormatValue(value),
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface, 10, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(chartLeft - ft.Width - 4, y - ft.Height / 2));
            }

            // X axis labels
            for (double t = firstGridTime; t <= windowEnd; t += secondsPerGrid)
            {
                double ratio = (t - windowStart) / _timeWindowSeconds;
                double x = chartLeft + ratio * chartWidth;
                string label = FormatTimeAxis(t, _timeWindowSeconds);

                var ft = new FormattedText(
                    label,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface, 10, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(x - ft.Width / 2, chartBottom + 4));
            }

            // Legend
            RenderLegend(dc, addresses, chartLeft, chartTop, typeface);

            // Border
            var borderPen = new Pen(TryFindResource("Brush.Line2") as SolidColorBrush ?? Brushes.DarkGray, 1);
            borderPen.Freeze();
            dc.DrawRectangle(null, borderPen, new Rect(chartLeft, chartTop, chartWidth, chartHeight));
        }

        private void RenderEmptyState(DrawingContext dc, double w, double h)
        {
            var bgBrush = TryFindResource("Brush.Bg") as SolidColorBrush ?? Brushes.Black;
            dc.DrawRectangle(bgBrush, null, new Rect(0, 0, w, h));

            var textBrush = TryFindResource("Brush.Text3") as SolidColorBrush ?? Brushes.Gray;
            var typeface = new Typeface("Segoe UI");
            var ft = new FormattedText(
                "No data — add monitored addresses and start polling",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface, 14, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
        }

        private void RenderLegend(DrawingContext dc, ObservableCollection<MonitoredAddress> addresses,
            double left, double top, Typeface typeface)
        {
            double x = left + 4;
            double y = top + 4;
            double lineHeight = 16;

            for (int i = 0; i < addresses.Count; i++)
            {
                var addr = addresses[i];
                var color = ParseColor(addr.SeriesColor);
                var textBrush = TryFindResource("Brush.Text2") as SolidColorBrush ?? Brushes.LightGray;

                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, y + 3, 10, 10));
                string label = $"{addr.DisplayName}: {addr.CurrentValueText}";
                var ft = new FormattedText(
                    label,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface, 11, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(x + 14, y));

                x += ft.Width + 24;
                if (x > left + 500)
                {
                    x = left + 4;
                    y += lineHeight;
                }
            }
        }

        // ── Helpers ──────────────────────────────────────

        private static Color ParseColor(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex)) return Colors.CornflowerBlue;
                hex = hex.TrimStart('#');
                if (hex.Length == 6)
                {
                    byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                    return Color.FromRgb(r, g, b);
                }
                if (hex.Length == 8)
                {
                    byte a = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                    byte r = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber);
                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch { }
            return Colors.CornflowerBlue;
        }

        private static string FormatValue(double value)
        {
            if (Math.Abs(value) >= 1e6 || (Math.Abs(value) < 0.01 && value != 0))
                return value.ToString("G4", CultureInfo.InvariantCulture);
            if (Math.Abs(value - Math.Round(value)) < 1e-6)
                return value.ToString("F0", CultureInfo.InvariantCulture);
            return value.ToString("G6", CultureInfo.InvariantCulture);
        }

        private static string FormatTimeAxis(double secondsAgo, double windowSeconds)
        {
            var t = DateTime.Now.AddSeconds(-secondsAgo);
            if (windowSeconds <= 120)
                return t.ToString("HH:mm:ss");
            if (windowSeconds <= 600)
                return t.ToString("HH:mm:ss");
            return t.ToString("HH:mm");
        }

        private static double CalcNiceInterval(double windowSeconds)
        {
            double raw = windowSeconds / 6;
            double[] nice = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
            for (int i = 0; i < nice.Length; i++)
                if (nice[i] >= raw) return nice[i];
            return 3600;
        }
    }
}
