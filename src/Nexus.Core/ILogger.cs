using System;

namespace Nexus
{
    /// <summary>
    /// 通讯日志记录接口 — 可由上层注入实现（WPF 绑定到 UI 文本框等）。
    /// </summary>
    public interface ILogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Debug(string message);
    }

    /// <summary>
    /// 默认空日志实现 — 不输出任何内容。
    /// </summary>
    public sealed class NullLogger : ILogger
    {
        public static NullLogger Instance { get; } = new NullLogger();
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
        public void Info(string message) => Console.WriteLine($"[INFO ] {message}");
        public void Warn(string message) => Console.WriteLine($"[WARN ] {message}");
        public void Error(string message) => Console.WriteLine($"[ERROR] {message}");
        public void Debug(string message) => Console.WriteLine($"[DEBUG] {message}");
    }
}
