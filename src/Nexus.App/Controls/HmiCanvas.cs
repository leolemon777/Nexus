using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nexus.App.Models;

namespace Nexus.App.Controls;

public class HmiCanvas : Canvas
{
    public static readonly DependencyProperty ElementsProperty =
        DependencyProperty.Register(nameof(Elements), typeof(ObservableCollection<HmiElement>),
            typeof(HmiCanvas), new PropertyMetadata(null, (d, _) => ((HmiCanvas)d).InvalidateVisual()));

    public static readonly DependencyProperty IsEditModeProperty =
        DependencyProperty.Register(nameof(IsEditMode), typeof(bool),
            typeof(HmiCanvas), new PropertyMetadata(true));

    public ObservableCollection<HmiElement>? Elements
    {
        get => (ObservableCollection<HmiElement>?)GetValue(ElementsProperty);
        set => SetValue(ElementsProperty, value);
    }

    public bool IsEditMode
    {
        get => (bool)GetValue(IsEditModeProperty);
        set => SetValue(IsEditModeProperty, value);
    }

    private HmiElement? _dragging;
    private Point _dragOffset;

    public HmiCanvas()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEditMode) return;
        var pos = e.GetPosition(this);
        _dragging = HitTest(pos);
        if (_dragging != null)
        {
            _dragOffset = new Point(pos.X - _dragging.X, pos.Y - _dragging.Y);
            CaptureMouse();
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = null;
        ReleaseMouseCapture();
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging == null || !IsEditMode) return;
        var pos = e.GetPosition(this);
        _dragging.X = pos.X - _dragOffset.X;
        _dragging.Y = pos.Y - _dragOffset.Y;
        InvalidateVisual();
        base.OnMouseMove(e);
    }

    private HmiElement? HitTest(Point pos)
    {
        if (Elements == null) return null;
        for (int i = Elements.Count - 1; i >= 0; i--)
        {
            var el = Elements[i];
            if (pos.X >= el.X && pos.X <= el.X + el.Width &&
                pos.Y >= el.Y && pos.Y <= el.Y + el.Height)
                return el;
        }
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Elements == null) return;

        double dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 0.5);
        gridPen.Freeze();
        for (double x = 0; x < ActualWidth; x += 20)
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, ActualHeight));
        for (double y = 0; y < ActualHeight; y += 20)
            dc.DrawLine(gridPen, new Point(0, y), new Point(ActualWidth, y));

        foreach (var el in Elements)
        {
            DrawElement(dc, el, dpiScale);
        }
    }

    private void DrawElement(DrawingContext dc, HmiElement el, double dpiScale)
    {
        var color = ParseColor(el.Color);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, 2);
        pen.Freeze();

        if (el.IsAlarming)
        {
            var alarmBrush = new SolidColorBrush(Color.FromArgb(80, 248, 81, 73));
            alarmBrush.Freeze();
            dc.DrawRectangle(alarmBrush, null, new Rect(el.X - 4, el.Y - 4, el.Width + 8, el.Height + 8));
        }

        switch (el.Type)
        {
            case HmiElementType.Tank:
                DrawTank(dc, el, color, pen, dpiScale);
                break;
            case HmiElementType.Pump:
                DrawPump(dc, el, brush, pen);
                break;
            case HmiElementType.Valve:
                DrawValve(dc, el, brush, pen);
                break;
            case HmiElementType.Pipe:
                DrawPipe(dc, el, brush, pen);
                break;
            case HmiElementType.Sensor:
                DrawSensor(dc, el, brush, pen, dpiScale);
                break;
            case HmiElementType.Label:
                DrawLabel(dc, el, brush, dpiScale);
                break;
            case HmiElementType.Indicator:
                DrawIndicator(dc, el, pen);
                break;
            case HmiElementType.Gauge:
                DrawGauge(dc, el, brush, pen, dpiScale);
                break;
        }

        if (!string.IsNullOrEmpty(el.BoundAddress))
        {
            var valueText = $"{el.CurrentValue:F1} {el.Unit}";
            var ft = new FormattedText(valueText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"), 11, Brushes.White, dpiScale);
            dc.DrawText(ft, new Point(el.X + (el.Width - ft.Width) / 2, el.Y + el.Height + 2));
        }
    }

    private void DrawTank(DrawingContext dc, HmiElement el, Color color, Pen pen, double dpiScale)
    {
        dc.DrawRectangle(null, pen, new Rect(el.X, el.Y, el.Width, el.Height));
        double ratio = (el.CurrentValue - el.MinValue) / Math.Max(1, el.MaxValue - el.MinValue);
        ratio = Math.Clamp(ratio, 0, 1);
        double fillHeight = el.Height * ratio;
        var fillBrush = new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B));
        fillBrush.Freeze();
        dc.DrawRectangle(fillBrush, null, new Rect(el.X + 2, el.Y + el.Height - fillHeight, el.Width - 4, fillHeight));
        for (int i = 1; i < 4; i++)
        {
            double ly = el.Y + (el.Height * i / 4);
            var dashPen = new Pen(Brushes.Gray, 0.5) { DashStyle = DashStyles.Dash };
            dashPen.Freeze();
            dc.DrawLine(dashPen, new Point(el.X, ly), new Point(el.X + el.Width, ly));
        }
    }

    private void DrawPump(DrawingContext dc, HmiElement el, Brush stroke, Pen pen)
    {
        double cx = el.X + el.Width / 2;
        double cy = el.Y + el.Height / 2;
        double r = Math.Min(el.Width, el.Height) / 2 - 2;
        dc.DrawEllipse(null, pen, new Point(cx, cy), r, r);
        bool running = el.CurrentValue > 0.5;
        if (running)
        {
            var arrowPen = new Pen(stroke, 3);
            arrowPen.Freeze();
            dc.DrawLine(arrowPen, new Point(cx - r * 0.5, cy), new Point(cx + r * 0.5, cy));
            dc.DrawLine(arrowPen, new Point(cx + r * 0.3, cy - r * 0.3), new Point(cx + r * 0.5, cy));
            dc.DrawLine(arrowPen, new Point(cx + r * 0.3, cy + r * 0.3), new Point(cx + r * 0.5, cy));
        }
    }

    private void DrawValve(DrawingContext dc, HmiElement el, Brush stroke, Pen pen)
    {
        bool open = el.CurrentValue > 0.5;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(el.X, el.Y), true, true);
            ctx.LineTo(new Point(el.X + el.Width, el.Y + el.Height / 2), true, false);
            ctx.LineTo(new Point(el.X, el.Y + el.Height), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(open ? stroke : null, pen, geometry);
    }

    private void DrawPipe(DrawingContext dc, HmiElement el, Brush stroke, Pen pen)
    {
        dc.DrawRectangle(stroke, null, new Rect(el.X, el.Y, el.Width, el.Height));
    }

    private void DrawSensor(DrawingContext dc, HmiElement el, Brush stroke, Pen pen, double dpiScale)
    {
        double cx = el.X + el.Width / 2;
        double cy = el.Y + el.Height / 2;
        double r = Math.Min(el.Width, el.Height) / 2 - 2;
        dc.DrawEllipse(null, pen, new Point(cx, cy), r, r);
        var ft = new FormattedText($"{el.CurrentValue:F1}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"), 12, Brushes.White, dpiScale);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    private void DrawLabel(DrawingContext dc, HmiElement el, Brush brush, double dpiScale)
    {
        var ft = new FormattedText(el.Label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 14, brush, dpiScale);
        dc.DrawText(ft, new Point(el.X, el.Y));
    }

    private void DrawIndicator(DrawingContext dc, HmiElement el, Pen pen)
    {
        double cx = el.X + el.Width / 2;
        double cy = el.Y + el.Height / 2;
        double r = Math.Min(el.Width, el.Height) / 2 - 2;
        bool on = el.CurrentValue > 0.5;
        var fillBrush = on ? Brushes.LimeGreen : Brushes.DarkGray;
        dc.DrawEllipse(fillBrush, pen, new Point(cx, cy), r, r);
    }

    private void DrawGauge(DrawingContext dc, HmiElement el, Brush stroke, Pen pen, double dpiScale)
    {
        double cx = el.X + el.Width / 2;
        double cy = el.Y + el.Height / 2;
        double r = Math.Min(el.Width, el.Height) / 2 - 4;

        var bgPen = new Pen(Brushes.DarkGray, 4);
        bgPen.Freeze();
        var bgGeom = CreateArcGeometry(cx, cy, r, 225, 315);
        dc.DrawGeometry(null, bgPen, bgGeom);

        double ratio = (el.CurrentValue - el.MinValue) / Math.Max(1, el.MaxValue - el.MinValue);
        ratio = Math.Clamp(ratio, 0, 1);
        double endAngle = 225 + ratio * 270;
        var valPen = new Pen(stroke, 4);
        valPen.Freeze();
        var valGeom = CreateArcGeometry(cx, cy, r, 225, endAngle);
        dc.DrawGeometry(null, valPen, valGeom);

        var ft = new FormattedText($"{el.CurrentValue:F1}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"), 12, Brushes.White, dpiScale);
        dc.DrawText(ft, new Point(cx - ft.Width / 2, cy + r * 0.3));
    }

    private static StreamGeometry CreateArcGeometry(double cx, double cy, double r, double startAngle, double endAngle)
    {
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            double startRad = startAngle * Math.PI / 180;
            double endRad = endAngle * Math.PI / 180;
            var start = new Point(cx + r * Math.Cos(startRad), cy + r * Math.Sin(startRad));
            var end = new Point(cx + r * Math.Cos(endRad), cy + r * Math.Sin(endRad));
            bool isLargeArc = (endAngle - startAngle) > 180;
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, isLargeArc, SweepDirection.Clockwise, true, false);
        }
        geom.Freeze();
        return geom;
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.Gray; }
    }
}
