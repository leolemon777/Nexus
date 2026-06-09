using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;

namespace Nexus.App.ViewModels;

/// <summary>
/// 本地 Modbus TCP 模拟器 ViewModel — 一键启动本地模拟 PLC。
/// </summary>
public partial class SimulatorViewModel : ObservableObject, IDisposable
{
    private readonly ModbusTcpSimulator _simulator = new();
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _updateTimer;
    private bool _disposed;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "已停止";
    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private int _port = 1502;
    [ObservableProperty] private int _connectionCount;
    [ObservableProperty] private int _simulatedLatencyMs;
    [ObservableProperty] private string _presetInfo = string.Empty;
    [ObservableProperty] private string _selectedMemoryArea = "Holding Register";
    [ObservableProperty] private int _memoryStartAddress;
    [ObservableProperty] private int _memoryCount = 16;
    [ObservableProperty] private int _editAddress;
    [ObservableProperty] private string _editValue = "0";

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<SimulatorMemoryRow> MemoryRows { get; } = new();
    public string[] MemoryAreas { get; } = { "Holding Register", "Input Register", "Coil", "Discrete Input" };
    private const int LogCap = 200;

    public SimulatorViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _simulator.RequestReceived += OnRequestReceived;
        UpdatePresetInfo();
        RefreshMemorySnapshot();
    }

    private void UpdatePresetInfo()
    {
        PresetInfo = "预置数据: HR0=128, HR1(正弦波), HR12=365, HR100=1000, HR101=2000, "
                   + "COIL0=ON, IR0(随机), DI0-DI19(随机)";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        try
        {
            AppendLog($"正在启动模拟器 {Host}:{Port}...");
            await _simulator.StartAsync(Port);
            IsRunning = true;
            Status = $"运行中 (端口 {Port})";

            // 启动定时器更新动态数据
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _updateTimer.Tick += OnTimerTick;
            _updateTimer.Start();

            AppendLog("[OK] 模拟器已启动，可用 Modbus TCP 连接 127.0.0.1:" + Port);
            AppendLog("提示: HR0=128, HR1=正弦波, HR12=365, COIL0=1");
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] 启动失败: " + ex.Message);
            Status = "启动失败";
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRunning) return;
        try
        {
            _updateTimer?.Stop();
            _updateTimer = null;
            await _simulator.StopAsync();
            IsRunning = false;
            Status = "已停止";
            ConnectionCount = 0;
            AppendLog("[--] 模拟器已停止。");
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] 停止失败: " + ex.Message);
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!IsRunning) return;
        _simulator.UpdateDynamicData();
        ConnectionCount = _simulator.ConnectionCount;
        RefreshMemorySnapshot();
    }

    partial void OnSimulatedLatencyMsChanged(int value)
    {
        _simulator.SimulatedLatencyMs = value;
        if (IsRunning)
            AppendLog($"延迟模拟: {value}ms");
    }

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    [RelayCommand]
    private void RefreshMemory()
    {
        RefreshMemorySnapshot();
        AppendLog($"[MEM] 已刷新 {SelectedMemoryArea} [{MemoryStartAddress}..{MemoryStartAddress + MemoryRows.Count - 1}]");
    }

    [RelayCommand]
    private void ApplyMemoryValue()
    {
        try
        {
            _simulator.SetValue(SelectedMemoryArea, EditAddress, EditValue);
            RefreshMemorySnapshot();
            AppendLog($"[MEM] {SelectedMemoryArea}[{EditAddress}] = {EditValue}");
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] 写入内存失败: " + ex.Message);
        }
    }

    [RelayCommand]
    private void ResetData()
    {
        _simulator.ResetData();
        RefreshMemorySnapshot();
        AppendLog("[MEM] 预置数据已重置。");
    }

    partial void OnSelectedMemoryAreaChanged(string value) => RefreshMemorySnapshot();
    partial void OnMemoryStartAddressChanged(int value) => RefreshMemorySnapshot();
    partial void OnMemoryCountChanged(int value) => RefreshMemorySnapshot();

    private void RefreshMemorySnapshot()
    {
        try
        {
            int count = MemoryCount;
            if (count < 1) count = 1;
            if (count > 100) count = 100;

            var rows = _simulator.GetSnapshot(SelectedMemoryArea, MemoryStartAddress, count);
            if (_dispatcher.CheckAccess())
                ReplaceRows(rows);
            else
                _dispatcher.BeginInvoke(new Action(() => ReplaceRows(rows)));
        }
        catch
        {
            // Snapshot refresh must never interrupt server operation.
        }
    }

    private void ReplaceRows(SimulatorMemoryRow[] rows)
    {
        MemoryRows.Clear();
        foreach (var row in rows)
            MemoryRows.Add(row);
    }

    private void OnRequestReceived(object? sender, string message)
    {
        AppendLog("[REQ] " + message);
    }

    private void AppendLog(string line)
    {
        string stamped = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line;
        if (_dispatcher.CheckAccess()) { DoAppend(stamped); }
        else { _dispatcher.BeginInvoke(new Action(() => DoAppend(stamped))); }
    }

    private void DoAppend(string stamped)
    {
        LogLines.Add(stamped);
        if (LogLines.Count > LogCap)
        {
            int remove = LogLines.Count - LogCap;
            for (int i = 0; i < remove; i++) LogLines.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updateTimer?.Stop();
        _simulator.RequestReceived -= OnRequestReceived;
        _simulator.Stop();
        GC.SuppressFinalize(this);
    }
}
