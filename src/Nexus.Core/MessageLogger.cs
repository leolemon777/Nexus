using System;

namespace Nexus
{
    /// <summary>
    /// 结构化消息日志条目，用于记录底层通讯的原始报文、耗时和关联 ID。
    /// </summary>
    public readonly struct MessageLogEntry
    {
        public DateTime Timestamp { get; }
        public string DeviceId { get; }
        public string CorrelationId { get; }
        public string Direction { get; } // "TX" or "RX"
        public byte[] Payload { get; }
        public string HexPayload { get; }
        public TimeSpan? Duration { get; }
        public string? ErrorMessage { get; }

        public MessageLogEntry(
            DateTime timestamp,
            string deviceId,
            string correlationId,
            string direction,
            byte[] payload,
            string hexPayload,
            TimeSpan? duration = null,
            string? errorMessage = null)
        {
            Timestamp = timestamp;
            DeviceId = deviceId;
            CorrelationId = correlationId;
            Direction = direction;
            Payload = payload;
            HexPayload = hexPayload;
            Duration = duration;
            ErrorMessage = errorMessage;
        }
    }

    /// <summary>
    /// 结构化消息日志接口，用于记录底层通讯的原始报文和性能指标。
    /// </summary>
    public interface IMessageLogger
    {
        void Log(MessageLogEntry entry);
    }

    /// <summary>
    /// 默认空消息日志实现。
    /// </summary>
    public sealed class NullMessageLogger : IMessageLogger
    {
        public static NullMessageLogger Instance { get; } = new NullMessageLogger();
        public void Log(MessageLogEntry entry) { }
    }
}