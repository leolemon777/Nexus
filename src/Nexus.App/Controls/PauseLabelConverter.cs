using System;
using System.Globalization;
using System.Windows.Data;

namespace Nexus.App;

/// <summary>
/// H-2: 右侧报文监控暂停按钮的文本转换器。
/// 输入为 <see cref="ProtocolViewModelBase.IsPacketMonitorPaused"/>（bool）：
///   true  → "▶"  (已暂停，点击继续)
///   false → "⏸"  (运行中，点击暂停)
/// 绑定源为 null 时回退到 "⏸"。
/// </summary>
public sealed class PauseLabelConverter : IValueConverter
{
    public static readonly PauseLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool paused) return paused ? "▶" : "⏸";
        return "⏸";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
