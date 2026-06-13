using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Nexus;

/// <summary>
/// Tag database — stores device tags with metadata for configuration and runtime use.
/// Similar to Kepware's tag management system.
/// </summary>
public sealed class TagDatabase
{
    private readonly Dictionary<string, DeviceTag> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _tags.Count; } }

    public void AddTag(DeviceTag tag)
    {
        lock (_lock) _tags[tag.FullName] = tag;
    }

    public void RemoveTag(string fullName)
    {
        lock (_lock) _tags.Remove(fullName);
    }

    public DeviceTag? GetTag(string fullName)
    {
        lock (_lock) return _tags.TryGetValue(fullName, out var tag) ? tag : null;
    }

    public List<DeviceTag> GetAllTags()
    {
        lock (_lock) return _tags.Values.ToList();
    }

    public List<DeviceTag> GetTagsByDevice(string deviceName)
    {
        lock (_lock) return _tags.Values.Where(t => t.DeviceName == deviceName).ToList();
    }

    public List<DeviceTag> GetTagsByGroup(string groupName)
    {
        lock (_lock) return _tags.Values.Where(t => t.GroupName == groupName).ToList();
    }

    public void Clear()
    {
        lock (_lock) _tags.Clear();
    }

    // ── Import/Export ──────────────────

    public void ExportToJson(string filePath)
    {
        var list = GetAllTags();
        var json = JsonHelper.SerializeTagList(list);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, json);
    }

    public int ImportFromJson(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var list = JsonHelper.DeserializeTagList(json);
        foreach (var tag in list) AddTag(tag);
        return list.Count;
    }

    public void ExportToCsv(string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FullName,DeviceName,GroupName,Address,DataType,Description,Unit,ScanRateMs,Enabled,ReadOnly,AlarmHigh,AlarmLow,ScaleMultiplier,ScaleOffset");
        foreach (var tag in GetAllTags())
        {
            sb.AppendLine($"{CsvEscape(tag.FullName)},{CsvEscape(tag.DeviceName)},{CsvEscape(tag.GroupName)},{CsvEscape(tag.Address)},{CsvEscape(tag.DataType)},{CsvEscape(tag.Description)},{CsvEscape(tag.Unit)},{tag.ScanRateMs},{tag.Enabled},{tag.ReadOnly},{tag.AlarmHigh?.ToString() ?? ""},{tag.AlarmLow?.ToString() ?? ""},{tag.ScaleMultiplier},{tag.ScaleOffset}");
        }
        File.WriteAllText(filePath, sb.ToString());
    }

    public int ImportFromCsv(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        int count = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var parts = ParseCsvLine(lines[i]);
            if (parts.Count < 6) continue;
            var tag = new DeviceTag
            {
                FullName = parts[0],
                DeviceName = parts[1],
                GroupName = parts[2],
                Address = parts[3],
                DataType = parts[4],
                Description = parts[5]
            };
            if (parts.Count > 6) tag.Unit = parts[6];
            if (parts.Count > 7 && int.TryParse(parts[7], out var scanRate)) tag.ScanRateMs = scanRate;
            if (parts.Count > 8 && bool.TryParse(parts[8], out var enabled)) tag.Enabled = enabled;
            if (parts.Count > 9 && bool.TryParse(parts[9], out var readOnly)) tag.ReadOnly = readOnly;
            if (parts.Count > 10 && double.TryParse(parts[10], out var ah)) tag.AlarmHigh = ah;
            if (parts.Count > 11 && double.TryParse(parts[11], out var al)) tag.AlarmLow = al;
            if (parts.Count > 12 && double.TryParse(parts[12], out var sm)) tag.ScaleMultiplier = sm;
            if (parts.Count > 13 && double.TryParse(parts[13], out var so)) tag.ScaleOffset = so;
            AddTag(tag);
            count++;
        }
        return count;
    }

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { result.Add(""); break; }
            if (line[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; }
                        else { i++; break; }
                    }
                    else { sb.Append(line[i]); i++; }
                }
                result.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ',') i++;
                result.Add(line.Substring(start, i - start).Trim());
                if (i < line.Length && line[i] == ',') i++;
            }
        }
        return result;
    }
}

