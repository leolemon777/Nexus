using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Nexus;

public enum AdvancedTagType
{
    Scaled,
    Calculated,
    Aggregated,
    Clamped,
    Deadband,
    RateOfChange,
    MovingAverage
}

public enum AggregationFunction
{
    Sum,
    Average,
    Min,
    Max,
    Count
}

public class AdvancedTag
{
    public string Name { get; set; } = string.Empty;
    public AdvancedTagType Type { get; set; }
    public string[] SourceTagNames { get; set; } = Array.Empty<string>();
    public string Expression { get; set; } = string.Empty;
    public double Multiplier { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public double MinValue { get; set; } = double.MinValue;
    public double MaxValue { get; set; } = double.MaxValue;
    public double DeadbandValue { get; set; } = 0.0;
    public AggregationFunction AggFunction { get; set; } = AggregationFunction.Average;
    public int WindowSize { get; set; } = 10;
    public string Unit { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class AdvancedTagEngine
{
    private readonly ConcurrentDictionary<string, AdvancedTag> _tags = new();
    private readonly ConcurrentDictionary<string, double> _currentValues = new();
    private readonly ConcurrentDictionary<string, List<double>> _history = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastUpdateTime = new();
    private readonly ConcurrentDictionary<string, double> _lastValue = new();

    public event EventHandler<(string Name, double Value)>? OnValueChanged;

    public void AddTag(AdvancedTag tag)
    {
        _tags[tag.Name] = tag;
        _currentValues[tag.Name] = 0;
    }

    public void RemoveTag(string name)
    {
        _tags.TryRemove(name, out _);
        _currentValues.TryRemove(name, out _);
        _history.TryRemove(name, out _);
        _lastUpdateTime.TryRemove(name, out _);
        _lastValue.TryRemove(name, out _);
    }

    public AdvancedTag? GetTag(string name) =>
        _tags.TryGetValue(name, out var tag) ? tag : null;

    public List<AdvancedTag> GetAllTags() => _tags.Values.ToList();

    public double GetValue(string name) =>
        _currentValues.TryGetValue(name, out var v) ? v : 0;

    public void UpdateSourceValues(Dictionary<string, double> sourceValues)
    {
        foreach (var kvp in _tags)
        {
            var tag = kvp.Value;
            double newValue;
            switch (tag.Type)
            {
                case AdvancedTagType.Scaled:
                    newValue = EvaluateScaled(tag, sourceValues);
                    break;
                case AdvancedTagType.Calculated:
                    newValue = EvaluateCalculated(tag, sourceValues);
                    break;
                case AdvancedTagType.Aggregated:
                    newValue = EvaluateAggregated(tag, sourceValues);
                    break;
                case AdvancedTagType.Clamped:
                    newValue = EvaluateClamped(tag, sourceValues);
                    break;
                case AdvancedTagType.Deadband:
                    newValue = EvaluateDeadband(tag, sourceValues);
                    break;
                case AdvancedTagType.RateOfChange:
                    newValue = EvaluateRateOfChange(tag, sourceValues);
                    break;
                case AdvancedTagType.MovingAverage:
                    newValue = EvaluateMovingAverage(tag, sourceValues);
                    break;
                default:
                    newValue = 0;
                    break;
            }

            double oldValue = _currentValues.GetOrAdd(kvp.Key, 0);
            if (Math.Abs(newValue - oldValue) > 1e-10)
            {
                _currentValues[kvp.Key] = newValue;
                OnValueChanged?.Invoke(this, (kvp.Key, newValue));
            }
        }
    }

    private double EvaluateScaled(AdvancedTag tag, Dictionary<string, double> source)
    {
        if (tag.SourceTagNames.Length == 0) return 0;
        double raw = source.TryGetValue(tag.SourceTagNames[0], out var v) ? v : 0;
        return raw * tag.Multiplier + tag.Offset;
    }

    private double EvaluateCalculated(AdvancedTag tag, Dictionary<string, double> source)
    {
        try
        {
            string expr = tag.Expression;
            foreach (var src in source)
            {
                int idx = expr.IndexOf(src.Key, StringComparison.OrdinalIgnoreCase);
                while (idx >= 0)
                {
                    expr = expr.Substring(0, idx) +
                           src.Value.ToString(CultureInfo.InvariantCulture) +
                           expr.Substring(idx + src.Key.Length);
                    idx = expr.IndexOf(src.Key, StringComparison.OrdinalIgnoreCase);
                }
            }
            return EvaluateSimpleMath(expr);
        }
        catch
        {
            return 0;
        }
    }

    private double EvaluateAggregated(AdvancedTag tag, Dictionary<string, double> source)
    {
        var values = new List<double>();
        foreach (var name in tag.SourceTagNames)
        {
            if (source.TryGetValue(name, out var v)) values.Add(v);
        }
        if (values.Count == 0) return 0;
        switch (tag.AggFunction)
        {
            case AggregationFunction.Sum: return values.Sum();
            case AggregationFunction.Average: return values.Average();
            case AggregationFunction.Min: return values.Min();
            case AggregationFunction.Max: return values.Max();
            case AggregationFunction.Count: return (double)values.Count;
            default: return 0;
        }
    }

    private double EvaluateClamped(AdvancedTag tag, Dictionary<string, double> source)
    {
        if (tag.SourceTagNames.Length == 0) return 0;
        double raw = source.TryGetValue(tag.SourceTagNames[0], out var v) ? v : 0;
        return Math.Max(tag.MinValue, Math.Min(tag.MaxValue, raw));
    }

    private double EvaluateDeadband(AdvancedTag tag, Dictionary<string, double> source)
    {
        if (tag.SourceTagNames.Length == 0) return 0;
        double raw = source.TryGetValue(tag.SourceTagNames[0], out var v) ? v : 0;
        double last = _lastValue.GetOrAdd(tag.Name, raw);
        if (Math.Abs(raw - last) >= tag.DeadbandValue)
        {
            _lastValue[tag.Name] = raw;
            return raw;
        }
        return last;
    }

    private double EvaluateRateOfChange(AdvancedTag tag, Dictionary<string, double> source)
    {
        if (tag.SourceTagNames.Length == 0) return 0;
        double raw = source.TryGetValue(tag.SourceTagNames[0], out var v) ? v : 0;
        var now = DateTime.UtcNow;
        var lastTime = _lastUpdateTime.GetOrAdd(tag.Name, now);
        double lastVal = _lastValue.GetOrAdd(tag.Name, raw);
        double timeDelta = (now - lastTime).TotalSeconds;
        if (timeDelta < 0.001) return 0;
        double rate = (raw - lastVal) / timeDelta;
        _lastValue[tag.Name] = raw;
        _lastUpdateTime[tag.Name] = now;
        return rate;
    }

    private double EvaluateMovingAverage(AdvancedTag tag, Dictionary<string, double> source)
    {
        if (tag.SourceTagNames.Length == 0) return 0;
        double raw = source.TryGetValue(tag.SourceTagNames[0], out var v) ? v : 0;
        var hist = _history.GetOrAdd(tag.Name, _ => new List<double>());
        lock (hist)
        {
            hist.Add(raw);
            if (hist.Count > tag.WindowSize) hist.RemoveAt(0);
            return hist.Average();
        }
    }

    internal static double EvaluateSimpleMath(string expr)
    {
        expr = expr.Trim();
        if (expr.Length == 0) return 0;
        if (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct))
            return direct;

        var tokens = Tokenize(expr);
        if (tokens.Count == 0) return 0;

        int pos = 0;
        double result = ParseExpression(tokens, ref pos);
        return result;
    }

    private static List<string> Tokenize(string expr)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < expr.Length)
        {
            char c = expr[i];
            if (c == ' ') { i++; continue; }
            if (c == '+' || c == '-' || c == '*' || c == '/' || c == '(' || c == ')')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (char.IsDigit(c) || c == '.')
            {
                int start = i;
                while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.' ||
                       expr[i] == 'E' || expr[i] == 'e' ||
                       ((expr[i] == '+' || expr[i] == '-') && i > start &&
                        (expr[i - 1] == 'E' || expr[i - 1] == 'e'))))
                    i++;
                tokens.Add(expr.Substring(start, i - start));
            }
            else
            {
                i++;
            }
        }
        return tokens;
    }

    private static double ParseExpression(List<string> tokens, ref int pos)
    {
        double left = ParseTerm(tokens, ref pos);
        while (pos < tokens.Count && (tokens[pos] == "+" || tokens[pos] == "-"))
        {
            string op = tokens[pos++];
            double right = ParseTerm(tokens, ref pos);
            left = op == "+" ? left + right : left - right;
        }
        return left;
    }

    private static double ParseTerm(List<string> tokens, ref int pos)
    {
        double left = ParseFactor(tokens, ref pos);
        while (pos < tokens.Count && (tokens[pos] == "*" || tokens[pos] == "/"))
        {
            string op = tokens[pos++];
            double right = ParseFactor(tokens, ref pos);
            left = op == "*" ? left * right : left / right;
        }
        return left;
    }

    private static double ParseFactor(List<string> tokens, ref int pos)
    {
        if (pos >= tokens.Count) return 0;
        string token = tokens[pos];
        if (token == "(")
        {
            pos++;
            double val = ParseExpression(tokens, ref pos);
            if (pos < tokens.Count && tokens[pos] == ")") pos++;
            return val;
        }
        if (token == "-")
        {
            pos++;
            return -ParseFactor(tokens, ref pos);
        }
        if (token == "+")
        {
            pos++;
            return ParseFactor(tokens, ref pos);
        }
        pos++;
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var val2);
        return val2;
    }
}
