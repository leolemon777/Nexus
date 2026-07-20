using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

// B8: PacketRecorder 仍支持旧 TcpDeviceBase,迁移完成后此 pragma 移除。
#pragma warning disable CS0618

namespace Nexus
{
    /// <summary>
    /// 报文记录器 — 捕获 TCP 设备的原始通讯报文，用于调试和分析。
    /// <para>附加到 TcpDeviceBase 的 OnMessageSent/OnMessageReceived 事件，</para>
    /// <para>记录所有 TX/RX 报文，支持 JSONL 导出和统计分析。</para>
    /// </summary>
    public class PacketRecorder : IDisposable
    {
        private readonly List<PacketEntry> _entries = new List<PacketEntry>();
        private readonly object _lock = new object();
        private volatile bool _recording;
        private bool _disposed;
        private DateTime _recordStartTime;

        /// <summary>是否正在录制。</summary>
        public bool IsRecording => _recording;

        /// <summary>已录制的报文数量。</summary>
        public int EntryCount
        {
            get { lock (_lock) { return _entries.Count; } }
        }

        /// <summary>附加到 TCP 设备的报文事件。</summary>
        public void Attach(TcpDeviceBase device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            device.OnMessageSent += OnMessageSent;
            device.OnMessageReceived += OnMessageReceived;
        }

        /// <summary>从设备分离事件。</summary>
        public void Detach(TcpDeviceBase device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            device.OnMessageSent -= OnMessageSent;
            device.OnMessageReceived -= OnMessageReceived;
        }

        /// <summary>开始录制。</summary>
        public void StartRecording()
        {
            _recording = true;
            _recordStartTime = DateTime.Now;
        }

        /// <summary>停止录制。</summary>
        public void StopRecording()
        {
            _recording = false;
        }

        /// <summary>清除所有录制数据。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }

        /// <summary>获取所有录制条目的副本。</summary>
        public List<PacketEntry> GetEntries()
        {
            lock (_lock)
            {
                return new List<PacketEntry>(_entries);
            }
        }

        /// <summary>导出为 JSON Lines 格式文件。</summary>
        public void ExportToJsonl(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            List<PacketEntry> snapshot;
            lock (_lock) { snapshot = new List<PacketEntry>(_entries); }

            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                foreach (var entry in snapshot)
                {
                    var sb = new StringBuilder();
                    sb.Append("{");
                    sb.Append("\"timestamp\":\"").Append(entry.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff")).Append("\",");
                    sb.Append("\"direction\":\"").Append(entry.Direction).Append("\",");
                    sb.Append("\"hex\":\"").Append(EscapeJson(entry.HexData)).Append("\"");
                    if (!string.IsNullOrEmpty(entry.Description))
                        sb.Append(",\"description\":\"").Append(EscapeJson(entry.Description)).Append("\"");
                    sb.Append("}");
                    writer.WriteLine(sb.ToString());
                }
            }
        }

        /// <summary>分析录制的报文数据。</summary>
        public PacketAnalysis Analyze()
        {
            List<PacketEntry> snapshot;
            lock (_lock) { snapshot = new List<PacketEntry>(_entries); }

            var analysis = new PacketAnalysis
            {
                TotalPackets = snapshot.Count,
                TxCount = 0,
                RxCount = 0,
                Errors = new List<string>()
            };

            if (snapshot.Count == 0)
            {
                analysis.Duration = TimeSpan.Zero;
                return analysis;
            }

            DateTime first = snapshot[0].Timestamp;
            DateTime last = snapshot[0].Timestamp;
            DateTime? lastTxTime = null;
            double totalResponseMs = 0;
            int responsePairs = 0;

            foreach (var entry in snapshot)
            {
                if (entry.Timestamp < first) first = entry.Timestamp;
                if (entry.Timestamp > last) last = entry.Timestamp;

                if (entry.Direction == "TX")
                {
                    analysis.TxCount++;
                    lastTxTime = entry.Timestamp;
                }
                else if (entry.Direction == "RX")
                {
                    analysis.RxCount++;
                    if (lastTxTime.HasValue)
                    {
                        totalResponseMs += (entry.Timestamp - lastTxTime.Value).TotalMilliseconds;
                        responsePairs++;
                        lastTxTime = null;
                    }
                }
            }

            analysis.Duration = last - first;
            analysis.AverageResponseTimeMs = responsePairs > 0 ? totalResponseMs / responsePairs : 0;

            return analysis;
        }

        private void OnMessageSent(object? sender, string hex)
        {
            if (!_recording) return;
            Record("TX", hex);
        }

        private void OnMessageReceived(object? sender, string hex)
        {
            if (!_recording) return;
            Record("RX", hex);
        }

        private void Record(string direction, string hexData)
        {
            var entry = new PacketEntry
            {
                Timestamp = DateTime.Now,
                Direction = direction,
                HexData = hexData ?? "",
                Description = ""
            };

            lock (_lock)
            {
                _entries.Add(entry);
            }
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopRecording();
            lock (_lock) { _entries.Clear(); }
        }
    }

    /// <summary>报文录制条目。</summary>
    public class PacketEntry
    {
        /// <summary>时间戳。</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>方向: TX 或 RX。</summary>
        public string Direction { get; set; } = "";

        /// <summary>十六进制报文数据。</summary>
        public string HexData { get; set; } = "";

        /// <summary>可读描述（如有解析器）。</summary>
        public string Description { get; set; } = "";
    }

    /// <summary>报文分析结果。</summary>
    public class PacketAnalysis
    {
        /// <summary>总报文数。</summary>
        public int TotalPackets { get; set; }

        /// <summary>录制持续时间。</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>发送报文数。</summary>
        public int TxCount { get; set; }

        /// <summary>接收报文数。</summary>
        public int RxCount { get; set; }

        /// <summary>平均响应时间（毫秒）。</summary>
        public double AverageResponseTimeMs { get; set; }

        /// <summary>错误列表。</summary>
        public List<string> Errors { get; set; } = new List<string>();
    }
}
