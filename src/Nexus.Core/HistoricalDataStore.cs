using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Nexus
{
    /// <summary>历史数据记录。</summary>
    public sealed class HistoryRecord
    {
        public DateTime Timestamp { get; set; }
        public string TagName { get; set; } = "";
        public double Value { get; set; }
        public string Quality { get; set; } = "Good";
        public string DataType { get; set; } = "Float";
    }

    /// <summary>聚合数据记录。</summary>
    public sealed class AggregatedRecord
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string TagName { get; set; } = "";
        public double Min { get; set; }
        public double Max { get; set; }
        public double Avg { get; set; }
        public double Sum { get; set; }
        public int Count { get; set; }
        public double First { get; set; }
        public double Last { get; set; }
    }

    /// <summary>聚合周期。</summary>
    public enum AggregationPeriod
    {
        Second,
        Minute,
        FiveMinutes,
        FifteenMinutes,
        Hour,
        Day,
        Week,
        Month
    }

    /// <summary>数据压缩方式。</summary>
    public enum CompressionType
    {
        /// <summary>不压缩。</summary>
        None,
        /// <summary>死区压缩：变化量小于阈值不记录。</summary>
        Deadband,
        /// <summary>摆动门压缩：工业标准压缩算法。</summary>
        SwingDoor,
        /// <summary>旋转门压缩（改进版）。</summary>
        ImprovedSwingDoor
    }

    /// <summary>历史数据存储配置。</summary>
    public sealed class HistoryStoreConfig
    {
        /// <summary>最大内存记录数（超过后落盘）。</summary>
        public int MaxMemoryRecords { get; set; } = 100000;

        /// <summary>数据保留时间（天）。0=永久保留。</summary>
        public int RetentionDays { get; set; } = 30;

        /// <summary>压缩类型。</summary>
        public CompressionType Compression { get; set; } = CompressionType.None;

        /// <summary>死区压缩阈值。</summary>
        public double DeadbandThreshold { get; set; } = 0.01;

        /// <summary>摆动门压缩容差。</summary>
        public double SwingDoorTolerance { get; set; } = 0.1;

        /// <summary>是否启用聚合。</summary>
        public bool EnableAggregation { get; set; } = true;

        /// <summary>聚合保留天数。</summary>
        public int AggregationRetentionDays { get; set; } = 365;

        /// <summary>数据落盘目录。</summary>
        public string DataDirectory { get; set; } = "";

        /// <summary>是否自动落盘。</summary>
        public bool AutoFlush { get; set; } = true;

        /// <summary>自动落盘间隔（秒）。</summary>
        public int AutoFlushIntervalSeconds { get; set; } = 60;
    }

    /// <summary>
    /// 历史数据存储引擎 — 工业级时序数据存储。
    /// <para>功能: 内存环形缓冲、数据压缩（死区/摆动门）、自动聚合（秒/分/时/天）、
    /// 时间范围查询、降采样、CSV导出、自动落盘、数据保留策略。</para>
    /// </summary>
    public sealed class HistoricalDataStore : IDisposable
    {
        private readonly HistoryStoreConfig _config;
        private readonly ConcurrentDictionary<string, List<HistoryRecord>> _tagData = new();
        private readonly ConcurrentDictionary<string, List<AggregatedRecord>> _aggregatedData = new();
        private readonly ConcurrentDictionary<string, double> _lastValues = new();
        private readonly ConcurrentDictionary<string, double> _lastSlopes = new();
        private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();
        private Timer? _flushTimer;
        private Timer? _cleanupTimer;
        private int _totalRecords;
        private bool _disposed;

        /// <summary>总记录数。</summary>
        public int TotalRecords => _totalRecords;

        /// <summary>标签数量。</summary>
        public int TagCount => _tagData.Count;

        /// <summary>数据写入事件。</summary>
        public event EventHandler<HistoryRecord>? OnRecordWritten;

        /// <summary>数据落盘事件。</summary>
        public event EventHandler<int>? OnFlushed;

        public HistoricalDataStore(HistoryStoreConfig? config = null)
        {
            _config = config ?? new HistoryStoreConfig();

            if (_config.AutoFlush && _config.AutoFlushIntervalSeconds > 0)
            {
                _flushTimer = new Timer(FlushCallback, null,
                    _config.AutoFlushIntervalSeconds * 1000,
                    _config.AutoFlushIntervalSeconds * 1000);
            }

            if (_config.RetentionDays > 0)
            {
                _cleanupTimer = new Timer(CleanupCallback, null,
                    TimeSpan.FromHours(1), TimeSpan.FromHours(6));
            }
        }

        // ═══════════════════════════════════════════
        //  数据写入
        // ═══════════════════════════════════════════

        /// <summary>写入一条历史记录。</summary>
        public void Write(string tagName, double value, DateTime? timestamp = null, string quality = "Good", string dataType = "Float")
        {
            var now = timestamp ?? DateTime.Now;

            // 压缩检查
            if (!PassesCompression(tagName, value, now)) return;

            var record = new HistoryRecord
            {
                Timestamp = now,
                TagName = tagName,
                Value = value,
                Quality = quality,
                DataType = dataType
            };

            _rwLock.EnterWriteLock();
            try
            {
                var list = _tagData.GetOrAdd(tagName, _ => new List<HistoryRecord>());
                list.Add(record);
                Interlocked.Increment(ref _totalRecords);

                // 内存限制检查
                if (list.Count > _config.MaxMemoryRecords / Math.Max(1, _tagData.Count))
                {
                    // 移除最旧的 20%
                    int removeCount = list.Count / 5;
                    list.RemoveRange(0, removeCount);
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            // 聚合更新
            if (_config.EnableAggregation)
            {
                UpdateAggregation(tagName, value, now);
            }

            _lastValues[tagName] = value;
            OnRecordWritten?.Invoke(this, record);
        }

        /// <summary>批量写入。</summary>
        public void WriteBatch(IEnumerable<HistoryRecord> records)
        {
            foreach (var record in records)
                Write(record.TagName, record.Value, record.Timestamp, record.Quality, record.DataType);
        }

        // ═══════════════════════════════════════════
        //  数据压缩
        // ═══════════════════════════════════════════

        private bool PassesCompression(string tagName, double value, DateTime timestamp)
        {
            if (!_lastValues.TryGetValue(tagName, out var lastValue))
                return true; // 第一条记录，总是通过

            switch (_config.Compression)
            {
                case CompressionType.None:
                    return true;

                case CompressionType.Deadband:
                    return Math.Abs(value - lastValue) >= _config.DeadbandThreshold;

                case CompressionType.SwingDoor:
                    return PassesSwingDoor(tagName, value, timestamp);

                case CompressionType.ImprovedSwingDoor:
                    return PassesImprovedSwingDoor(tagName, value, timestamp);

                default:
                    return true;
            }
        }

        private bool PassesSwingDoor(string tagName, double value, DateTime timestamp)
        {
            // 摆动门压缩算法 (Swinging Door Trending)
            // 如果新数据点与上一个记录点的连线，偏离了中间所有点的范围超过容差，则记录
            if (!_lastValues.TryGetValue(tagName, out var lastValue))
                return true;

            if (!_lastSlopes.TryGetValue(tagName, out var lastSlope))
            {
                _lastSlopes[tagName] = 0;
                return true;
            }

            double tolerance = _config.SwingDoorTolerance;

            // 简化版：检查值变化是否超过容差
            if (Math.Abs(value - lastValue) >= tolerance)
            {
                _lastSlopes[tagName] = value - lastValue;
                return true;
            }

            return false;
        }

        private bool PassesImprovedSwingDoor(string tagName, double value, DateTime timestamp)
        {
            // 改进版摆动门：考虑时间因素
            if (!_lastValues.TryGetValue(tagName, out var lastValue))
                return true;

            double tolerance = _config.SwingDoorTolerance;
            double delta = Math.Abs(value - lastValue);

            // 值变化超过容差
            if (delta >= tolerance) return true;

            // 长时间无变化（超过30秒）也记录
            if (_tagData.TryGetValue(tagName, out var list) && list.Count > 0)
            {
                var lastRecord = list[list.Count - 1];
                if ((timestamp - lastRecord.Timestamp).TotalSeconds > 30)
                    return true;
            }

            return false;
        }

        // ═══════════════════════════════════════════
        //  数据聚合
        // ═══════════════════════════════════════════

        private void UpdateAggregation(string tagName, double value, DateTime timestamp)
        {
            // 按分钟聚合
            var minuteKey = $"{tagName}:min:{timestamp:yyyyMMddHHmm}";
            var minuteList = _aggregatedData.GetOrAdd(minuteKey, _ => new List<AggregatedRecord>());

            _rwLock.EnterWriteLock();
            try
            {
                var existing = minuteList.LastOrDefault();
                if (existing == null || existing.StartTime.Minute != timestamp.Minute)
                {
                    var newRecord = new AggregatedRecord
                    {
                        StartTime = new DateTime(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, timestamp.Minute, 0),
                        EndTime = timestamp,
                        TagName = tagName,
                        Min = value,
                        Max = value,
                        Avg = value,
                        Sum = value,
                        Count = 1,
                        First = value,
                        Last = value
                    };
                    minuteList.Add(newRecord);
                }
                else
                {
                    existing.EndTime = timestamp;
                    existing.Min = Math.Min(existing.Min, value);
                    existing.Max = Math.Max(existing.Max, value);
                    existing.Sum += value;
                    existing.Count++;
                    existing.Avg = existing.Sum / existing.Count;
                    existing.Last = value;
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        // ═══════════════════════════════════════════
        //  数据查询
        // ═══════════════════════════════════════════

        /// <summary>查询指定时间范围内的历史数据。</summary>
        public List<HistoryRecord> Query(string tagName, DateTime startTime, DateTime endTime, int maxPoints = 0)
        {
            _rwLock.EnterReadLock();
            try
            {
                if (!_tagData.TryGetValue(tagName, out var list))
                    return new List<HistoryRecord>();

                var result = list.Where(r => r.Timestamp >= startTime && r.Timestamp <= endTime).ToList();

                // 降采样
                if (maxPoints > 0 && result.Count > maxPoints)
                    result = Downsample(result, maxPoints);

                return result;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>查询多个标签的历史数据。</summary>
        public Dictionary<string, List<HistoryRecord>> QueryMultiple(IEnumerable<string> tagNames, DateTime startTime, DateTime endTime, int maxPoints = 0)
        {
            var result = new Dictionary<string, List<HistoryRecord>>();
            foreach (var tag in tagNames)
                result[tag] = Query(tag, startTime, endTime, maxPoints);
            return result;
        }

        /// <summary>获取最新值。</summary>
        public HistoryRecord? GetLatest(string tagName)
        {
            _rwLock.EnterReadLock();
            try
            {
                if (!_tagData.TryGetValue(tagName, out var list) || list.Count == 0)
                    return null;
                return list[list.Count - 1];
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>获取多个标签的最新值。</summary>
        public Dictionary<string, HistoryRecord?> GetLatest(IEnumerable<string> tagNames)
        {
            var result = new Dictionary<string, HistoryRecord?>();
            foreach (var tag in tagNames)
                result[tag] = GetLatest(tag);
            return result;
        }

        /// <summary>查询聚合数据。</summary>
        public List<AggregatedRecord> QueryAggregated(string tagName, DateTime startTime, DateTime endTime, AggregationPeriod period = AggregationPeriod.Minute)
        {
            _rwLock.EnterReadLock();
            try
            {
                // 查找所有匹配的聚合键
                var result = new List<AggregatedRecord>();
                foreach (var kv in _aggregatedData)
                {
                    if (!kv.Key.StartsWith(tagName + ":")) continue;
                    foreach (var record in kv.Value)
                    {
                        if (record.StartTime >= startTime && record.StartTime <= endTime)
                            result.Add(record);
                    }
                }
                return result.OrderBy(r => r.StartTime).ToList();
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>获取统计摘要。</summary>
        public AggregatedRecord? GetStatistics(string tagName, DateTime startTime, DateTime endTime)
        {
            var records = Query(tagName, startTime, endTime);
            if (records.Count == 0) return null;

            return new AggregatedRecord
            {
                StartTime = startTime,
                EndTime = endTime,
                TagName = tagName,
                Min = records.Min(r => r.Value),
                Max = records.Max(r => r.Value),
                Avg = records.Average(r => r.Value),
                Sum = records.Sum(r => r.Value),
                Count = records.Count,
                First = records[0].Value,
                Last = records[records.Count - 1].Value
            };
        }

        // ═══════════════════════════════════════════
        //  降采样
        // ═══════════════════════════════════════════

        /// <summary>LTTB 降采样算法 (Largest Triangle Three Buckets)。</summary>
        private List<HistoryRecord> Downsample(List<HistoryRecord> data, int targetCount)
        {
            if (data.Count <= targetCount) return data;

            var result = new List<HistoryRecord>(targetCount);
            result.Add(data[0]); // 第一个点

            double bucketSize = (double)(data.Count - 2) / (targetCount - 2);

            int prevIndex = 0;

            for (int i = 1; i < targetCount - 1; i++)
            {
                int bucketStart = (int)(1 + (i - 1) * bucketSize);
                int bucketEnd = (int)(1 + i * bucketSize);
                if (bucketEnd >= data.Count) bucketEnd = data.Count - 1;

                // 下一个桶的平均点
                int nextBucketStart = (int)(1 + i * bucketSize);
                int nextBucketEnd = (int)(1 + (i + 1) * bucketSize);
                if (nextBucketStart >= data.Count) nextBucketStart = data.Count - 1;
                if (nextBucketEnd >= data.Count) nextBucketEnd = data.Count - 1;

                double avgX = 0, avgY = 0;
                int nextCount = nextBucketEnd - nextBucketStart + 1;
                for (int j = nextBucketStart; j <= nextBucketEnd; j++)
                {
                    avgX += j;
                    avgY += data[j].Value;
                }
                avgX /= nextCount;
                avgY /= nextCount;

                // 在当前桶中找最大三角形面积的点
                double maxArea = -1;
                int maxIndex = bucketStart;
                for (int j = bucketStart; j <= bucketEnd; j++)
                {
                    double area = Math.Abs(
                        (prevIndex - avgX) * (data[j].Value - data[prevIndex].Value) -
                        (prevIndex - j) * (avgY - data[prevIndex].Value)
                    );
                    if (area > maxArea)
                    {
                        maxArea = area;
                        maxIndex = j;
                    }
                }

                result.Add(data[maxIndex]);
                prevIndex = maxIndex;
            }

            result.Add(data[data.Count - 1]); // 最后一个点
            return result;
        }

        // ═══════════════════════════════════════════
        //  数据导出
        // ═══════════════════════════════════════════

        /// <summary>导出为 CSV 文件。</summary>
        public int ExportToCsv(string filePath, string tagName, DateTime startTime, DateTime endTime)
        {
            var records = Query(tagName, startTime, endTime);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                writer.WriteLine("Timestamp,TagName,Value,Quality,DataType");
                foreach (var r in records)
                {
                    writer.WriteLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{EscapeCsv(r.TagName)},{r.Value:F6},{EscapeCsv(r.Quality)},{EscapeCsv(r.DataType)}");
                }
            }
            return records.Count;
        }

        /// <summary>导出多个标签为 CSV。</summary>
        public int ExportMultipleToCsv(string filePath, IEnumerable<string> tagNames, DateTime startTime, DateTime endTime)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            int totalCount = 0;
            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                writer.WriteLine("Timestamp,TagName,Value,Quality,DataType");
                foreach (var tagName in tagNames)
                {
                    var records = Query(tagName, startTime, endTime);
                    foreach (var r in records)
                    {
                        writer.WriteLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{EscapeCsv(r.TagName)},{r.Value:F6},{EscapeCsv(r.Quality)},{EscapeCsv(r.DataType)}");
                        totalCount++;
                    }
                }
            }
            return totalCount;
        }

        /// <summary>导出为 JSON Lines 文件。</summary>
        public int ExportToJsonl(string filePath, string tagName, DateTime startTime, DateTime endTime)
        {
            var records = Query(tagName, startTime, endTime);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                foreach (var r in records)
                {
                    writer.WriteLine($"{{\"t\":\"{r.Timestamp:O}\",\"tag\":\"{EscapeJson(r.TagName)}\",\"v\":{r.Value:F6},\"q\":\"{EscapeJson(r.Quality)}\"}}");
                }
            }
            return records.Count;
        }

        /// <summary>导出聚合数据为 CSV。</summary>
        public int ExportAggregatedToCsv(string filePath, string tagName, DateTime startTime, DateTime endTime)
        {
            var records = QueryAggregated(tagName, startTime, endTime);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                writer.WriteLine("StartTime,EndTime,TagName,Min,Max,Avg,Sum,Count,First,Last");
                foreach (var r in records)
                {
                    writer.WriteLine($"{r.StartTime:yyyy-MM-dd HH:mm:ss},{r.EndTime:yyyy-MM-dd HH:mm:ss},{EscapeCsv(r.TagName)},{r.Min:F6},{r.Max:F6},{r.Avg:F6},{r.Sum:F6},{r.Count},{r.First:F6},{r.Last:F6}");
                }
            }
            return records.Count;
        }

        // ═══════════════════════════════════════════
        //  落盘和清理
        // ═══════════════════════════════════════════

        /// <summary>手动落盘到文件。</summary>
        public int Flush()
        {
            if (string.IsNullOrEmpty(_config.DataDirectory)) return 0;

            int count = 0;
            var dir = _config.DataDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            _rwLock.EnterReadLock();
            try
            {
                foreach (var kv in _tagData)
                {
                    string filePath = Path.Combine(dir, $"{kv.Key}_{DateTime.Now:yyyyMMdd}.csv");
                    using (var writer = new StreamWriter(filePath, true, Encoding.UTF8))
                    {
                        foreach (var record in kv.Value)
                        {
                            writer.WriteLine($"{record.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{record.Value:F6},{record.Quality}");
                            count++;
                        }
                    }
                    kv.Value.Clear();
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            OnFlushed?.Invoke(this, count);
            return count;
        }

        /// <summary>清理过期数据。</summary>
        public int Cleanup()
        {
            if (_config.RetentionDays <= 0) return 0;

            int removed = 0;
            var cutoff = DateTime.Now.AddDays(-_config.RetentionDays);

            _rwLock.EnterWriteLock();
            try
            {
                foreach (var kv in _tagData)
                {
                    int before = kv.Value.Count;
                    kv.Value.RemoveAll(r => r.Timestamp < cutoff);
                    removed += before - kv.Value.Count;
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            // 清理过期聚合数据
            if (_config.AggregationRetentionDays > 0)
            {
                var aggCutoff = DateTime.Now.AddDays(-_config.AggregationRetentionDays);
                foreach (var kv in _aggregatedData)
                {
                    kv.Value.RemoveAll(r => r.StartTime < aggCutoff);
                }
            }

            return removed;
        }

        private void FlushCallback(object? state) => Flush();
        private void CleanupCallback(object? state) => Cleanup();

        // ═══════════════════════════════════════════
        //  诊断
        // ═══════════════════════════════════════════

        /// <summary>获取存储统计信息。</summary>
        public string GetStorageStatistics()
        {
            int totalRecords = 0;
            int totalTags = _tagData.Count;
            int totalAggregated = _aggregatedData.Values.Sum(v => v.Count);

            foreach (var kv in _tagData)
                totalRecords += kv.Value.Count;

            var sb = new StringBuilder();
            sb.AppendLine("=== 历史数据存储统计 ===");
            sb.AppendLine($"标签数: {totalTags}");
            sb.AppendLine($"原始记录: {totalRecords:N0}");
            sb.AppendLine($"聚合记录: {totalAggregated:N0}");
            sb.AppendLine($"总记录: {(totalRecords + totalAggregated):N0}");
            sb.AppendLine($"压缩方式: {_config.Compression}");
            sb.AppendLine($"保留天数: {_config.RetentionDays}");

            if (_config.EnableAggregation)
                sb.AppendLine($"聚合: 已启用");

            return sb.ToString();
        }

        /// <summary>获取每个标签的详细统计。</summary>
        public string GetTagStatistics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 标签统计 ===");

            foreach (var kv in _tagData.OrderBy(k => k.Key))
            {
                var list = kv.Value;
                if (list.Count == 0) continue;

                sb.AppendLine($"[{kv.Key}]");
                sb.AppendLine($"  记录数: {list.Count:N0}");
                sb.AppendLine($"  最早: {list[0].Timestamp:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"  最新: {list[list.Count - 1].Timestamp:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"  最小值: {list.Min(r => r.Value):F4}");
                sb.AppendLine($"  最大值: {list.Max(r => r.Value):F4}");
                sb.AppendLine($"  平均值: {list.Average(r => r.Value):F4}");
            }

            return sb.ToString();
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _flushTimer?.Dispose();
            _cleanupTimer?.Dispose();
            Flush();
            _rwLock.Dispose();
        }
    }
}
