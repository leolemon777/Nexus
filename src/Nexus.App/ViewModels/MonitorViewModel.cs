using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;
using Nexus;

namespace Nexus.App.ViewModels
{
    public partial class MonitorViewModel : ObservableObject, IDisposable
    {
        private readonly MonitorService _service = new();
        private readonly Dispatcher _dispatcher;
        private bool _disposed;

        public ObservableCollection<TagEntry> Tags { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();
        public ObservableCollection<MonitoredAddress> MonitoredAddresses { get; } = new();

        [ObservableProperty] private bool _isPolling;
        [ObservableProperty] private int _tagCount;
        [ObservableProperty] private string _csvLogPath = string.Empty;
        [ObservableProperty] private bool _isCsvLogging;

        [ObservableProperty] private string _newTagName = string.Empty;
        [ObservableProperty] private string _newTagAddress = "D100";
        [ObservableProperty] private string _newTagDataType = "Int16";
        [ObservableProperty] private string _newTagProtocol = "Modbus TCP";

        [ObservableProperty] private string _newMonAddress = "D100";
        [ObservableProperty] private string _newMonAlias = string.Empty;
        [ObservableProperty] private string _newMonDataType = "Int16";
        [ObservableProperty] private int _newMonIntervalMs = 1000;
        [ObservableProperty] private string _newMonColor = "#58A6FF";
        [ObservableProperty] private int _monitoredCount;

        [ObservableProperty] private int _maxDataPoints = 1000;
        [ObservableProperty] private int _chartTimeWindowSeconds = 60;
        [ObservableProperty] private bool _isDeviceConnected;

        public string[] DataTypes { get; } =
            { "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double", "String", "Bool" };

        public string[] Protocols { get; } =
            { "Modbus TCP", "Modbus RTU", "Siemens S7", "Mitsubishi MC", "Omron FINS",
              "Allen-Bradley", "Keyence KV", "Panasonic", "Beckhoff ADS", "Delta",
              "Fuji", "LS XGT", "Inovance", "Eurotherm", "Fatek FBs" };

        public string[] ChartColorPalette { get; } =
        {
            "#58A6FF", "#3FB950", "#D29922", "#F85149", "#BC8CFF",
            "#FF7B72", "#79C0FF", "#56D364", "#E3B341", "#F778BA",
            "#A5D6FF", "#7EE787", "#F2CC60", "#FF9EB7", "#D2A8FF"
        };

        public int[] IntervalPresets { get; } = { 100, 250, 500, 1000, 2000, 5000 };

        private const int LogCap = 300;
        private static readonly string TagsFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Nexus", "tags.json");

        private static readonly string MonitoredFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Nexus", "monitored_addresses.json");

        public MonitorViewModel()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _service.TagValueChanged += OnTagValueChanged;
            _service.OnDataPoint += OnDataPointReceived;
            LoadSavedTags();
            LoadMonitoredAddresses();
        }

        public void SetDevice(IReadWriteDevice? device)
        {
            _service.SetDevice(device);
            IsDeviceConnected = device?.IsConnected ?? false;
        }

        // ── Tag commands (existing) ──────────────────────

        [RelayCommand]
        private void AddTag()
        {
            if (string.IsNullOrWhiteSpace(NewTagName))
                NewTagName = NewTagAddress;
            if (string.IsNullOrWhiteSpace(NewTagAddress)) return;

            var tag = new TagEntry
            {
                Name = NewTagName.Trim(),
                Address = NewTagAddress.Trim(),
                DataType = NewTagDataType,
                ProtocolName = NewTagProtocol,
                PollIntervalMs = 1000
            };

            _service.AddTag(tag);
            Tags.Add(tag);
            TagCount = Tags.Count;
            AppendLog($"[+] 添加标签: {tag.Name} ({tag.Address})");

            NewTagName = string.Empty;
            NewTagAddress = "D100";
            SaveTags();
        }

        [RelayCommand]
        private void RemoveTag(TagEntry? tag)
        {
            if (tag == null) return;
            _service.RemoveTag(tag.Id);
            Tags.Remove(tag);
            TagCount = Tags.Count;
            AppendLog($"[-] 移除标签: {tag.Name}");
            SaveTags();
        }

        [RelayCommand]
        private void ClearAllTags()
        {
            _service.ClearTags();
            Tags.Clear();
            TagCount = 0;
            AppendLog("[--] 已清除所有标签");
            SaveTags();
        }

