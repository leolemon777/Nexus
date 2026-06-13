#nullable disable warnings
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.OpcUa
{
    public sealed class OpcUaAlarmServer
    {
        private readonly ConcurrentDictionary<string, AlarmCondition> _conditions = new ConcurrentDictionary<string, AlarmCondition>();
        private readonly List<AlarmEvent> _eventHistory = new List<AlarmEvent>();
        private readonly object _lock = new object();
        public event EventHandler<AlarmEvent> OnAlarm;

        public void ReportAlarm(string conditionId, string sourceName, string message, AlarmSeverity severity)
        {
            var evt = new AlarmEvent
            {
                EventId = Guid.NewGuid().ToString(),
                ConditionId = conditionId,
                SourceName = sourceName,
                Message = message,
                Severity = severity,
                Time = DateTime.UtcNow,
                EventType = AlarmEventType.Condition
            };
            lock (_lock)
            {
                _eventHistory.Add(evt);
                if (_eventHistory.Count > 10000) _eventHistory.RemoveAt(0);
            }
            _conditions[conditionId] = new AlarmCondition
            {
                ConditionId = conditionId,
                SourceName = sourceName,
                Message = message,
                Severity = severity,
                ActiveState = true,
                LastTime = DateTime.UtcNow
            };
            OnAlarm?.Invoke(this, evt);
        }

        public void AcknowledgeAlarm(string conditionId, string userId)
        {
            if (_conditions.TryGetValue(conditionId, out var c))
            {
                c.AcknowledgedState = true;
                c.AckUserId = userId;
                c.AckTime = DateTime.UtcNow;
            }
        }

        public void ClearAlarm(string conditionId)
        {
            if (_conditions.TryGetValue(conditionId, out var c))
            {
                c.ActiveState = false;
                c.ClearTime = DateTime.UtcNow;
            }
        }

        public List<AlarmCondition> GetActiveAlarms()
        {
            return _conditions.Values.Where(c => c.ActiveState).ToList();
        }

        public List<AlarmEvent> GetEventHistory(int count = 100)
        {
            lock (_lock)
            {
                int skip = _eventHistory.Count > count ? _eventHistory.Count - count : 0;
                return _eventHistory.Skip(skip).ToList();
            }
        }
    }

    public class AlarmCondition
    {
        public string ConditionId { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string Message { get; set; } = "";
        public AlarmSeverity Severity { get; set; }
        public bool ActiveState { get; set; }
        public bool AcknowledgedState { get; set; }
        public string AckUserId { get; set; } = "";
        public DateTime? AckTime { get; set; }
        public DateTime? ClearTime { get; set; }
        public DateTime LastTime { get; set; }
    }

    public class AlarmEvent
    {
        public string EventId { get; set; } = "";
        public string ConditionId { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string Message { get; set; } = "";
        public AlarmSeverity Severity { get; set; }
        public AlarmEventType EventType { get; set; }
        public DateTime Time { get; set; }
    }

    public enum AlarmSeverity
    {
        Low = 100,
        Medium = 200,
        High = 300,
        Critical = 400
    }

    public enum AlarmEventType
    {
        Condition,
        Tracking,
        Simple
    }
}
