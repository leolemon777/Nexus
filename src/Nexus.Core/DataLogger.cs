using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    /// <summary>
    /// Data logger — writes monitored data to files for historical analysis.
    /// Supports CSV and JSON Lines formats.
    /// For SQLite integration, use Nexus.DataLogger extension.
    /// </summary>
    public sealed class DataLogger : IDisposable
    {
        private StreamWriter? _writer;
        private readonly object _lock = new object();
        private bool _logging;
        private long _entryCount;
        private string _filePath = string.Empty;
        private DataLogFormat _format = DataLogFormat.Csv;

        public bool IsLogging => _logging;
        public long EntryCount => _entryCount;
        public string FilePath => _filePath;

        public event EventHandler<string>? OnLog;

        public void StartLogging(string filePath, DataLogFormat format = DataLogFormat.Csv)
        {
            lock (_lock)
            {
                if (_logging) return;
                _filePath = filePath;
                _format = format;
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(filePath, false, Encoding.UTF8);
                if (format == DataLogFormat.Csv)
                    _writer.WriteLine("Timestamp,Address,Alias,Value,Quality,DataType");
                _logging = true;
                _entryCount = 0;
                OnLog?.Invoke(this, "[Log] 数据记录已启动: " + filePath);
            }
        }

        public void Log(string address, string alias, double value, string quality = "Good", string dataType = "Float")
        {
            if (!_logging || _writer == null) return;
            lock (_lock)
            {
                var now = DateTime.Now;
                if (_format == DataLogFormat.Csv)
                {
                    _writer.WriteLine(now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "," +
                        EscapeCsv(address) + "," +
                        EscapeCsv(alias) + "," +
                        value.ToString("F6") + "," +
                        EscapeCsv(quality) + "," +
                        EscapeCsv(dataType));
                }
                else
                {
                    _writer.WriteLine("{\"t\":\"" + now.ToString("O") + "\",\"a\":\"" + EscapeJson(address) +
                        "\",\"n\":\"" + EscapeJson(alias) + "\",\"v\":" + value.ToString("F6") +
                        ",\"q\":\"" + EscapeJson(quality) + "\",\"dt\":\"" + EscapeJson(dataType) + "\"}");
                }
                _entryCount++;

                if (_entryCount % 10000 == 0)
                {
                    _writer.Flush();
                    OnLog?.Invoke(this, "[Log] 已记录 " + _entryCount + " 条数据");
                }
            }
        }

        public void StopLogging()
        {
            lock (_lock)
            {
                if (!_logging) return;
                _logging = false;
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
                OnLog?.Invoke(this, "[Log] 数据记录已停止: " + _entryCount + " 条");
            }
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

        public void Dispose() => StopLogging();
    }

    public enum DataLogFormat
    {
        Csv,
        JsonLines
    }

    /// <summary>
    /// Log rotation — automatically creates new log files when size exceeds limit.
    /// </summary>
    public sealed class RotatingDataLogger : IDisposable
    {
        private readonly string _basePath;
        private readonly long _maxFileSize;
        private readonly int _maxFiles;
        private readonly DataLogFormat _format;
        private DataLogger? _currentLogger;

        public bool IsLogging => _currentLogger?.IsLogging ?? false;
        public long EntryCount => _currentLogger?.EntryCount ?? 0;

        public event EventHandler<string>? OnLog;

        public RotatingDataLogger(string basePath, long maxFileSize = 50 * 1024 * 1024, int maxFiles = 10, DataLogFormat format = DataLogFormat.Csv)
        {
            _basePath = basePath;
            _maxFileSize = maxFileSize;
            _maxFiles = maxFiles;
            _format = format;
        }

        public void Start()
        {
            RotateIfNeeded();
            _currentLogger?.StartLogging(GetCurrentPath(), _format);
        }

        public void Log(string address, string alias, double value, string quality = "Good", string dataType = "Float")
        {
            RotateIfNeeded();
            _currentLogger?.Log(address, alias, value, quality, dataType);
        }

        public void Stop()
        {
            _currentLogger?.StopLogging();
        }

        private void RotateIfNeeded()
        {
            if (_currentLogger == null || (_currentLogger.IsLogging && new FileInfo(_currentLogger.FilePath).Length > _maxFileSize))
            {
                _currentLogger?.StopLogging();
                CleanOldFiles();
                _currentLogger = new DataLogger();
                _currentLogger.OnLog += (s, msg) => OnLog?.Invoke(s, msg);
            }
        }

        private string GetCurrentPath()
        {
            string ext = _format == DataLogFormat.Csv ? "csv" : "jsonl";
            return _basePath + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "." + ext;
        }

        private void CleanOldFiles()
        {
            var dir = Path.GetDirectoryName(_basePath);
            if (string.IsNullOrEmpty(dir)) return;
            var pattern = Path.GetFileName(_basePath) + "*";
            var files = Directory.GetFiles(dir, pattern);
            if (files.Length >= _maxFiles)
            {
                Array.Sort(files);
                for (int i = 0; i < files.Length - _maxFiles + 1; i++)
                    File.Delete(files[i]);
            }
        }

        public void Dispose()
        {
            _currentLogger?.Dispose();
        }
    }
}