        [RelayCommand]
        private async Task TogglePollingAsync()
        {
            if (IsPolling)
            {
                await _service.StopAsync();
                IsPolling = false;
                AppendLog("[||] 轮询已暂停");
            }
            else
            {
                await _service.StartAsync();
                IsPolling = true;
                AppendLog("[>] 轮询已启动");
            }
        }

        [RelayCommand]
        private void StartCsvLog()
        {
            if (IsCsvLogging) return;
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"Nexus_Monitor_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _service.StartCsvLog(path);
            CsvLogPath = path;
            IsCsvLogging = true;
            AppendLog($"[CSV] 日志已启动: {path}");
        }

        [RelayCommand]
        private void StopCsvLog()
        {
            if (!IsCsvLogging) return;
            _service.StopCsvLog();
            IsCsvLogging = false;
            AppendLog("[CSV] 日志已停止");
        }

        [RelayCommand]
        private void ClearLog() => LogLines.Clear();

        // ── Monitored address commands ───────────────────

        [RelayCommand]
        private void AddMonitoredAddress()
        {
            if (string.IsNullOrWhiteSpace(NewMonAddress)) return;

            var addr = new MonitoredAddress(MaxDataPoints)
            {
                Address = NewMonAddress.Trim(),
                Alias = NewMonAlias.Trim(),
                DataType = NewMonDataType,
                IntervalMs = NewMonIntervalMs,
                SeriesColor = NewMonColor
            };

            _service.AddMonitoredAddress(addr);
            MonitoredAddresses.Add(addr);
            MonitoredCount = MonitoredAddresses.Count;
            AppendLog($"[+] 添加监控地址: {addr.DisplayName} ({addr.Address})");

            NewMonAddress = "D100";
            NewMonAlias = string.Empty;
            NewMonIntervalMs = 1000;
            AdvanceColor();
            SaveMonitoredAddresses();
        }

        [RelayCommand]
        private void RemoveMonitoredAddress(MonitoredAddress? addr)
        {
            if (addr == null) return;
            _service.RemoveMonitoredAddress(addr.Id);
            MonitoredAddresses.Remove(addr);
            MonitoredCount = MonitoredAddresses.Count;
            AppendLog($"[-] 移除监控地址: {addr.DisplayName}");
            SaveMonitoredAddresses();
        }

        [RelayCommand]
        private void BatchAddMonitoredAddresses(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // 支持格式：每行一个地址，或逗号/分号分隔
            // 可选格式: "Address" 或 "Address,Alias" 或 "Address,Alias,DataType"
            var lines = text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            int added = 0;

            foreach (var line in lines)
            {
                var parts = line.Trim().Split(new[] { '|' }, StringSplitOptions.None);
                if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) continue;

                var addr = new MonitoredAddress(MaxDataPoints)
                {
                    Address = parts[0].Trim(),
                    Alias = parts.Length > 1 ? parts[1].Trim() : "",
                    DataType = parts.Length > 2 ? parts[2].Trim() : NewMonDataType,
                    IntervalMs = NewMonIntervalMs,
                    SeriesColor = ChartColorPalette[MonitoredAddresses.Count % ChartColorPalette.Length]
                };

                // 避免重复地址
                if (MonitoredAddresses.Any(a => a.Address == addr.Address)) continue;

                _service.AddMonitoredAddress(addr);
                MonitoredAddresses.Add(addr);
                added++;
            }

            MonitoredCount = MonitoredAddresses.Count;
            if (added > 0)
            {
                AdvanceColor();
                SaveMonitoredAddresses();
                AppendLog($"[+] 批量添加 {added} 个监控地址");
            }
        }

        [RelayCommand]
        private void ClearAllMonitoredAddresses()
        {
            foreach (var addr in MonitoredAddresses)
                _service.RemoveMonitoredAddress(addr.Id);
            MonitoredAddresses.Clear();
            MonitoredCount = 0;
            AppendLog("[--] 已清除所有监控地址");
            SaveMonitoredAddresses();
        }

        [RelayCommand]
        private void ClearHistory()
        {
            foreach (var addr in MonitoredAddresses)
                addr.ClearHistory();
            AppendLog("[CLR] 已清除历史数据");
        }

