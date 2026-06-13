using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Nexus.App.Services;

public static class ReportGenerator
{
    public static string GenerateCsvReport(IEnumerable<MonitoredAddress> addresses)
    {
        var sb = new StringBuilder();
        sb.AppendLine("地址,别名,类型,当前值,最小值,最大值,平均值,更新时间");
        foreach (var addr in addresses)
        {
            var snapshot = addr.GetSnapshot();
            if (snapshot.Count == 0)
            {
                sb.AppendLine($"{Escape(addr.Address)},{Escape(addr.Alias)},{addr.DataType},{Escape(addr.CurrentValueText)},,,,,");
                continue;
            }

            double min = snapshot.Min(p => p.Value);
            double max = snapshot.Max(p => p.Value);
            double avg = snapshot.Average(p => p.Value);

            sb.AppendLine($"{Escape(addr.Address)},{Escape(addr.Alias)},{addr.DataType},{Escape(addr.CurrentValueText)},{min:F4},{max:F4},{avg:F4},{addr.LastUpdateTime:yyyy-MM-dd HH:mm:ss}");
        }
        return sb.ToString();
    }

    public static string GenerateHtmlReport(IEnumerable<MonitoredAddress> addresses, string title = "Nexus SCADA 报表")
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset='utf-8'>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: -apple-system, sans-serif; padding: 24px; background: #0d1117; color: #c9d1d9; }");
        sb.AppendLine("h1 { color: #58a6ff; border-bottom: 1px solid #30363d; padding-bottom: 8px; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin: 16px 0; }");
        sb.AppendLine("th, td { padding: 8px 12px; text-align: left; border-bottom: 1px solid #21262d; }");
        sb.AppendLine("th { background: #161b22; color: #8b949e; font-weight: 600; }");
        sb.AppendLine(".value { font-family: Consolas, monospace; font-size: 16px; font-weight: bold; color: #58a6ff; }");
        sb.AppendLine(".ok { color: #3fb950; } .warn { color: #d29922; } .err { color: #f85149; }");
        sb.AppendLine(".timestamp { color: #8b949e; font-size: 12px; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>{EscapeHtml(title)}</h1>");
        sb.AppendLine($"<p class='timestamp'>生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");

        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>地址</th><th>别名</th><th>类型</th><th>当前值</th><th>最小值</th><th>最大值</th><th>平均值</th><th>更新时间</th></tr>");

        foreach (var addr in addresses)
        {
            var snapshot = addr.GetSnapshot();
            if (snapshot.Count == 0)
            {
                sb.AppendLine($"<tr><td>{EscapeHtml(addr.Address)}</td><td>{EscapeHtml(addr.Alias)}</td><td>{addr.DataType}</td>");
                sb.AppendLine($"<td class='value'>{EscapeHtml(addr.CurrentValueText)}</td><td>-</td><td>-</td><td>-</td><td>-</td></tr>");
                continue;
            }

            double min = snapshot.Min(p => p.Value);
            double max = snapshot.Max(p => p.Value);
            double avg = snapshot.Average(p => p.Value);

            sb.AppendLine($"<tr><td>{EscapeHtml(addr.Address)}</td><td>{EscapeHtml(addr.Alias)}</td><td>{addr.DataType}</td>");
            sb.AppendLine($"<td class='value'>{EscapeHtml(addr.CurrentValueText)}</td>");
            sb.AppendLine($"<td>{min:F4}</td><td>{max:F4}</td><td>{avg:F4}</td>");
            sb.AppendLine($"<td class='timestamp'>{addr.LastUpdateTime:HH:mm:ss}</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine($"<p class='timestamp'>数据点总数: {addresses.Sum(a => a.GetSnapshot().Count)}</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static string SaveReport(string content, string extension, string? directory = null)
    {
        directory ??= Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"Nexus_Report_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    private static string EscapeHtml(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
