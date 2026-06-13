using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Modbus;
using Nexus.Mqtt;
using Nexus.Siemens;
using Nexus.Mitsubishi;
using Nexus.Omron;
using Nexus.AllenBradley;

namespace Nexus.Bridge
{
    /// <summary>
    /// 协议桥接引擎 — 从工业协议读取数据，桥接到 MQTT 等目标。
    /// <para>这是 HSL 不提供的差异化特性：统一的协议桥接能力。</para>
    /// <para>支持: Modbus TCP → MQTT, 任意 IReadWriteDevice → Console/Csv。</para>
    /// </summary>
    public class ProtocolBridge : IDisposable
    {
        private readonly BridgeConfig _config;
        private readonly IReadWriteDevice _source;
        private readonly IBridgeTarget _target;
        private CancellationTokenSource? _cts;
        private Task? _pollTask;
        private bool _disposed;

        /// <summary>已桥接的数据点数。</summary>
        public long BridgedCount { get; private set; }

        /// <summary>最后错误信息。</summary>
        public string? LastError { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        /// <summary>数据桥接事件。</summary>
        public event EventHandler<BridgeDataEventArgs>? OnDataBridged;

        /// <summary>桥接错误事件。</summary>
        public event EventHandler<BridgeErrorEventArgs>? OnError;

        /// <summary>
        /// 使用配置和自定义源设备创建桥接。
        /// </summary>
        public ProtocolBridge(BridgeConfig config, IReadWriteDevice source, IBridgeTarget target)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>
        /// 使用便捷配置创建 Modbus TCP → MQTT 桥接。
        /// </summary>
        public ProtocolBridge(BridgeConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _source = CreateSource(config);
            _target = CreateTarget(config);
        }

        /// <summary>启动桥接。</summary>
        public OperateResult Start()
        {
            if (IsRunning) return OperateResult.Success();
            if (_config.Points.Count == 0) return OperateResult.Failed("桥接点列表为空");

            // 连接源设备
            if (!_source.IsConnected)
            {
                var conn = _source.Connect();
                if (!conn.IsSuccess) return OperateResult.Failed($"源设备连接失败: {conn.Message}");
            }

            // 连接目标
            var targetConn = _target.Connect();
            if (!targetConn.IsSuccess) return OperateResult.Failed($"目标连接失败: {targetConn.Message}");

            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoop(_cts.Token));

            return OperateResult.Success();
        }

        /// <summary>停止桥接。</summary>
        public void Stop()
        {
            _cts?.Cancel();
            try { _pollTask?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            _pollTask = null;
            _cts?.Dispose();
            _cts = null;

            try { _target.Disconnect(); } catch { }
            try { _source.Disconnect(); } catch { }
        }

        private async Task PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var point in _config.Points)
                    {
                        if (ct.IsCancellationRequested) break;

                        var value = ReadPoint(point);
                        if (value == null) continue;

                        // 应用缩放
                        double numericValue = value.Value;
                        if (point.Scale != 1.0 || point.Offset != 0.0)
                            numericValue = numericValue * point.Scale + point.Offset;

                        var data = new BridgeData
                        {
                            Address = point.Address,
                            Tag = point.Tag,
                            DataType = point.DataType,
                            RawValue = value.Value,
                            ScaledValue = numericValue,
                            Timestamp = DateTime.Now
                        };

                        _target.Publish(data);
                        BridgedCount++;
                        OnDataBridged?.Invoke(this, new BridgeDataEventArgs(data));
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    OnError?.Invoke(this, new BridgeErrorEventArgs(ex.Message));
                }