        [RelayCommand]
        private void ExportCsv()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Nexus_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                var sb = new StringBuilder();
                sb.AppendLine("Time,Address,Alias,Value");
                foreach (var addr in MonitoredAddresses)
                {
                    var points = addr.GetSnapshot();
                    foreach (var p in points)
                        sb.AppendLine($"{p.Time:yyyy-MM-dd HH:mm:ss.fff},{addr.Address},{addr.Alias},{p.Value}");
                }
                File.WriteAllText(path, sb.ToString());
                AppendLog($"[CSV] 已导出到: {path}");
            }
            catch (Exception ex)
            {
                AppendLog($"[ERR] CSV 导出失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ExportJson()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Nexus_Export_{DateTime.Now:yyyyMMdd_HHmmss}.json");

                var data = MonitoredAddresses.Select(addr => new
                {
                    address = addr.Address,
                    alias = addr.Alias,
                    dataType = addr.DataType,
                    color = addr.SeriesColor,
                    points = addr.GetSnapshot().Select(p => new
                    {
                        time = p.Time.ToString("O"),
                        value = p.Value
                    }).ToArray()
                }).ToArray();

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                AppendLog($"[JSON] 已导出到: {path}");
            }
            catch (Exception ex)
            {
                AppendLog($"[ERR] JSON 导出失败: {ex.Message}");
            }
        }

        // ── Data point handler ───────────────────────────

        private void OnDataPointReceived(object? sender, (MonitoredAddress Address, DataPoint Point) e)
        {
            // Chart updates are driven by a timer in RealtimeChart; no per-point UI dispatch needed
        }

        private void OnTagValueChanged(object? sender, TagEntry tag)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                var existing = Tags.FirstOrDefault(t => t.Id == tag.Id);
                if (existing != null)
                {
                    existing.LastValue = tag.LastValue;
                    existing.Quality = tag.Quality;
                    existing.LastUpdate = tag.LastUpdate;
                    int idx = Tags.IndexOf(existing);
                    if (idx >= 0)
                        Tags[idx] = existing;
                }
            }));
        }

        // ── Persistence ──────────────────────────────────

        private void SaveTags()
        {
            try { _service.SaveToFile(TagsFilePath); }
            catch (Exception ex) { AppendLog($"[ERR] 保存标签失败: {ex.Message}"); }
        }

        private void LoadSavedTags()
        {
            try
            {
                var dtos = _service.LoadFromFile(TagsFilePath);
                foreach (var dto in dtos)
                {
                    var tag = new TagEntry
                    {
                        Name = dto.Name,
                        Address = dto.Address,
                        DataType = dto.DataType,
                        ProtocolName = dto.ProtocolName,
                        PollIntervalMs = dto.PollIntervalMs
                    };
                    _service.AddTag(tag);
                    Tags.Add(tag);
                }
                TagCount = Tags.Count;
                if (Tags.Count > 0)
                    AppendLog($"[OK] 已加载 {Tags.Count} 个保存的标签");
            }
            catch { }
        }

        private void SaveMonitoredAddresses()
        {
            try
            {
                var list = MonitoredAddresses.Select(a => new MonitoredAddressDto
                {
                    Address = a.Address,
                    Alias = a.Alias,
                    DataType = a.DataType,
                    IntervalMs = a.IntervalMs,
                    SeriesColor = a.SeriesColor
                }).ToList();

                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                var dir = Path.GetDirectoryName(MonitoredFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(MonitoredFilePath, json);
            }
            catch (Exception ex) { AppendLog($"[ERR] 保存监控地址失败: {ex.Message}"); }
        }

        private void LoadMonitoredAddresses()
        {
            try
            {
                if (!File.Exists(MonitoredFilePath)) return;
                var json = File.ReadAllText(MonitoredFilePath);
                var list = JsonSerializer.Deserialize<List<MonitoredAddressDto>>(json) ?? new();
                foreach (var dto in list)
                {
                    var addr = new MonitoredAddress(MaxDataPoints)
                    {
                        Address = dto.Address,
                        Alias = dto.Alias,
                        DataType = dto.DataType,
                        IntervalMs = dto.IntervalMs,
                        SeriesColor = dto.SeriesColor
                    };
                    _service.AddMonitoredAddress(addr);
                    MonitoredAddresses.Add(addr);
                }
                MonitoredCount = MonitoredAddresses.Count;
                if (MonitoredAddresses.Count > 0)
                    AppendLog($"[OK] 已加载 {MonitoredAddresses.Count} 个监控地址");
            }
            catch { }
        }

        private void AdvanceColor()
        {
            int idx = Array.IndexOf(ChartColorPalette, NewMonColor);
            idx = (idx + 1) % ChartColorPalette.Length;
            NewMonColor = ChartColorPalette[idx];
        }

        // ── Logging ──────────────────────────────────────

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
            SaveTags();
            SaveMonitoredAddresses();
            _service.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    internal sealed class MonitoredAddressDto
    {
        public string Address { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public string DataType { get; set; } = "Int16";
        public int IntervalMs { get; set; } = 1000;
        public string SeriesColor { get; set; } = "#58A6FF";
    }
}
