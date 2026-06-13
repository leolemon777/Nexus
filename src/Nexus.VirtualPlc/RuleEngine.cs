using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.VirtualPlc
{
    /// <summary>
    /// 规则引擎 — 监视虚拟 PLC 内存变化，当条件满足时触发动作。
    /// <para>支持: 值比较触发、变化触发、延迟执行、级联规则。</para>
    /// <para>使用方式: 注册规则后调用 <see cref="Start"/> 开始监视。</para>
    /// </summary>
    public class VirtualPlcRuleEngine : IDisposable
    {
        private readonly VirtualPlcMemory _memory;
        private readonly List<ActiveRule> _rules = new List<ActiveRule>();
        private readonly object _lock = new object();
        private Timer? _evalTimer;
        private int _disposed;
        private int _fireCount;

        /// <summary>规则触发事件。</summary>
        public event EventHandler<RuleFiredEventArgs>? OnRuleFired;

        /// <summary>已注册规则数。</summary>
        public int RuleCount
        {
            get { lock (_lock) { return _rules.Count; } }
        }

        /// <summary>已触发次数。</summary>
        public int FireCount => _fireCount;

        /// <summary>评估间隔（毫秒），默认 100ms。</summary>
        public int EvaluationIntervalMs { get; set; } = 100;

        public VirtualPlcRuleEngine(VirtualPlcMemory memory)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        }

        /// <summary>添加规则。</summary>
        public void AddRule(ScenarioRule rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            lock (_lock)
            {
                _rules.Add(new ActiveRule(rule));
            }
        }

        /// <summary>移除规则。</summary>
        public bool RemoveRule(string ruleName)
        {
            lock (_lock)
            {
                for (int i = 0; i < _rules.Count; i++)
                {
                    if (_rules[i].Rule.Name == ruleName)
                    {
                        _rules.RemoveAt(i);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>清除所有规则。</summary>
        public void ClearRules()
        {
            lock (_lock)
            {
                _rules.Clear();
            }
        }

        /// <summary>从场景加载规则。</summary>
        public void LoadFromScenario(ScenarioScript scenario)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            lock (_lock)
            {
                foreach (var rule in scenario.Rules)
                    _rules.Add(new ActiveRule(rule));
            }
        }

        /// <summary>启动规则引擎。</summary>
        public void Start()
        {
            Stop();
            _evalTimer = new Timer(Evaluate, null, EvaluationIntervalMs, EvaluationIntervalMs);
        }

        /// <summary>停止规则引擎。</summary>
        public void Stop()
        {
            _evalTimer?.Dispose();
            _evalTimer = null;
        }

        /// <summary>手动评估所有规则（用于测试）。</summary>
        public void EvaluateNow()
        {
            Evaluate(null);
        }

        private void Evaluate(object? state)
        {
            ActiveRule[] snapshot;
            lock (_lock)
            {
                snapshot = _rules.ToArray();
            }

            foreach (var active in snapshot)
            {
                try
                {
                    if (CheckCondition(active))
                    {
                        ExecuteAction(active);
                    }
                }
                catch
                {
                    // 规则执行失败不应中断引擎
                }
            }
        }

        private bool CheckCondition(ActiveRule active)
        {
            var rule = active.Rule;
            int currentValue = _memory.GetInt16(rule.WatchAddress);

            // TriggerValue == -1 表示任意变化触发
            if (rule.TriggerValue == -1)
            {
                bool changed = currentValue != active.LastValue;
                active.LastValue = currentValue;
                return changed;
            }

            return currentValue == rule.TriggerValue;
        }

        private void ExecuteAction(ActiveRule active)
        {
            var rule = active.Rule;

            if (rule.DelayMs > 0)
            {
                // 延迟执行 — 在新线程上等待后执行
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(rule.DelayMs);
                    rule.Execute(_memory);
                    Interlocked.Increment(ref _fireCount);
                    OnRuleFired?.Invoke(this, new RuleFiredEventArgs(rule.Name, rule.TargetAddress));
                });
            }
            else
            {
                rule.Execute(_memory);
                Interlocked.Increment(ref _fireCount);
                OnRuleFired?.Invoke(this, new RuleFiredEventArgs(rule.Name, rule.TargetAddress));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
            lock (_lock) { _rules.Clear(); }
        }

        // ── 内部活跃规则跟踪 ────────────────────────

        private class ActiveRule
        {
            public ScenarioRule Rule { get; }
            public int LastValue { get; set; }

            public ActiveRule(ScenarioRule rule)
            {
                Rule = rule;
                LastValue = int.MinValue; // 标记为未初始化
            }
        }
    }

    /// <summary>规则触发事件参数。</summary>
    public class RuleFiredEventArgs : EventArgs
    {
        /// <summary>规则名称。</summary>
        public string RuleName { get; }

        /// <summary>目标地址。</summary>
        public int TargetAddress { get; }

        /// <summary>触发时间。</summary>
        public DateTime Timestamp { get; }

        public RuleFiredEventArgs(string ruleName, int targetAddress)
        {
            RuleName = ruleName;
            TargetAddress = targetAddress;
            Timestamp = DateTime.Now;
        }
    }
}
