using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nexus
{
    /// <summary>日志级别。</summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// 通讯日志记录接口 — 可由上层注入实现（WPF 绑定到 UI 文本框等）。
    /// </summary>
    public interface ILogger
    {
        /// <summary>通用日志方法。</summary>
        void Log(LogLevel level, string message);

        /// <summary>信息日志。</summary>
        void Info(string message);

        /// <summary>警告日志。</summary>
        void Warn(string message);

        /// <summary>错误日志。</summary>
        void Error(string message);

        /// <summary>调试日志。</summary>
        void Debug(string message);
    }

    /// <summary>
    /// 默认空日志实现 — 不输出任何内容。
    /// </summary>
    public sealed class NullLogger : ILogger
    {
        public static NullLogger Instance { get; } = new NullLogger();
        public void Log(LogLevel level, string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Debug(string message) { }
    }

    /// <summary>
    /// 控制台日志实现 — 输出到 Console。
    /// </summary>
    public sealed class ConsoleLogger : ILogger
    {
        public void Log(LogLevel level, string message)
            => Console.WriteLine($"[{level.ToString().ToUpperInvariant(),5}] {message}");
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
    }

    /// <summary>
    /// 委托日志实现 — 将日志转发到自定义 Action。
    /// </summary>
    public sealed class DelegateLogger : ILogger
    {
        private readonly Action<LogLevel, string> _action;

        public DelegateLogger(Action<LogLevel, string> action)
            => _action = action ?? throw new ArgumentNullException(nameof(action));

        public void Log(LogLevel level, string message) => _action(level, message);
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
    }

    /// <summary>
    /// 环形缓冲日志实现 — 保留最近 N 条日志，用于 WPF 报文查看器。
    /// </summary>
    public sealed class BufferedLogger : ILogger
    {
        private readonly int _capacity;
        private readonly List<LogRecord> _entries;
        private readonly object _sync = new object();

        public BufferedLogger(int capacity = 500)
        {
            _capacity = capacity > 0 ? capacity : 500;
            _entries = new List<LogRecord>(_capacity);
        }

        public void Log(LogLevel level, string message)
        {
            lock (_sync)
            {
                if (_entries.Count >= _capacity)
                    _entries.RemoveAt(0);
                _entries.Add(new LogRecord(DateTime.Now, level, message));
            }
        }

        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);

        /// <summary>获取所有缓冲日志的快照。</summary>
        public List<LogRecord> GetSnapshot()
        {
            lock (_sync) { return new List<LogRecord>(_entries); }
        }

        /// <summary>清空缓冲区。</summary>
        public void Clear()
        {
            lock (_sync) { _entries.Clear(); }
        }
    }

    /// <summary>
    /// 滚动文件日志实现 — 按大小滚动创建新文件。
    /// </summary>
    public sealed class FileLogger : ILogger
    {
        private readonly string _basePath;
        private readonly long _maxFileSize;
        private readonly int _maxFiles;
        private readonly object _sync = new object();
        private string _currentPath;
        private long _currentSize;

        /// <param name="basePath">日志文件路径（如 "logs/nexus.log"）。</param>
        /// <param name="maxFileSize">单个文件最大字节数，默认 10 MB。</param>
        /// <param name="maxFiles">最大文件数，默认 5。</param>
        public FileLogger(string basePath, long maxFileSize = 10 * 1024 * 1024, int maxFiles = 5)
        {
            _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
            _maxFileSize = maxFileSize;
            _maxFiles = maxFiles;
            _currentPath = basePath;
            _currentSize = File.Exists(basePath) ? new FileInfo(basePath).Length : 0;
        }

        public void Log(LogLevel level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),5}] {message}{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetBytes(line);

            lock (_sync)
            {
                if (_currentSize + bytes.Length > _maxFileSize)
                    Roll();

                File.AppendAllText(_currentPath, line);
                _currentSize += bytes.Length;
            }
        }

        private void Roll()
        {
            // 删除最老的文件
            string oldest = $"{_basePath}.{_maxFiles}";
            if (File.Exists(oldest))
                File.Delete(oldest);

            // 依次重命名
            for (int i = _maxFiles - 1; i >= 1; i--)
            {
                string src = $"{_basePath}.{i}";
                string dst = $"{_basePath}.{i + 1}";
                if (File.Exists(src))
                    File.Move(src, dst);
            }

            // 当期文件 → .1
            if (File.Exists(_currentPath))
                File.Move(_currentPath, $"{_currentPath}.1");

            _currentSize = 0;
        }

        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
    }

    /// <summary>
    /// 组合日志实现 — 将日志分发到多个 ILogger。
    /// </summary>
    public sealed class MultiplexLogger : ILogger
    {
        private readonly ILogger[] _loggers;

        public MultiplexLogger(params ILogger[] loggers)
            => _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));

        public void Log(LogLevel level, string message)
        {
            foreach (var logger in _loggers)
            {
                try { logger.Log(level, message); }
                catch { /* 吞掉单个 logger 异常 */ }
            }
        }

        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
    }

    /// <summary>日志条目。</summary>
    public struct LogRecord
    {
        /// <summary>时间戳。</summary>
        public DateTime Timestamp { get; }

        /// <summary>日志级别。</summary>
        public LogLevel Level { get; }

        /// <summary>日志消息。</summary>
        public string Message { get; }

        public LogRecord(DateTime timestamp, LogLevel level, string message)
        {
            Timestamp = timestamp;
            Level = level;
            Message = message;
        }

        public override string ToString() => $"{Timestamp:HH:mm:ss.fff} [{Level,5}] {Message}";
    }
}
