using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    /// <summary>
    /// 数据采集引擎 — 多设备、多地址的统一轮询调度器。
    /// 支持数据变更事件、数据质量追踪、可扩展的数据输出接口 (IDataSink)。
    /// 这是 HSL 不提供的差异化特性。
    /// </summary>
    public sealed class DataAcquisitionEngine : IDisposable
    {
        private readonly ConcurrentDictionary<string, DevicePoller> _pollers = new();
        private readonly List<IDataSink> _sinks = new();
        private readonly object _sinkLock = new object();
        private CancellationTokenSource? _cts;
        private bool _disposed;

        /// <summary>采集引擎是否正在运行。</summary>
        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        /// <summary>当前已注册的设备数量。</summary>
        public int DeviceCount => _pollers.Count;

        /// <summary>已注册的数据接收器数量。</summary>
        public int SinkCount { get { lock (_sinkLock) { return _sinks.Count; } } }

        /// <summary>数据变更事件（带质量标签）。</summary>
        public event EventHandler<DataSampleEventArgs>? OnSample;

        /// <summary>采集错误事件。</summary>
        public event EventHandler<DataErrorEventArgs>? OnError;

        // ── 当前值查询 ────────────────────────────

        /// <summary>获取所有设备所有采集点的当前值。</summary>
        public Dictionary<string, string> GetCurrentValues()
        {
            var result = new Dictionary<string, string>();
            foreach (var kvp in _pollers)
            {
                var values = kvp.Value.GetCurrentValues();
                foreach (var v in values)
                    result[v.Key] = v.Value;
            }
            return result;
        }

        // ── CSV 导出 ──────────────────────────────

        /// <summary>将内存接收器中的数据导出为 CSV 文件。</summary>
        public void ExportToCsv(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));

            var dir = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            lock (_sinkLock)
            {
                using (var writer = new System.IO.StreamWriter(filePath, false, System.Text.Encoding.UTF8))
                {
                    writer.WriteLine("Timestamp,DeviceName,Address,DataType,Tag,Quality,Value");
                    foreach (var sink in _sinks)
                    {
                        if (sink is MemoryDataSink memSink)
                        {
                            foreach (var sample in memSink.GetAll())
                            {
                                writer.WriteLine($"{sample.Timestamp:yyyy-MM-dd HH:mm:ss.fff}," +
                                    $"{EscapeCsv(sample.DeviceName)}," +
                                    $"{EscapeCsv(sample.Address)}," +
                                    $"{EscapeCsv(sample.DataType)}," +
                                    $"{EscapeCsv(sample.Tag ?? "")}," +
                                    $"{EscapeCsv(sample.Quality)}," +
                                    $"{EscapeCsv(sample.Value)}");
                            }
                        }
                    }
                }
            }
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        // ── 数据接收器管理 ─────────────────────────

        /// <summary>添加数据接收器（如控制台、文件、数据库、MQTT 等）。</summary>
        public void AddSink(IDataSink sink)
        {
            lock (_sinkLock) { _sinks.Add(sink); }
        }

        /// <summary>移除数据接收器。</summary>
        public bool RemoveSink(IDataSink sink)
        {
            lock (_sinkLock) { return _sinks.Remove(sink); }
        }

        // ── 设备和地址注册 ─────────────────────────

        /// <summary>注册一个设备及其采集地址列表。</summary>
        public void RegisterDevice(string name, IReadWriteDevice device, PollConfig config)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            var poller = new DevicePoller(name, device, config);
            _pollers[name] = poller;
        }

        /// <summary>为已注册的设备添加一个采集点。</summary>
        public void AddPoint(string deviceName, string address, string dataType = "Int16", string? tag = null)
        {
            if (_pollers.TryGetValue(deviceName, out var poller))
                poller.AddPoint(address, dataType, tag);
            else
                throw new InvalidOperationException($"Device '{deviceName}' is not registered.");
        }

        /// <summary>移除一个采集点。</summary>
        public void RemovePoint(string deviceName, string address)
        {
            if (_pollers.TryGetValue(deviceName, out var poller))
                poller.RemovePoint(address);
        }

        /// <summary>移除整个设备。</summary>
        public void UnregisterDevice(string deviceName)
        {
            if (_pollers.TryRemove(deviceName, out var poller))
                poller.Dispose();
        }

        // ── 启动/停止 ─────────────────────────────

        /// <summary>启动所有设备的轮询。</summary>
        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            foreach (var kvp in _pollers)
                kvp.Value.Start(_cts.Token, OnSampleReceived, OnErrorReceived, PushToSinks);
        }

        /// <summary>停止所有设备的轮询。</summary>
        public void Stop()
        {
            _cts?.Cancel();
            foreach (var kvp in _pollers)
                kvp.Value.Stop();
            _cts?.Dispose();
            _cts = null;
        }

        // ── 内部回调 ──────────────────────────────

        private void OnSampleReceived(DataSample sample)
        {
            OnSample?.Invoke(this, new DataSampleEventArgs(sample));
        }

        private void OnErrorReceived(string deviceName, string address, string error)
        {
            OnError?.Invoke(this, new DataErrorEventArgs(deviceName, address, error));
        }

        private void PushToSinks(DataSample sample)
        {
            lock (_sinkLock)
            {
                foreach (var sink in _sinks)
                {
                    try { sink.Write(sample); } catch { /* sink 不得中断采集 */ }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            foreach (var kvp in _pollers)
                kvp.Value.Dispose();
            _pollers.Clear();
            lock (_sinkLock) { _sinks.Clear(); }
            GC.SuppressFinalize(this);
        }
    }

    // ── 采集点配置 ──────────────────────────────

    /// <summary>单个设备的轮询配置。</summary>
    public sealed class PollConfig
    {
        /// <summary>轮询间隔（毫秒），默认 1000ms。</summary>
        public int IntervalMs { get; set; } = 1000;

        /// <summary>单次轮询超时（毫秒），默认 5000ms。</summary>
        public int TimeoutMs { get; set; } = 5000;

        /// <summary>连续失败多少次后标记设备为离线，默认 3。</summary>
        public int FailureThreshold { get; set; } = 3;

        /// <summary>设备离线后重试间隔（毫秒），默认 10000ms。</summary>
        public int RetryIntervalMs { get; set; } = 10000;

        /// <summary>是否仅推送变化数据（减少数据量），默认 true。</summary>
        public bool OnlyOnChange { get; set; } = true;
    }

    /// <summary>一个采集数据样本。</summary>
    public sealed class DataSample
    {
        /// <summary>设备名称。</summary>
        public string DeviceName { get; set; } = "";

        /// <summary>地址。</summary>
        public string Address { get; set; } = "";

        /// <summary>数据类型。</summary>
        public string DataType { get; set; } = "Int16";

        /// <summary>用户自定义标签。</summary>
        public string? Tag { get; set; }

        /// <summary>读取值（字符串表示）。</summary>
        public string Value { get; set; } = "";

        /// <summary>数据质量: Good, Uncertain, Bad。</summary>
        public string Quality { get; set; } = "Good";

        /// <summary>采集时间戳。</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>数据样本事件参数。</summary>
    public sealed class DataSampleEventArgs : EventArgs
    {
        public DataSample Sample { get; }
        public DataSampleEventArgs(DataSample sample) => Sample = sample;
    }

    /// <summary>数据错误事件参数。</summary>
    public sealed class DataErrorEventArgs : EventArgs
    {
        public string DeviceName { get; }
        public string Address { get; }
        public string Error { get; }
        public DataErrorEventArgs(string deviceName, string address, string error)
        {
            DeviceName = deviceName;
            Address = address;
            Error = error;
        }
    }

    // ── 数据接收器接口 ───────────────────────────

    /// <summary>
    /// 数据接收器接口 — 将采集数据输出到任意目标。
    /// 内置实现: ConsoleDataSink, CsvDataSink, MemoryDataSink。
    /// 用户可实现此接口对接 MQTT、数据库、时序库等。
    /// </summary>
    public interface IDataSink : IDisposable
    {
        /// <summary>写入一个数据样本。</summary>
        void Write(DataSample sample);
    }

    // ── 内置接收器 ───────────────────────────────

    /// <summary>控制台数据接收器 — 用于调试。</summary>
    public sealed class ConsoleDataSink : IDataSink
    {
        public void Write(DataSample sample)
        {
            Console.WriteLine($"[{sample.Timestamp:HH:mm:ss.fff}] {sample.DeviceName}/{sample.Address} = {sample.Value} ({sample.Quality})");
        }
        public void Dispose() { }
    }

    /// <summary>内存数据接收器 — 最近 N 条记录的环形缓冲。</summary>
    public sealed class MemoryDataSink : IDataSink
    {
        private readonly DataSample[] _buffer;
        private int _index;
        private readonly object _lock = new object();

        public int Capacity { get; }
        public int Count { get { lock (_lock) { return _count; } } }
        private int _count;

        public MemoryDataSink(int capacity = 1000)
        {
            Capacity = capacity;
            _buffer = new DataSample[capacity];
        }

        public void Write(DataSample sample)
        {
            lock (_lock)
            {
                _buffer[_index] = sample;
                _index = (_index + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        /// <summary>获取所有缓存的样本（按时间顺序）。</summary>
        public DataSample[] GetAll()
        {
            lock (_lock)
            {
                var result = new DataSample[_count];
                int start = _count < Capacity ? 0 : _index;
                for (int i = 0; i < _count; i++)
                    result[i] = _buffer[(start + i) % Capacity];
                return result;
            }
        }

        public void Dispose() { }
    }

    /// <summary>
    /// CSV 文件数据接收器 — 将采集数据追加写入 CSV 文件。
    /// 线程安全，自动创建目录和文件头。
    /// </summary>
    public sealed class CsvDataSink : IDataSink
    {
        private readonly string _filePath;
        private readonly object _sync = new object();
        private bool _headerWritten;

        /// <param name="filePath">CSV 文件路径。目录不存在时自动创建。</param>
        public CsvDataSink(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            var dir = System.IO.Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
        }

        public void Write(DataSample sample)
        {
            lock (_sync)
            {
                bool writeHeader = !_headerWritten;
                using (var writer = new System.IO.StreamWriter(_filePath, append: true, System.Text.Encoding.UTF8))
                {
                    if (writeHeader)
                    {
                        writer.WriteLine("Timestamp,DeviceName,Address,DataType,Tag,Quality,Value");
                        _headerWritten = true;
                    }

                    string line = $"{sample.Timestamp:yyyy-MM-dd HH:mm:ss.fff}," +
                                  $"{EscapeCsv(sample.DeviceName)}," +
                                  $"{EscapeCsv(sample.Address)}," +
                                  $"{EscapeCsv(sample.DataType)}," +
                                  $"{EscapeCsv(sample.Tag ?? "")}," +
                                  $"{EscapeCsv(sample.Quality)}," +
                                  $"{EscapeCsv(sample.Value)}";
                    writer.WriteLine(line);
                }
            }
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        public void Dispose() { }
    }

    // ── 内部设备轮询器 ───────────────────────────

    internal sealed class DevicePoller : IDisposable
    {
        private readonly string _name;
        private readonly IReadWriteDevice _device;
        private readonly PollConfig _config;
        private readonly List<PollPoint> _points = new List<PollPoint>();
        private readonly object _pointsLock = new object();
        private Timer? _timer;
        private int _consecutiveFailures;
        private bool _offline;

        public DevicePoller(string name, IReadWriteDevice device, PollConfig config)
        {
            _name = name;
            _device = device;
            _config = config;
        }

        public void AddPoint(string address, string dataType, string? tag)
        {
            lock (_pointsLock)
            {
                _points.Add(new PollPoint { Address = address, DataType = dataType, Tag = tag });
            }
        }

        public void RemovePoint(string address)
        {
            lock (_pointsLock)
            {
                _points.RemoveAll(p => p.Address == address);
            }
        }

        public void Start(CancellationToken ct, Action<DataSample> onSample, Action<string, string, string> onError, Action<DataSample> onSink)
        {
            // Auto-connect if not connected
            if (!_device.IsConnected)
            {
                var connectResult = _device.Connect();
                if (!connectResult.IsSuccess)
                {
                    onError(_name, "*", $"Connect failed: {connectResult.Message}");
                    _offline = true;
                }
            }

            _timer = new Timer(_ =>
            {
                if (ct.IsCancellationRequested) return;
                PollOnce(onSample, onError, onSink);
            }, null, 0, _offline ? _config.RetryIntervalMs : _config.IntervalMs);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void PollOnce(Action<DataSample> onSample, Action<string, string, string> onError, Action<DataSample> onSink)
        {
            PollPoint[] snapshot;
            lock (_pointsLock) { snapshot = _points.ToArray(); }
            if (snapshot.Length == 0) return;

            var now = DateTime.Now;

            foreach (var point in snapshot)
            {
                var sample = new DataSample
                {
                    DeviceName = _name,
                    Address = point.Address,
                    DataType = point.DataType,
                    Tag = point.Tag,
                    Timestamp = now
                };

                try
                {
                    string value = ReadValue(point);
                    sample.Value = value;
                    sample.Quality = "Good";

                    if (_config.OnlyOnChange && value == point.LastValue)
                        continue; // 跳过未变化的数据

                    point.LastValue = value;
                    _consecutiveFailures = 0;
                    if (_offline) _offline = false;

                    onSample(sample);
                    onSink(sample);
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    sample.Value = point.LastValue ?? "";
                    sample.Quality = "Bad";

                    onError(_name, point.Address, ex.Message);

                    if (_consecutiveFailures >= _config.FailureThreshold)
                    {
                        _offline = true;
                    }
                }
            }
        }

        private string ReadValue(PollPoint point)
        {
            switch (point.DataType)
            {
                case "Bool":
                    return _device.ReadBool(point.Address).Content.ToString();
                case "Int16":
                    return _device.ReadInt16(point.Address).Content.ToString();
                case "UInt16":
                    return _device.ReadUInt16(point.Address).Content.ToString();
                case "Int32":
                    return _device.ReadInt32(point.Address).Content.ToString();
                case "UInt32":
                    return _device.ReadUInt32(point.Address).Content.ToString();
                case "Int64":
                    return _device.ReadInt64(point.Address).Content.ToString();
                case "UInt64":
                    return _device.ReadUInt64(point.Address).Content.ToString();
                case "Float":
                    return _device.ReadFloat(point.Address).Content.ToString();
                case "Double":
                    return _device.ReadDouble(point.Address).Content.ToString();
                default:
                    return _device.ReadInt16(point.Address).Content.ToString();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        public Dictionary<string, string> GetCurrentValues()
        {
            var result = new Dictionary<string, string>();
            PollPoint[] snapshot;
            lock (_pointsLock) { snapshot = _points.ToArray(); }
            foreach (var point in snapshot)
            {
                var key = $"{_name}/{point.Address}";
                result[key] = point.LastValue ?? "";
            }
            return result;
        }

        private class PollPoint
        {
            public string Address { get; set; } = "";
            public string DataType { get; set; } = "Int16";
            public string? Tag { get; set; }
            public string? LastValue { get; set; }
        }
    }
}
