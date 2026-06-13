using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexus;

namespace Nexus.App.Services
{
    public readonly struct DataPoint
    {
        public DateTime Time { get; }
        public double Value { get; }

        public DataPoint(DateTime time, double value)
        {
            Time = time;
            Value = value;
        }
    }

    public partial class MonitoredAddress : ObservableObject
    {
        public Guid Id { get; } = Guid.NewGuid();

        [ObservableProperty] private string _address = string.Empty;
        [ObservableProperty] private string _alias = string.Empty;
        [ObservableProperty] private string _dataType = "Int16";
        [ObservableProperty] private int _intervalMs = 1000;
        [ObservableProperty] private string _seriesColor = "#58A6FF";
        [ObservableProperty] private double _currentValue;
        [ObservableProperty] private string _currentValueText = "--";
        [ObservableProperty] private string _quality = "Pending";
        [ObservableProperty] private DateTime? _lastUpdateTime;

        /// <summary>报警上限。null 表示不启用上限报警。</summary>
        [ObservableProperty] private double? _alarmHigh;

        /// <summary>报警下限。null 表示不启用下限报警。</summary>
        [ObservableProperty] private double? _alarmLow;

        /// <summary>是否触发报警。</summary>
        [ObservableProperty] private bool _isAlarming;

        /// <summary>报警信息。</summary>
        [ObservableProperty] private string _alarmMessage = string.Empty;

        /// <summary>该地址所属设备。null 表示使用默认设备。</summary>
        public Guid? DeviceId { get; set; }

        public event EventHandler<(MonitoredAddress addr, string message)>? AlarmTriggered;

        private readonly List<DataPoint> _dataPoints = new();
        private readonly object _dataLock = new();
        private readonly int _maxPoints;
        private double _minValue = double.MaxValue;
        private double _maxValue = double.MinValue;

        public MonitoredAddress(int maxPoints = 1000)
        {
            _maxPoints = maxPoints;
        }

        public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Address : Alias;

        public void AddPoint(DataPoint point)
        {
            lock (_dataLock)
            {
                _dataPoints.Add(point);
                if (_dataPoints.Count > _maxPoints)
                    _dataPoints.RemoveAt(0);

                if (point.Value < _minValue) _minValue = point.Value;
                if (point.Value > _maxValue) _maxValue = point.Value;

                RecalcMinMax();
            }
            CurrentValue = point.Value;
            CurrentValueText = point.Value.ToString("G6");
            LastUpdateTime = point.Time;
            CheckAlarm(point.Value);
        }

        private void CheckAlarm(double value)
        {
            bool wasAlarming = IsAlarming;
            bool alarming = false;
            string msg = string.Empty;

            if (AlarmHigh.HasValue && value > AlarmHigh.Value)
            {
                alarming = true;
                msg = $"高报警: {value:F2} > {AlarmHigh.Value:F2}";
            }
            else if (AlarmLow.HasValue && value < AlarmLow.Value)
            {
                alarming = true;
                msg = $"低报警: {value:F2} < {AlarmLow.Value:F2}";
            }

            IsAlarming = alarming;
            AlarmMessage = msg;

            if (alarming && !wasAlarming)
                AlarmTriggered?.Invoke(this, (this, msg));
        }

        private void RecalcMinMax()
        {
            if (_dataPoints.Count == 0)
            {
                _minValue = double.MaxValue;
                _maxValue = double.MinValue;
                return;
            }
            _minValue = double.MaxValue;
            _maxValue = double.MinValue;
            for (int i = 0; i < _dataPoints.Count; i++)
            {
                var v = _dataPoints[i].Value;
                if (v < _minValue) _minValue = v;
                if (v > _maxValue) _maxValue = v;
            }
        }

        public List<DataPoint> GetSnapshot()
        {
            lock (_dataLock)
                return new List<DataPoint>(_dataPoints);
        }

        public void GetRange(out double min, out double max)
        {
            lock (_dataLock)
            {
                min = _minValue;
                max = _maxValue;
            }
        }

        public void ClearHistory()
        {
            lock (_dataLock)
            {
                _dataPoints.Clear();
                _minValue = double.MaxValue;
                _maxValue = double.MinValue;
            }
        }
    }

    public sealed class MonitorService : IAsyncDisposable, IDisposable
    {
        private readonly ConcurrentDictionary<Guid, TagEntry> _tags = new();
        private readonly ConcurrentDictionary<Guid, MonitoredAddress> _monitoredAddresses = new();
        private CancellationTokenSource? _cts;
        private Task? _pollLoop;

        private readonly ConcurrentDictionary<Guid, DeviceConnection> _devices = new();
        private DeviceConnection? _defaultDevice;
        private readonly object _deviceLock = new();

