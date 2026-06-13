using System;

namespace Nexus.App.Models;

public enum HmiElementType
{
    Tank,
    Pump,
    Valve,
    Pipe,
    Sensor,
    Label,
    Button,
    Indicator,
    Gauge,
    Chart
}

public class HmiElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public HmiElementType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 80;
    public double Height { get; set; } = 80;
    public string Label { get; set; } = string.Empty;
    public string BoundAddress { get; set; } = string.Empty;
    public string DataType { get; set; } = "Float";
    public double MinValue { get; set; } = 0;
    public double MaxValue { get; set; } = 100;
    public string Color { get; set; } = "#58A6FF";
    public double CurrentValue { get; set; }
    public string Unit { get; set; } = string.Empty;
    public bool IsAlarming { get; set; }
    public double? AlarmHigh { get; set; }
    public double? AlarmLow { get; set; }
}
