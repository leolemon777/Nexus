using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Nexus.App.Services;

/// <summary>
/// Records data points to a JSONL file for playback.
/// </summary>
public sealed class DataRecorder : IDisposable
{
    private StreamWriter? _writer;
    private readonly object _lock = new();
    private bool _recording;
    private int _pointCount;

    public bool IsRecording => _recording;
    public int PointCount => _pointCount;

    public void StartRecording(string filePath)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
            _recording = true;
            _pointCount = 0;
        }
    }

    public void Record(string address, string alias, double value, DateTime timestamp)
    {
        if (!_recording || _writer == null) return;
        lock (_lock)
        {
            var entry = new { t = timestamp.ToString("O"), a = address, n = alias, v = value };
            _writer.WriteLine(JsonSerializer.Serialize(entry));
            _pointCount++;
        }
    }

    public void StopRecording()
    {
        lock (_lock)
        {
            _recording = false;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Dispose() => StopRecording();
}

/// <summary>
/// Represents a recorded data point for playback.
/// </summary>
public struct PlaybackPoint
{
    public DateTime Time { get; set; }
    public string Address { get; set; }
    public string Alias { get; set; }
    public double Value { get; set; }
}

/// <summary>
/// Loads and manages playback data from a JSONL file.
/// </summary>
public sealed class PlaybackData
{
    public List<PlaybackPoint> Points { get; } = new();
    public DateTime StartTime { get; private set; }
    public DateTime EndTime { get; private set; }
    public TimeSpan Duration => EndTime - StartTime;
    public int PointCount => Points.Count;

    public void LoadFromFile(string filePath)
    {
        Points.Clear();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var point = new PlaybackPoint
                {
                    Time = DateTime.Parse(root.GetProperty("t").GetString() ?? ""),
                    Address = root.GetProperty("a").GetString() ?? "",
                    Alias = root.TryGetProperty("n", out var n) ? n.GetString() ?? "" : "",
                    Value = root.GetProperty("v").GetDouble()
                };
                Points.Add(point);
            }
            catch { }
        }

        if (Points.Count > 0)
        {
            StartTime = Points[0].Time;
            EndTime = Points[Points.Count - 1].Time;
        }
    }

    public List<PlaybackPoint> GetPointsInWindow(DateTime windowStart, DateTime windowEnd)
    {
        var result = new List<PlaybackPoint>();
        foreach (var p in Points)
        {
            if (p.Time >= windowStart && p.Time <= windowEnd)
                result.Add(p);
        }
        return result;
    }

    public List<PlaybackPoint> GetPointsForAddress(string address, DateTime windowStart, DateTime windowEnd)
    {
        var result = new List<PlaybackPoint>();
        foreach (var p in Points)
        {
            if (p.Address == address && p.Time >= windowStart && p.Time <= windowEnd)
                result.Add(p);
        }
        return result;
    }
}