        private readonly AdvancedTagEngine _advancedTagEngine = new();

        public bool IsRunning => _pollLoop is not null;
        public int TagCount => _tags.Count;
        public event EventHandler<TagEntry>? TagValueChanged;
        public event EventHandler<(MonitoredAddress Address, DataPoint Point)>? OnDataPoint;

        public AdvancedTagEngine AdvancedTags => _advancedTagEngine;

        private System.IO.StreamWriter? _csvWriter;

        // ── Multi-device management ─────────────────────

        public void AddDevice(DeviceConnection connection)
        {
            _devices[connection.Id] = connection;
            lock (_deviceLock)
            {
                if (_defaultDevice == null) _defaultDevice = connection;
            }
        }

        public void RemoveDevice(Guid deviceId)
        {
            if (_devices.TryRemove(deviceId, out var conn))
            {
                lock (_deviceLock)
                {
                    if (_defaultDevice?.Id == deviceId)
                        _defaultDevice = _devices.Values.FirstOrDefault();
                }
                conn.Dispose();
            }
        }

        public DeviceConnection? GetDevice(Guid deviceId)
            => _devices.TryGetValue(deviceId, out var conn) ? conn : null;

        public IReadOnlyCollection<DeviceConnection> GetDevices()
            => _devices.Values.ToList().AsReadOnly();

        public void SetDefaultDevice(Guid deviceId)
        {
            if (_devices.TryGetValue(deviceId, out var conn))
            {
                lock (_deviceLock)
                    _defaultDevice = conn;
            }
        }

        public void SetDevice(IReadWriteDevice? device)
        {
            lock (_deviceLock)
            {
                if (_defaultDevice != null)
                    _defaultDevice.Device.Disconnect();
                if (device != null)
                {
                    var conn = new DeviceConnection(device, "Default", "", "");
                    _devices[conn.Id] = conn;
                    _defaultDevice = conn;
                }
                else
                {
                    _defaultDevice = null;
                }
            }
        }

        public Task StartAsync(CancellationToken ct = default)
        {
            if (IsRunning) return Task.CompletedTask;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pollLoop = PollLoopAsync(_cts.Token);
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_cts is null) return;
            _cts.Cancel();
            if (_pollLoop is not null)
                try { await _pollLoop; } catch (OperationCanceledException) { }
            _cts.Dispose();
            _cts = null;
            _pollLoop = null;
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _pollLoop = null;
        }

        // ── Tag management ────────────────────────────────

        public void AddTag(TagEntry tag) => _tags[tag.Id] = tag;

        public void RemoveTag(Guid id) => _tags.TryRemove(id, out _);

        public void ClearTags() => _tags.Clear();

        public IReadOnlyList<TagEntry> GetAllTags() => _tags.Values.ToList();

        public TagEntry? GetTag(Guid id) => _tags.TryGetValue(id, out var t) ? t : null;

        public void UpdateTagValue(Guid id, string value, string quality = "Good")
        {
            if (!_tags.TryGetValue(id, out var tag)) return;
            tag.LastValue = value;
            tag.Quality = quality;
            tag.LastUpdate = DateTime.Now;
            TagValueChanged?.Invoke(this, tag);
            WriteCsvLine(tag);
        }

        // ── Monitored address management ──────────────────

        public void AddMonitoredAddress(MonitoredAddress address)
            => _monitoredAddresses[address.Id] = address;

        public void RemoveMonitoredAddress(Guid id)
            => _monitoredAddresses.TryRemove(id, out _);

        public IReadOnlyList<MonitoredAddress> GetAllMonitoredAddresses()
            => _monitoredAddresses.Values.ToList();

        // ── CSV log ───────────────────────────────────────

        public void StartCsvLog(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _csvWriter = new StreamWriter(filePath, true, System.Text.Encoding.UTF8);
            _csvWriter.AutoFlush = true;
            _csvWriter.WriteLine("Timestamp,Name,Address,Value,Quality");
        }

        public void StopCsvLog()
        {
            try { _csvWriter?.Close(); } catch { }
            _csvWriter = null;
        }

