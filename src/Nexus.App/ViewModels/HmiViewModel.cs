using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Models;

namespace Nexus.App.ViewModels;

public partial class HmiViewModel : ObservableObject, IDisposable
{
    private IReadWriteDevice? _device;
    private readonly Dispatcher _dispatcher;
    private System.Threading.Timer? _pollTimer;

    public ObservableCollection<HmiElement> Elements { get; } = new();

    [ObservableProperty] private bool _isEditMode = true;
    [ObservableProperty] private HmiElementType _selectedTool = HmiElementType.Tank;
    [ObservableProperty] private HmiElement? _selectedElement;
    [ObservableProperty] private bool _isPolling;
    [ObservableProperty] private string _dashboardName = "新建工艺图";

    public HmiElementType[] ToolTypes { get; } = (HmiElementType[])Enum.GetValues(typeof(HmiElementType));

    private static readonly string SavePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexus", "hmi_dashboards");

    public HmiViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void SetDevice(IReadWriteDevice? device)
    {
        _device = device;
    }

    [RelayCommand]
    private void AddElement()
    {
        var element = new HmiElement
        {
            Type = SelectedTool,
            X = 100,
            Y = 100,
            Width = SelectedTool switch
            {
                HmiElementType.Pipe => 120,
                HmiElementType.Label => 100,
                HmiElementType.Sensor => 60,
                _ => 80
            },
            Height = SelectedTool switch
            {
                HmiElementType.Pipe => 20,
                HmiElementType.Label => 30,
                HmiElementType.Sensor => 60,
                _ => 80
            },
            Label = SelectedTool switch
            {
                HmiElementType.Tank => "TK-101",
                HmiElementType.Pump => "P-101",
                HmiElementType.Valve => "V-101",
                HmiElementType.Sensor => "TIC-101",
                HmiElementType.Label => "标签",
                HmiElementType.Button => "按钮",
                HmiElementType.Indicator => "指示灯",
                HmiElementType.Gauge => "仪表",
                HmiElementType.Chart => "趋势",
                HmiElementType.Pipe => "管道",
                _ => ""
            }
        };
        Elements.Add(element);
        SelectedElement = element;
    }

    [RelayCommand]
    private void DeleteElement(HmiElement? element)
    {
        if (element == null) return;
        Elements.Remove(element);
        if (SelectedElement?.Id == element.Id) SelectedElement = null;
    }

    [RelayCommand]
    private void StartPolling()
    {
        if (IsPolling || _device == null) return;
        IsPolling = true;
        _pollTimer = new System.Threading.Timer(_ => PollAll(), null, 0, 1000);
    }

    [RelayCommand]
    private void StopPolling()
    {
        IsPolling = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void PollAll()
    {
        if (_device == null || !_device.IsConnected) return;
        foreach (var element in Elements)
        {
            if (string.IsNullOrWhiteSpace(element.BoundAddress)) continue;
            try
            {
                var result = _device.ReadFloat(element.BoundAddress);
                if (result.IsSuccess)
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        element.CurrentValue = result.Content;
                        element.IsAlarming = (element.AlarmHigh.HasValue && element.CurrentValue > element.AlarmHigh.Value) ||
                                            (element.AlarmLow.HasValue && element.CurrentValue < element.AlarmLow.Value);
                    });
                }
            }
            catch { }
        }
    }

    [RelayCommand]
    private void SaveDashboard()
    {
        Directory.CreateDirectory(SavePath);
        var json = JsonSerializer.Serialize(Elements.ToList(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(SavePath, $"{DashboardName}.json"), json);
    }

    [RelayCommand]
    private void LoadDashboard(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var path = Path.Combine(SavePath, $"{name}.json");
        if (!File.Exists(path)) return;
        var json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<HmiElement[]>(json) ?? Array.Empty<HmiElement>();
        Elements.Clear();
        foreach (var e in list) Elements.Add(e);
        DashboardName = name;
    }

    public string[] GetSavedDashboards()
    {
        if (!Directory.Exists(SavePath)) return Array.Empty<string>();
        return Directory.GetFiles(SavePath, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToArray();
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