                await Task.Delay(_config.PollIntervalMs, ct).ConfigureAwait(false);
            }
        }

        private BridgeValue? ReadPoint(BridgePoint point)
        {
            try
            {
                switch (point.DataType)
                {
                    case "Bool":
                        var b = _source.ReadBool(point.Address);
                        return b.IsSuccess ? new BridgeValue(b.Content ? 1.0 : 0.0) : null;
                    case "Int16":
                        var i16 = _source.ReadInt16(point.Address);
                        return i16.IsSuccess ? new BridgeValue(i16.Content) : null;
                    case "UInt16":
                        var u16 = _source.ReadUInt16(point.Address);
                        return u16.IsSuccess ? new BridgeValue(u16.Content) : null;
                    case "Int32":
                        var i32 = _source.ReadInt32(point.Address);
                        return i32.IsSuccess ? new BridgeValue(i32.Content) : null;
                    case "UInt32":
                        var u32 = _source.ReadUInt32(point.Address);
                        return u32.IsSuccess ? new BridgeValue(u32.Content) : null;
                    case "Float":
                        var f = _source.ReadFloat(point.Address);
                        return f.IsSuccess ? new BridgeValue(f.Content) : null;
                    case "Double":
                        var d = _source.ReadDouble(point.Address);
                        return d.IsSuccess ? new BridgeValue(d.Content) : null;
                    default:
                        var def = _source.ReadInt16(point.Address);
                        return def.IsSuccess ? new BridgeValue(def.Content) : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static IReadWriteDevice CreateSource(BridgeConfig config)
        {
            switch (config.SourceType)
            {
                case "ModbusTcp":
                    return new ModbusTcpClient(config.SourceIp, config.SourcePort ?? 502, config.SourceStation);
                case "SiemensS7":
                    return new SiemensS7Client(SiemensPLCS.S7_1200, config.SourceIp, config.SourcePort ?? 102);
                case "Mitsubishi":
                    return new Mc3EBinaryClient(MitsubishiModel.Qna_3E, config.SourceIp, config.SourcePort ?? 6000);
                case "OmronFins":
                    return new FinsTcpClient(config.SourceIp, config.SourcePort ?? 9600);
                case "AllenBradley":
                    return new AllenBradleyCipClient(config.SourceIp, config.SourcePort ?? 44818);
                default:
                    throw new ArgumentException($"不支持的源类型: {config.SourceType}");
            }
        }

        private static IBridgeTarget CreateTarget(BridgeConfig config)
        {
            switch (config.TargetType)
            {
                case "Mqtt":
                    return new MqttBridgeTarget(config.TargetHost, config.TargetPort,
                        config.MqttClientId, config.MqttTopicPrefix);
                case "Console":
                    return new ConsoleBridgeTarget();
                case "Csv":
                    return new CsvBridgeTarget(config.CsvFilePath, config.CsvAppend);
                case "Redis":
                    return new RedisBridgeTarget(config.RedisConnectionString, config.RedisKeyPrefix);
                case "InfluxDb":
                    return new InfluxDbBridgeTarget(config.InfluxDbUrl, config.InfluxDbDatabase);
                default:
                    throw new ArgumentException($"不支持的目标类型: {config.TargetType}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            (_source as IDisposable)?.Dispose();
            (_target as IDisposable)?.Dispose();
        }
    }

    // ── 桥接目标接口 ─────────────────────────────

    /// <summary>桥接目标接口。</summary>
    public interface IBridgeTarget : IDisposable
    {
        OperateResult Connect();
        void Disconnect();
        void Publish(BridgeData data);
    }

    // ── 桥接数据结构 ─────────────────────────────

    /// <summary>桥接数据。</summary>
    public class BridgeData
    {
        public string Address { get; set; } = "";
        public string Tag { get; set; } = "";
        public string DataType { get; set; } = "";
        public double RawValue { get; set; }
        public double ScaledValue { get; set; }
        public DateTime Timestamp { get; set; }

        /// <summary>转为 JSON 字符串。</summary>
        public string ToJson()
        {
            return $"{{\"address\":\"{Address}\",\"tag\":\"{Tag}\"," +
                   $"\"type\":\"{DataType}\",\"rawValue\":{RawValue}," +
                   $"\"scaledValue\":{ScaledValue}," +
                   $"\"timestamp\":\"{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ}\"}}";
        }
    }

    /// <summary>桥接值包装。</summary>
    internal struct BridgeValue
    {
        public double Value { get; }
        public BridgeValue(double value) => Value = value;
        public static implicit operator double(BridgeValue v) => v.Value;
    }

    // ── 事件参数 ────────────────────────────────

    /// <summary>桥接数据事件。</summary>
    public class BridgeDataEventArgs : EventArgs
    {
        public BridgeData Data { get; }
        public BridgeDataEventArgs(BridgeData data) => Data = data;
    }

    /// <summary>桥接错误事件。</summary>
    public class BridgeErrorEventArgs : EventArgs
    {
        public string Error { get; }
        public BridgeErrorEventArgs(string error) => Error = error;
    }

    // ── 内置桥接目标 ────────────────────────────

    /// <summary>MQTT 桥接目标。</summary>
    public sealed class MqttBridgeTarget : IBridgeTarget
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _clientId;
        private readonly string _topicPrefix;
        private MqttClient? _mqtt;

        public MqttBridgeTarget(string host, int port, string clientId, string topicPrefix)
        {
            _host = host;
            _port = port;
            _clientId = clientId;
            _topicPrefix = topicPrefix;
        }

        public OperateResult Connect()
        {
            try
            {
                _mqtt = new MqttClient();
                var task = _mqtt.ConnectAsync(_host, _port, _clientId);
                if (!task.Wait(5000))
                    return OperateResult.Failed("MQTT 连接超时");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"MQTT 连接失败: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            try { _mqtt?.Disconnect(); } catch { }
        }

        public void Publish(BridgeData data)
        {
            if (_mqtt == null) return;
            string topic = string.IsNullOrEmpty(data.Tag)
                ? $"{_topicPrefix}{data.Address}"
                : $"{_topicPrefix}{data.Tag}";

            var json = data.ToJson();
            try
            {
                var task = _mqtt.PublishAsync(topic, Encoding.UTF8.GetBytes(json));
                task.Wait(3000);
            }
            catch { /* 发布失败不应中断桥接 */ }
        }

        public void Dispose()
        {
            Disconnect();
            _mqtt?.Dispose();
        }
    }

    /// <summary>控制台桥接目标（调试用，支持颜色编码）。</summary>
    public sealed class ConsoleBridgeTarget : IBridgeTarget
    {
        private static readonly Dictionary<string, ConsoleColor> TypeColors = new Dictionary<string, ConsoleColor>(StringComparer.OrdinalIgnoreCase)
        {
            { "Bool", ConsoleColor.Green },
            { "Int16", ConsoleColor.Cyan },
            { "UInt16", ConsoleColor.Cyan },
            { "Int32", ConsoleColor.Yellow },
            { "UInt32", ConsoleColor.Yellow },
            { "Float", ConsoleColor.Magenta },
            { "Double", ConsoleColor.Magenta },
            { "String", ConsoleColor.White }
        };

        public OperateResult Connect() => OperateResult.Success();
        public void Disconnect() { }

        public void Publish(BridgeData data)
        {
            var prev = Console.ForegroundColor;
            if (TypeColors.TryGetValue(data.DataType, out var color))
                Console.ForegroundColor = color;

            Console.WriteLine($"[{data.Timestamp:HH:mm:ss.fff}] {data.Tag ?? data.Address} = {data.ScaledValue:F2} ({data.DataType})");

            Console.ForegroundColor = prev;
        }

        public void Dispose() { }
    }

    /// <summary>CSV 文件桥接目标。</summary>
    public sealed class CsvBridgeTarget : IBridgeTarget
    {
        private readonly string _filePath;
        private readonly bool _append;
        private StreamWriter? _writer;

        public CsvBridgeTarget(string filePath, bool append = true)
        {
            _filePath = filePath;
            _append = append;
        }

        public OperateResult Connect()
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var fileExists = File.Exists(_filePath);
                _writer = new StreamWriter(_filePath, _append, Encoding.UTF8);

                if (!_append || !fileExists)
                {
                    _writer.WriteLine("timestamp,tag,address,value,type");
                    _writer.Flush();
                }
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"CSV 文件打开失败: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            try { _writer?.Flush(); _writer?.Close(); } catch { }
        }

        public void Publish(BridgeData data)
        {
            if (_writer == null) return;
            _writer.WriteLine($"{data.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{Escape(data.Tag)},{Escape(data.Address)},{data.ScaledValue},{data.DataType}");
            _writer.Flush();
        }

        private static string Escape(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var val = s!;
            if (val.Contains(",") || val.Contains("\"") || val.Contains("\n"))
                return "\"" + val.Replace("\"", "\"\"") + "\"";
            return val;
        }

        public void Dispose() { Disconnect(); _writer?.Dispose(); }
    }

    /// <summary>Redis 桥接目标。</summary>
    public sealed class RedisBridgeTarget : IBridgeTarget
    {
        private readonly string _connectionString;
        private readonly string _keyPrefix;
        private object? _client;

        public RedisBridgeTarget(string connectionString, string keyPrefix = "nexus:")
        {
            _connectionString = connectionString;
            _keyPrefix = keyPrefix;
        }

        public OperateResult Connect()
        {
            try
            {
                var parts = _connectionString.Split(':');
                var host = parts.Length > 0 ? parts[0] : "127.0.0.1";
                var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 6379;

                var type = Type.GetType("Nexus.Redis.RedisClient, Nexus.Redis");
                if (type == null)
                    return OperateResult.Failed("Nexus.Redis 程序集不可用");

                _client = Activator.CreateInstance(type, host, port, 5000, 10);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Redis 连接失败: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            (_client as IDisposable)?.Dispose();
            _client = null;
        }

        public void Publish(BridgeData data)
        {
            if (_client == null) return;
            var key = string.IsNullOrEmpty(data.Tag)
                ? $"{_keyPrefix}{data.Address}"
                : $"{_keyPrefix}{data.Tag}";

            try
            {
                var setMethod = _client.GetType().GetMethod("Set");
                setMethod?.Invoke(_client, new object[] { key, data.ScaledValue.ToString("F4"), 0 });
            }
            catch { }
        }

        public void Dispose() { Disconnect(); }
    }

    /// <summary>InfluxDB 桥接目标（Line Protocol）。</summary>
    public sealed class InfluxDbBridgeTarget : IBridgeTarget
    {
        private readonly string _url;
        private readonly string _database;
        private HttpClient? _http;

        public InfluxDbBridgeTarget(string url, string database = "nexus")
        {
            _url = url.TrimEnd('/');
            _database = database;
        }

        public OperateResult Connect()
        {
            try
            {
                _http = new HttpClient();
                _http.Timeout = TimeSpan.FromSeconds(10);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"InfluxDB 初始化失败: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            _http?.Dispose();
            _http = null;
        }

        public void Publish(BridgeData data)
        {
            if (_http == null) return;
            var measurement = string.IsNullOrEmpty(data.Tag) ? data.Address : data.Tag;
            var line = $"{measurement} value={data.ScaledValue.ToString("F6")} {(data.Timestamp.ToUniversalTime().Ticks - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / 100}0";

            try
            {
                var url = $"{_url}/write?db={_database}";
                var content = new StringContent(line, Encoding.UTF8, "text/plain");
                _http.PostAsync(url, content).Wait(3000);
            }
            catch { }
        }

        public void Dispose() { Disconnect(); }
    }
}
