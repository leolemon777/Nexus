using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;

namespace Nexus.App.ViewModels;

public partial class AlarmViewModel : ObservableObject, IDisposable
{
    private readonly AlarmService _alarmService;

    [ObservableProperty] private string _tagName = string.Empty;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _protocolName = string.Empty;
    [ObservableProperty] private double _threshold;
    [ObservableProperty] private int _selectedConditionIndex;
    [ObservableProperty] private int _selectedSeverityIndex = 1; // Warning
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private int _activeAlarmCount;
    [ObservableProperty] private int _totalRuleCount;

    public string[] Conditions { get; } = { "> 大于", "< 小于", "== 等于", "!= 不等于", ">= 大于等于", "<= 小于等于" };
    public string[] Severities { get; } = { "ℹ️ 信息", "⚠️ 警告", "🔴 严重" };
    public string[] Protocols { get; } = {
        "Modbus TCP", "Modbus RTU", "S7-1200/1500", "MC 协议 (Q/L/FX5U)",
        "FINS-TCP", "CIP (ControlLogix)", "Mewtocol (FP 系列)", "KV 系列上位通讯",
        "TwinCAT ADS", "DVP/AS 系列", "SPH/SPB 系列", "XGT 协议",
        "H3U/AM 系列", "2400/2500 调节器", "FBs 系列", "FANUC FOCAS",
        "GE SRTP", "信捷 Xinje", "KUKA EKI", "OPC UA"
    };

    public ObservableCollection<AlarmRule> Rules { get; } = new();
    public ObservableCollection<AlarmRecord> ActiveAlarms { get; } = new();
    public ObservableCollection<AlarmRecord> HistoryRecords { get; } = new();

    public AlarmViewModel(AlarmService alarmService)
    {
        _alarmService = alarmService;
        _alarmService.AlarmTriggered += OnAlarmTriggered;
        _alarmService.AlarmAcknowledged += OnAlarmAcknowledged;

        // Load existing rules
        foreach (var rule in _alarmService.Rules)
            Rules.Add(rule);

        foreach (var alarm in _alarmService.ActiveAlarms)
            ActiveAlarms.Add(alarm);

        foreach (var record in _alarmService.History)
            HistoryRecords.Add(record);

        UpdateCounts();
    }

    [RelayCommand]
    private void AddRule()
    {
        if (string.IsNullOrWhiteSpace(TagName) || string.IsNullOrWhiteSpace(Address))
        {
            MessageBox.Show("请填写标签名和地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var rule = new AlarmRule
        {
            TagName = TagName.Trim(),
            Address = Address.Trim(),
            ProtocolName = ProtocolName,
            Condition = (AlarmCondition)SelectedConditionIndex,
            Threshold = Threshold,
            Severity = (AlarmSeverity)SelectedSeverityIndex,
            Description = Description.Trim(),
            IsEnabled = true
        };

        _alarmService.AddRule(rule);
        Rules.Add(rule);
        UpdateCounts();

        TagName = string.Empty;
        Address = string.Empty;
        Description = string.Empty;
    }

    [RelayCommand]
    private void RemoveRule(AlarmRule? rule)
    {
        if (rule == null) return;
        _alarmService.RemoveRule(rule.Id);
        Rules.Remove(rule);
        UpdateCounts();
    }

    [RelayCommand]
    private void ToggleRule(AlarmRule? rule)
    {
        if (rule == null) return;
        rule.IsEnabled = !rule.IsEnabled;
        _alarmService.UpdateRule(rule);
    }

    [RelayCommand]
    private void AcknowledgeAlarm(AlarmRecord? record)
    {
        if (record == null) return;
        _alarmService.Acknowledge(record);
        ActiveAlarms.Remove(record);
        UpdateCounts();
    }

    [RelayCommand]
    private void AcknowledgeAll()
    {
        _alarmService.AcknowledgeAll();
        Application.Current.Dispatcher.Invoke(() =>
        {
            while (ActiveAlarms.Any(a => !a.IsAcknowledged))
                ActiveAlarms.Remove(ActiveAlarms.First(a => !a.IsAcknowledged));
        });
        UpdateCounts();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        HistoryRecords.Clear();
    }

    private void OnAlarmTriggered(object? sender, AlarmRecord record)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ActiveAlarms.Add(record);
            HistoryRecords.Add(record);
            UpdateCounts();
        });
    }

    private void OnAlarmAcknowledged(object? sender, AlarmRecord record)
    {
        Application.Current.Dispatcher.Invoke(() => UpdateCounts());
    }

    private void UpdateCounts()
    {
        ActiveAlarmCount = ActiveAlarms.Count(a => !a.IsAcknowledged);
        TotalRuleCount = Rules.Count;
    }

    public void Dispose()
    {
        _alarmService.AlarmTriggered -= OnAlarmTriggered;
        _alarmService.AlarmAcknowledged -= OnAlarmAcknowledged;
        GC.SuppressFinalize(this);
    }
}
