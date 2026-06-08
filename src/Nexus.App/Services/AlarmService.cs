using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nexus.App.Services
{
    /// <summary>
    /// 报警规则模型
    /// </summary>
    public sealed class AlarmRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string TagName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string ProtocolName { get; set; } = string.Empty;
        public AlarmCondition Condition { get; set; } = AlarmCondition.GreaterThan;
        public double Threshold { get; set; }
        public AlarmSeverity Severity { get; set; } = AlarmSeverity.Warning;
        public string Description { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public enum AlarmCondition
    {
        GreaterThan,    // > 阈值
        LessThan,       // < 阈值
        EqualTo,        // == 阈值
        NotEqualTo,     // != 阈值
        GreaterOrEqual, // >= 阈值
        LessOrEqual     // <= 阈值
    }

    public enum AlarmSeverity
    {
        Info,
        Warning,
        Critical
    }

    /// <summary>
    /// 报警记录模型
    /// </summary>
    public sealed class AlarmRecord
    {
        public string RuleId { get; set; } = string.Empty;
        public string TagName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public double Value { get; set; }
        public double Threshold { get; set; }
        public AlarmSeverity Severity { get; set; }
        public DateTime TriggeredAt { get; set; } = DateTime.Now;
        public DateTime? AcknowledgedAt { get; set; }

        public bool IsAcknowledged => AcknowledgedAt.HasValue;
    }

    /// <summary>
    /// 报警服务 — 管理报警规则、触发报警、记录历史。
    /// <para>对标 HSL AlarmManager。</para>
    /// </summary>
    public sealed class AlarmService : IDisposable
    {
        private readonly List<AlarmRule> _rules = new();
        private readonly ObservableCollection<AlarmRecord> _activeAlarms = new();
        private readonly List<AlarmRecord> _history = new();
        private readonly string _dataPath;

        public IReadOnlyList<AlarmRule> Rules => _rules.AsReadOnly();
        public ObservableCollection<AlarmRecord> ActiveAlarms => _activeAlarms;
        public IReadOnlyList<AlarmRecord> History => _history.AsReadOnly();

        public event EventHandler<AlarmRecord>? AlarmTriggered;
        public event EventHandler<AlarmRecord>? AlarmAcknowledged;

        public AlarmService()
        {
            _dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexus", "alarms.json");
            LoadRules();
        }

        // ── 规则管理 ─────────────────────────

        public void AddRule(AlarmRule rule)
        {
            _rules.Add(rule);
            SaveRules();
        }

        public void RemoveRule(string ruleId)
        {
            _rules.RemoveAll(r => r.Id == ruleId);
            SaveRules();
        }

        public void UpdateRule(AlarmRule rule)
        {
            int idx = _rules.FindIndex(r => r.Id == rule.Id);
            if (idx >= 0) { _rules[idx] = rule; SaveRules(); }
        }

        // ── 报警评估 ─────────────────────────

        /// <summary>
        /// 评估一个值是否触发报警规则。
        /// </summary>
        public void Evaluate(string tagOrAddress, double value)
        {
            foreach (var rule in _rules)
            {
                if (!rule.IsEnabled) continue;
                if (!rule.TagName.Equals(tagOrAddress, StringComparison.OrdinalIgnoreCase) &&
                    !rule.Address.Equals(tagOrAddress, StringComparison.OrdinalIgnoreCase)) continue;

                bool triggered = rule.Condition switch
                {
                    AlarmCondition.GreaterThan => value > rule.Threshold,
                    AlarmCondition.LessThan => value < rule.Threshold,
                    AlarmCondition.EqualTo => Math.Abs(value - rule.Threshold) < 0.0001,
                    AlarmCondition.NotEqualTo => Math.Abs(value - rule.Threshold) >= 0.0001,
                    AlarmCondition.GreaterOrEqual => value >= rule.Threshold,
                    AlarmCondition.LessOrEqual => value <= rule.Threshold,
                    _ => false
                };

                if (triggered)
                {
                    // 检查是否已有活跃报警
                    bool alreadyActive = _activeAlarms.Any(a =>
                        a.RuleId == rule.Id && !a.IsAcknowledged);

                    if (!alreadyActive)
                    {
                        var record = new AlarmRecord
                        {
                            RuleId = rule.Id,
                            TagName = rule.TagName,
                            Message = $"{rule.TagName}: {value} {ConditionText(rule.Condition)} {rule.Threshold}",
                            Value = value,
                            Threshold = rule.Threshold,
                            Severity = rule.Severity
                        };
                        _activeAlarms.Add(record);
                        _history.Add(record);
                        AlarmTriggered?.Invoke(this, record);
                    }
                }
                else
                {
                    // 条件不再满足，自动清除活跃报警
                    var existing = _activeAlarms.FirstOrDefault(a =>
                        a.RuleId == rule.Id && !a.IsAcknowledged);
                    if (existing != null)
                    {
                        existing.AcknowledgedAt = DateTime.Now;
                        AlarmAcknowledged?.Invoke(this, existing);
                    }
                }
            }
        }

        // ── 确认报警 ─────────────────────────

        public void Acknowledge(AlarmRecord record)
        {
            record.AcknowledgedAt = DateTime.Now;
            AlarmAcknowledged?.Invoke(this, record);
        }

        public void AcknowledgeAll()
        {
            foreach (var alarm in _activeAlarms.Where(a => !a.IsAcknowledged))
                Acknowledge(alarm);
        }

        // ── 持久化 ─────────────────────────

        private void SaveRules()
        {
            try
            {
                var dir = Path.GetDirectoryName(_dataPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_rules, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_dataPath, json);
            }
            catch { }
        }

        private void LoadRules()
        {
            try
            {
                if (!File.Exists(_dataPath)) return;
                var json = File.ReadAllText(_dataPath);
                var rules = JsonSerializer.Deserialize<List<AlarmRule>>(json);
                if (rules != null) _rules.AddRange(rules);
            }
            catch { }
        }

        private static string ConditionText(AlarmCondition c) => c switch
        {
            AlarmCondition.GreaterThan => ">",
            AlarmCondition.LessThan => "<",
            AlarmCondition.EqualTo => "==",
            AlarmCondition.NotEqualTo => "!=",
            AlarmCondition.GreaterOrEqual => ">=",
            AlarmCondition.LessOrEqual => "<=",
            _ => "?"
        };

        public void Dispose()
        {
            SaveRules();
            GC.SuppressFinalize(this);
        }
    }
}