/// <summary>
/// Represents a device tag in the tag database.
/// </summary>
public class DeviceTag
{
    public string FullName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string GroupName { get; set; } = "Default";
    public string Address { get; set; } = string.Empty;
    public string DataType { get; set; } = "Int16";
    public string Description { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public int ScanRateMs { get; set; } = 1000;
    public bool Enabled { get; set; } = true;
    public bool ReadOnly { get; set; } = false;
    public double? AlarmHigh { get; set; }
    public double? AlarmLow { get; set; }
    public double ScaleMultiplier { get; set; } = 1.0;
    public double ScaleOffset { get; set; } = 0.0;
    public string AccessLevel { get; set; } = "Operator";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastModified { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Description) ? FullName : Description;

    /// <summary>Apply scaling: ScaledValue = RawValue * ScaleMultiplier + ScaleOffset</summary>
    public double ApplyScale(double rawValue) => rawValue * ScaleMultiplier + ScaleOffset;

    /// <summary>Reverse scaling: RawValue = (ScaledValue - ScaleOffset) / ScaleMultiplier</summary>
    public double ReverseScale(double scaledValue) => ScaleMultiplier != 0 ? (scaledValue - ScaleOffset) / ScaleMultiplier : 0;
}

/// <summary>
/// Minimal JSON serializer for DeviceTag (netstandard2.0 compatible, no System.Text.Json).
/// </summary>
internal static class JsonHelper
{
    public static string SerializeTagList(List<DeviceTag> tags)
    {
        var sb = new StringBuilder();
        sb.Append("[\n");
        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0) sb.Append(",\n");
            sb.Append("  ");
            SerializeTag(sb, tags[i]);
        }
        sb.Append("\n]");
        return sb.ToString();
    }

    private static void SerializeTag(StringBuilder sb, DeviceTag t)
    {
        sb.Append('{');
        WriteStr(sb, "FullName", t.FullName); sb.Append(", ");
        WriteStr(sb, "DeviceName", t.DeviceName); sb.Append(", ");
        WriteStr(sb, "GroupName", t.GroupName); sb.Append(", ");
        WriteStr(sb, "Address", t.Address); sb.Append(", ");
        WriteStr(sb, "DataType", t.DataType); sb.Append(", ");
        WriteStr(sb, "Description", t.Description); sb.Append(", ");
        WriteStr(sb, "Unit", t.Unit); sb.Append(", ");
        WriteNum(sb, "ScanRateMs", t.ScanRateMs); sb.Append(", ");
        WriteBool(sb, "Enabled", t.Enabled); sb.Append(", ");
        WriteBool(sb, "ReadOnly", t.ReadOnly); sb.Append(", ");
        WriteNullableNum(sb, "AlarmHigh", t.AlarmHigh); sb.Append(", ");
        WriteNullableNum(sb, "AlarmLow", t.AlarmLow); sb.Append(", ");
        WriteNum(sb, "ScaleMultiplier", t.ScaleMultiplier); sb.Append(", ");
        WriteNum(sb, "ScaleOffset", t.ScaleOffset); sb.Append(", ");
        WriteStr(sb, "AccessLevel", t.AccessLevel); sb.Append(", ");
        WriteStr(sb, "CreatedAt", t.CreatedAt.ToString("o")); sb.Append(", ");
        WriteStr(sb, "LastModified", t.LastModified?.ToString("o") ?? "null");
        sb.Append('}');
    }

    private static void WriteStr(StringBuilder sb, string key, string val)
    {
        sb.Append('"').Append(EscapeJson(key)).Append("\": ");
        if (val == "null") sb.Append("null");
        else sb.Append('"').Append(EscapeJson(val)).Append('"');
    }

    private static void WriteNum(StringBuilder sb, string key, double val)
    {
        sb.Append('"').Append(key).Append("\": ").Append(val.ToString("G"));
    }

    private static void WriteNullableNum(StringBuilder sb, string key, double? val)
    {
        sb.Append('"').Append(key).Append("\": ");
        sb.Append(val.HasValue ? val.Value.ToString("G") : "null");
    }

    private static void WriteBool(StringBuilder sb, string key, bool val)
    {
        sb.Append('"').Append(key).Append("\": ").Append(val ? "true" : "false");
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public static List<DeviceTag> DeserializeTagList(string json)
    {
        var result = new List<DeviceTag>();
        json = json.Trim();
        if (!json.StartsWith("[")) return result;
        json = json.Substring(1).TrimStart();
        while (json.Length > 0)
        {
            json = json.TrimStart();
            if (json.StartsWith("]")) break;
            if (json.StartsWith(",")) { json = json.Substring(1).TrimStart(); }
            if (!json.StartsWith("{")) break;
            var (tag, rest) = ParseTagObject(json);
            if (tag != null) result.Add(tag);
            json = rest;
        }
        return result;
    }

    private static (DeviceTag?, string) ParseTagObject(string json)
    {
        if (!json.StartsWith("{")) return (null, json);
        json = json.Substring(1).TrimStart();
        var tag = new DeviceTag();
        while (json.Length > 0)
        {
            json = json.TrimStart();
            if (json.StartsWith("}"))
            {
                json = json.Substring(1);
                break;
            }
            if (json.StartsWith(",")) { json = json.Substring(1).TrimStart(); }
            var (key, rest1) = ParseJsonString(json);
            if (key == null) break;
            rest1 = rest1.TrimStart();
            if (!rest1.StartsWith(":")) break;
            rest1 = rest1.Substring(1).TrimStart();

            string? strVal = null;
            double? numVal = null;
            bool? boolVal = null;
            bool isNull = false;

            if (rest1.StartsWith("\""))
            {
                var (sv, r) = ParseJsonString(rest1);
                strVal = sv; rest1 = r;
            }
            else if (rest1.StartsWith("null"))
            {
                isNull = true; rest1 = rest1.Substring(4);
            }
            else if (rest1.StartsWith("true"))
            {
                boolVal = true; rest1 = rest1.Substring(4);
            }
            else if (rest1.StartsWith("false"))
            {
                boolVal = false; rest1 = rest1.Substring(5);
            }
            else
            {
                int end = 0;
                while (end < rest1.Length && rest1[end] != ',' && rest1[end] != '}' && rest1[end] != ' ')
                    end++;
                if (double.TryParse(rest1.Substring(0, end), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var nv))
                    numVal = nv;
                rest1 = rest1.Substring(end);
            }

            switch (key)
            {
                case "FullName": tag.FullName = strVal ?? ""; break;
                case "DeviceName": tag.DeviceName = strVal ?? ""; break;
                case "GroupName": tag.GroupName = strVal ?? "Default"; break;
                case "Address": tag.Address = strVal ?? ""; break;
                case "DataType": tag.DataType = strVal ?? "Int16"; break;
                case "Description": tag.Description = strVal ?? ""; break;
                case "Unit": tag.Unit = strVal ?? ""; break;
                case "ScanRateMs": if (numVal.HasValue) tag.ScanRateMs = (int)numVal.Value; break;
                case "Enabled": tag.Enabled = boolVal ?? true; break;
                case "ReadOnly": tag.ReadOnly = boolVal ?? false; break;
                case "AlarmHigh": tag.AlarmHigh = isNull ? null : numVal; break;
                case "AlarmLow": tag.AlarmLow = isNull ? null : numVal; break;
                case "ScaleMultiplier": if (numVal.HasValue) tag.ScaleMultiplier = numVal.Value; break;
                case "ScaleOffset": if (numVal.HasValue) tag.ScaleOffset = numVal.Value; break;
                case "AccessLevel": tag.AccessLevel = strVal ?? "Operator"; break;
                case "CreatedAt":
                    if (strVal != null && DateTime.TryParse(strVal, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ct))
                        tag.CreatedAt = ct;
                    break;
                case "LastModified":
                    if (!isNull && strVal != null && DateTime.TryParse(strVal, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lm))
                        tag.LastModified = lm;
                    break;
            }
            json = rest1;
        }
        return (tag, json);
    }

    private static (string?, string) ParseJsonString(string json)
    {
        if (!json.StartsWith("\"")) return (null, json);
        int i = 1;
        var sb = new StringBuilder();
        while (i < json.Length)
        {
            if (json[i] == '\\')
            {
                i++;
                if (i >= json.Length) break;
                switch (json[i])
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append(json[i]); break;
                }
                i++;
            }
            else if (json[i] == '"')
            {
                i++;
                return (sb.ToString(), json.Substring(i));
            }
            else
            {
                sb.Append(json[i]);
                i++;
            }
        }
        return (sb.ToString(), json.Substring(i));
    }
}
