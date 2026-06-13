using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;

namespace Nexus.App.ViewModels;

public partial class DataLoggerViewModel : ObservableObject, IDisposable
{
    private readonly SqliteDataLogger _logger;

    [ObservableProperty] private bool _isLogging;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _totalLogged;
    [ObservableProperty] private string _statusText = "未启动";

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<LogEntry> RecentEntries { get; } = new();

    public DataLoggerViewModel(SqliteDataLogger logger)
    {
        _logger = logger;
        _logger.OnFlushed += OnFlushed;
    }

    [RelayCommand]
    private void StartLogging()
    {
        _logger.Start();
        IsLogging = true;
        StatusText = $"记录中 → {_logger.DatabasePath}";
        AppendLog("[OK] 数据记录已启动");
    }

    [RelayCommand]
    private void StopLogging()
    {
        _logger.Stop();
        IsLogging = false;
        StatusText = $"已停止 (共 {_logger.TotalLogged} 条)";
        AppendLog("[OK] 数据记录已停止");
    }

    [RelayCommand]
    private void RefreshStats()
    {
        PendingCount = _logger.PendingCount;
        TotalLogged = _logger.TotalLogged;

        // Show last 20 entries
        var recent = _logger.Query(DateTime.Now.AddHours(-1), DateTime.Now, limit: 20);
        RecentEntries.Clear();
        foreach (var entry in recent)
            RecentEntries.Add(entry);
    }

    [RelayCommand]
    private void ExportCsv()
    {
        try
        {
            var csv = _logger.ExportCsv(DateTime.Now.AddDays(-7), DateTime.Now);
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"nexus_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            System.IO.File.WriteAllText(path, csv);
            AppendLog($"[OK] 已导出 CSV: {path}");
        }
        catch (Exception ex) { AppendLog($"[ERR] 导出失败: {ex.Message}"); }
    }

    [RelayCommand]
    private void ExportJson()
    {
        try
        {
            var json = _logger.ExportJson(DateTime.Now.AddDays(-7), DateTime.Now);
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"nexus_export_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            System.IO.File.WriteAllText(path, json);
            AppendLog($"[OK] 已导出 JSON: {path}");
        }
        catch (Exception ex) { AppendLog($"[ERR] 导出失败: {ex.Message}"); }
    }

    private void OnFlushed(object? sender, int count)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            TotalLogged = _logger.TotalLogged;
            PendingCount = _logger.PendingCount;
            AppendLog($"[DB] 已写入 {count} 条记录");
        }));
    }

    private void AppendLog(string line)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        if (LogLines.Count > 200) LogLines.RemoveAt(0);
    }

    public void Dispose()
    {
        _logger.OnFlushed -= OnFlushed;
        GC.SuppressFinalize(this);
    }
}