        private void WriteCsvLine(TagEntry tag)
        {
            if (_csvWriter == null) return;
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{tag.Name},{tag.Address},{tag.LastValue},{tag.Quality}";
                _csvWriter.WriteLine(line);
            }
            catch { }
        }

        // ── JSON persistence ─────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public void SaveToFile(string filePath)
        {
            var list = _tags.Values.Select(t => new TagDto
            {
                Name = t.Name,
                Address = t.Address,
                DataType = t.DataType,
                ProtocolName = t.ProtocolName,
                PollIntervalMs = t.PollIntervalMs
            }).ToList();

            var json = JsonSerializer.Serialize(list, _jsonOpts);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, json);
        }

        public List<TagDto> LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath)) return new List<TagDto>();
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<TagDto>>(json, _jsonOpts) ?? new List<TagDto>();
        }

        // ── Polling loop ──────────────────────────────────

        private async Task PollLoopAsync(CancellationToken ct)
        {
            var nextPoll = new Dictionary<Guid, DateTime>();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    var now = DateTime.UtcNow;

                    foreach (var kvp in _monitoredAddresses)
                    {
                        var addr = kvp.Value;
                        if (!nextPoll.TryGetValue(kvp.Key, out var next) || now >= next)
                        {
                            nextPoll[kvp.Key] = now.AddMilliseconds(addr.IntervalMs);
                            await ReadAndUpdateAsync(addr, ct).ConfigureAwait(false);
                        }
                    }

                    var sourceValues = new Dictionary<string, double>();
                    foreach (var addr in _monitoredAddresses.Values)
                    {
                        if (double.TryParse(addr.CurrentValueText, out var val))
                            sourceValues[addr.Address] = val;
                    }
                    _advancedTagEngine.UpdateSourceValues(sourceValues);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private async Task ReadAndUpdateAsync(MonitoredAddress addr, CancellationToken ct)
        {
            DeviceConnection? targetDevice;
            lock (_deviceLock)
            {
                if (addr.DeviceId.HasValue)
                    targetDevice = GetDevice(addr.DeviceId.Value);
                else
                    targetDevice = null;
                targetDevice ??= _defaultDevice;
            }

            if (targetDevice == null || !targetDevice.IsConnected)
            {
                addr.Quality = "Bad";
                return;
            }

            var dev = targetDevice.Device;

            try
            {
                double value = double.NaN;
                switch (addr.DataType)
                {
                    case "Int16":
                        var r16 = await dev.ReadInt16Async(addr.Address).ConfigureAwait(false);
                        if (r16.IsSuccess) value = r16.Content;
                        break;
                    case "UInt16":
                        var ru16 = await dev.ReadUInt16Async(addr.Address).ConfigureAwait(false);
                        if (ru16.IsSuccess) value = ru16.Content;
                        break;
                    case "Int32":
                        var r32 = await dev.ReadInt32Async(addr.Address).ConfigureAwait(false);
                        if (r32.IsSuccess) value = r32.Content;
                        break;
                    case "UInt32":
                        var ru32 = await dev.ReadUInt32Async(addr.Address).ConfigureAwait(false);
                        if (ru32.IsSuccess) value = ru32.Content;
                        break;
                    case "Int64":
                        var r64 = await dev.ReadInt64Async(addr.Address).ConfigureAwait(false);
                        if (r64.IsSuccess) value = r64.Content;
                        break;
                    case "UInt64":
                        var ru64 = await dev.ReadUInt64Async(addr.Address).ConfigureAwait(false);
                        if (ru64.IsSuccess) value = ru64.Content;
                        break;
                    case "Float":
                        var rf = await dev.ReadFloatAsync(addr.Address).ConfigureAwait(false);
                        if (rf.IsSuccess) value = rf.Content;
                        break;
                    case "Double":
                        var rd = await dev.ReadDoubleAsync(addr.Address).ConfigureAwait(false);
                        if (rd.IsSuccess) value = rd.Content;
                        break;
                    case "Bool":
                        var rb = await dev.ReadBoolAsync(addr.Address).ConfigureAwait(false);
                        if (rb.IsSuccess) value = rb.Content ? 1 : 0;
                        break;
                    default:
                        var rd2 = await dev.ReadDoubleAsync(addr.Address).ConfigureAwait(false);
                        if (rd2.IsSuccess) value = rd2.Content;
                        break;
                }

                if (double.IsNaN(value))
                {
                    addr.Quality = "Bad";
                    return;
                }

                var point = new DataPoint(DateTime.Now, value);
                addr.Quality = "Good";
                addr.AddPoint(point);
                OnDataPoint?.Invoke(this, (addr, point));
            }
            catch
            {
                addr.Quality = "Bad";
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            StopCsvLog();
            foreach (var conn in _devices.Values)
                conn.Dispose();
            _devices.Clear();
            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            Stop();
            StopCsvLog();
            foreach (var conn in _devices.Values)
                conn.Dispose();
            _devices.Clear();
            GC.SuppressFinalize(this);
        }
    }

    public sealed class TagDto
    {
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string DataType { get; set; } = "Int16";
        public string ProtocolName { get; set; } = string.Empty;
        public int PollIntervalMs { get; set; } = 1000;
    }
}
