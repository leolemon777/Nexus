using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Nexus.App.Services
{
    /// <summary>
    /// 数据导出服务 — 支持 CSV、JSON、TXT 批量导出。
    /// <para>对标 HSL DataExport，提供多格式数据导出。</para>
    /// </summary>
    public static class DataExportService
    {
        /// <summary>导出为 CSV 文件</summary>
        public static void ExportCsv(IEnumerable<ExportData> data, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,Protocol,Address,DataType,Value,Quality");
            foreach (var d in data)
                sb.AppendLine($"{d.Timestamp:O},{EscapeCsv(d.Protocol)},{EscapeCsv(d.Address)},{EscapeCsv(d.DataType)},{EscapeCsv(d.Value)},{EscapeCsv(d.Quality)}");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>导出为 JSON 文件</summary>
        public static void ExportJson(IEnumerable<ExportData> data, string filePath)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(data.ToList(), options);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>导出为 TXT (制表符分隔)</summary>
        public static void ExportTxt(IEnumerable<ExportData> data, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp\tProtocol\tAddress\tDataType\tValue\tQuality");
            foreach (var d in data)
                sb.AppendLine($"{d.Timestamp:O}\t{d.Protocol}\t{d.Address}\t{d.DataType}\t{d.Value}\t{d.Quality}");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>从 CSV 文件导入</summary>
        public static List<ExportData> ImportCsv(string filePath)
        {
            var result = new List<ExportData>();
            if (!File.Exists(filePath)) return result;

            foreach (var line in File.ReadAllLines(filePath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 5) continue;

                result.Add(new ExportData
                {
                    Timestamp = DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTime.Now,
                    Protocol = parts[1].Trim('"'),
                    Address = parts[2].Trim('"'),
                    DataType = parts[3].Trim('"'),
                    Value = parts[4].Trim('"'),
                    Quality = parts.Length > 5 ? parts[5].Trim('"') : "Good"
                });
            }
            return result;
        }

        /// <summary>从 JSON 文件导入</summary>
        public static List<ExportData> ImportJson(string filePath)
        {
            if (!File.Exists(filePath)) return new List<ExportData>();
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<ExportData>>(json) ?? new List<ExportData>();
        }

        private static string EscapeCsv(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return $"\"{s.Replace("\"", "\"\"")}\"";
            return s;
        }
    }

    public sealed class ExportData
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Protocol { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Quality { get; set; } = "Good";
    }
}
