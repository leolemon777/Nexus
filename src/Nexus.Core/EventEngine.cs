using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nexus;

/// <summary>
/// Event-driven data collection engine.
/// Instead of polling at fixed intervals, fire events when values change.
/// </summary>
public sealed class EventEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, EventRule> _rules = new();
    private readonly ConcurrentDictionary<string, double> _lastValues = new();
    
    public event EventHandler<DataChangeEvent>? OnDataChanged;
    public event EventHandler<AlarmEvent>? OnAlarm;
    
    public void AddRule(EventRule rule)
    {
        _rules[rule.TagName] = rule;
    }
    
    public void RemoveRule(string tagName)
    {
        _rules.TryRemove(tagName, out _);
        _lastValues.TryRemove(tagName, out _);
    }
    
    /// <summary>
    /// Process a new data point. Fires events if conditions are met.
    /// </summary>
    public void ProcessValue(string tagName, double value, DateTime timestamp)
    {
        double lastValue = _lastValues.GetOrAdd(tagName, value);
        
        if (_rules.TryGetValue(tagName, out var rule))
        {
            // On-change detection
            if (rule.TriggerOnChange)
            {
                double change = Math.Abs(value - lastValue);
                if (change >= rule.Deadband)
                {
                    OnDataChanged?.Invoke(this, new DataChangeEvent
                    {
                        TagName = tagName,
                        OldValue = lastValue,
                        NewValue = value,
                        Timestamp = timestamp,
                        ChangeAmount = change
                    });
                    _lastValues[tagName] = value;
                }
            }
            
            // Alarm detection
            if (rule.AlarmHigh.HasValue && value > rule.AlarmHigh.Value)
            {
                OnAlarm?.Invoke(this, new AlarmEvent
                {
                    TagName = tagName,
                    Value = value,
                    Threshold = rule.AlarmHigh.Value,
                    Type = AlarmType.High,
                    Timestamp = timestamp,
                    Message = $"{tagName}: {value:F2} > 高限 {rule.AlarmHigh.Value:F2}"
                });
            }
            else if (rule.AlarmLow.HasValue && value < rule.AlarmLow.Value)
            {
                OnAlarm?.Invoke(this, new AlarmEvent
                {
                    TagName = tagName,
                    Value = value,
                    Threshold = rule.AlarmLow.Value,
                    Type = AlarmType.Low,
                    Timestamp = timestamp,
                    Message = $"{tagName}: {value:F2} < 低限 {rule.AlarmLow.Value:F2}"
                });
            }
            
            // Rate of change alarm
            if (rule.MaxRateOfChange.HasValue)
            {
                double rate = Math.Abs(value - lastValue); // simplified: per-sample rate
                if (rate > rule.MaxRateOfChange.Value)
                {
                    OnAlarm?.Invoke(this, new AlarmEvent
                    {
                        TagName = tagName,
                        Value = rate,
                        Threshold = rule.MaxRateOfChange.Value,
                        Type = AlarmType.RateOfChange,
                        Timestamp = timestamp,
                        Message = $"{tagName}: 变化率 {rate:F2} > {rule.MaxRateOfChange.Value:F2}"
                    });
                }
            }
        }
        else
        {
            // No rule, just track value
            _lastValues[tagName] = value;
        }
    }
    
    public List<EventRule> GetAllRules() => new List<EventRule>(_rules.Values);
    
    public void Dispose()
    {
        _rules.Clear();
        _lastValues.Clear();
    }
}

public class EventRule
{
    public string TagName { get; set; } = string.Empty;
    public bool TriggerOnChange { get; set; } = true;
    public double Deadband { get; set; } = 0.0;
    public double? AlarmHigh { get; set; }
    public double? AlarmLow { get; set; }
    public double? MaxRateOfChange { get; set; }
}

public class DataChangeEvent
{
    public string TagName { get; set; } = string.Empty;
    public double OldValue { get; set; }
    public double NewValue { get; set; }
    public DateTime Timestamp { get; set; }
    public double ChangeAmount { get; set; }
}

public class AlarmEvent
{
    public string TagName { get; set; } = string.Empty;
    public double Value { get; set; }
    public double Threshold { get; set; }
    public AlarmType Type { get; set; }
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = string.Empty;
}

public enum AlarmType
{
    High,
    Low,
    RateOfChange,
    Deviation
}
