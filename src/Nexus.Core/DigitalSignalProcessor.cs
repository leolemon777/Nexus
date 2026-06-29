using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Nexus
{
    /// <summary>开关量边沿类型。</summary>
    public enum EdgeType
    {
        /// <summary>上升沿 (OFF→ON)。</summary>
        Rising,
        /// <summary>下降沿 (ON→OFF)。</summary>
        Falling,
        /// <summary>双边沿 (任何变化)。</summary>
        Both
    }

    /// <summary>开关量报警状态。</summary>
    public enum DigitalAlarmState
    {
        /// <summary>正常。</summary>
        Normal,
        /// <summary>报警（未确认）。</summary>
        Alarming,
        /// <summary>报警已确认。</summary>
        Acknowledged,
        /// <summary>已恢复（未确认）。</summary>
        Returned
    }

    /// <summary>开关量输入配置。</summary>
    public sealed class DigitalInputConfig
    {
        /// <summary>标签名。</summary>
        public string Name { get; set; } = "";

        /// <summary>是否取反（常闭触点）。</summary>
        public bool Invert { get; set; }

        /// <summary>去抖时间（毫秒）。在此时间内的抖动不触发状态变化。</summary>
        public int DebounceMs { get; set; } = 50;

        /// <summary>是否启用脉冲计数。</summary>
        public bool EnablePulseCount { get; set; }

        /// <summary>是否启用ON时间累计。</summary>
        public bool EnableOnTimeTracking { get; set; }

        /// <summary>是否启用报警。</summary>
        public bool EnableAlarm { get; set; }

        /// <summary>报警触发条件（true=ON时报警，false=OFF时报警）。</summary>
        public bool AlarmOnTrue { get; set; } = true;

        /// <summary>报警消息。</summary>
        public string AlarmMessage { get; set; } = "";

        /// <summary>是否启用边沿检测。</summary>
        public bool EnableEdgeDetection { get; set; }

        /// <summary>检测的边沿类型。</summary>
        public EdgeType EdgeType { get; set; } = EdgeType.Both;

        /// <summary>描述。</summary>
        public string Description { get; set; } = "";
    }

    /// <summary>开关量输入状态。</summary>
    public sealed class DigitalInputState
    {
        /// <summary>当前值（取反后）。</summary>
        public bool Value { get; set; }

        /// <summary>原始值（取反前）。</summary>
        public bool RawValue { get; set; }

        /// <summary>上次值。</summary>
        public bool LastValue { get; set; }

        /// <summary>最后变化时间。</summary>
        public DateTime LastChangeTime { get; set; } = DateTime.MinValue;

        /// <summary>上次变化的持续时间（秒）。ON→OFF 时记录 ON 持续时间。</summary>
        public double LastDuration { get; set; }

        /// <summary>脉冲计数（上升沿次数）。</summary>
        public long PulseCount { get; set; }

        /// <summary>总 ON 时间（秒）。</summary>
        public double TotalOnTime { get; set; }

        /// <summary>总 OFF 时间（秒）。</summary>
        public double TotalOffTime { get; set; }

        /// <summary>当前状态持续时间（秒）。</summary>
        public double CurrentDuration
        {
            get
            {
                if (LastChangeTime == DateTime.MinValue) return 0;
                return (DateTime.Now - LastChangeTime).TotalSeconds;
            }
        }

        /// <summary>报警状态。</summary>
        public DigitalAlarmState AlarmState { get; set; } = DigitalAlarmState.Normal;

        /// <summary>报警消息。</summary>
        public string AlarmMessage { get; set; } = "";

        /// <summary>是否在报警。</summary>
        public bool IsAlarming => AlarmState == DigitalAlarmState.Alarming || AlarmState == DigitalAlarmState.Acknowledged;

        /// <summary>变化次数。</summary>
        public long ChangeCount { get; set; }

        /// <summary>最后更新时间。</summary>
        public DateTime LastUpdateTime { get; set; } = DateTime.MinValue;

        /// <summary>去抖中的待定值。</summary>
        public bool PendingValue { get; set; }

        /// <summary>去抖开始时间。</summary>
        public DateTime DebounceStartTime { get; set; } = DateTime.MinValue;
    }

    /// <summary>开关量变化事件参数。</summary>
    public sealed class DigitalChangeEventArgs : EventArgs
    {
        public string TagName { get; set; } = "";
        public bool OldValue { get; set; }
        public bool NewValue { get; set; }
        public EdgeType Edge { get; set; }
        public DateTime Timestamp { get; set; }
        public double Duration { get; set; }
        public long PulseCount { get; set; }
    }

    /// <summary>开关量报警事件参数。</summary>
    public sealed class DigitalAlarmEventArgs : EventArgs
    {
        public string TagName { get; set; } = "";
        public DigitalAlarmState State { get; set; }
        public bool CurrentValue { get; set; }
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// 开关量处理器 — 工业级数字信号处理引擎。
    /// <para>功能: 取反、去抖、边沿检测、脉冲计数、ON时间累计、报警状态机、批量读取。</para>
    /// </summary>
    public sealed class DigitalSignalProcessor : IDisposable
    {
        private readonly ConcurrentDictionary<string, DigitalInputConfig> _configs = new();
        private readonly ConcurrentDictionary<string, DigitalInputState> _states = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastOnTimeUpdate = new();
        private bool _disposed;

        /// <summary>状态变化事件。</summary>
        public event EventHandler<DigitalChangeEventArgs>? OnStateChanged;

        /// <summary>边沿事件。</summary>
        public event EventHandler<DigitalChangeEventArgs>? OnEdge;

        /// <summary>报警事件。</summary>
        public event EventHandler<DigitalAlarmEventArgs>? OnAlarm;

        /// <summary>报警恢复事件。</summary>
        public event EventHandler<DigitalAlarmEventArgs>? OnAlarmReturn;

        // ═══════════════════════════════════════════
        //  配置管理
        // ═══════════════════════════════════════════

        /// <summary>添加开关量输入配置。</summary>
        public void AddInput(DigitalInputConfig config)
        {
            _configs[config.Name] = config;
            _states[config.Name] = new DigitalInputState { LastUpdateTime = DateTime.MinValue };
        }

        /// <summary>移除开关量输入。</summary>
        public void RemoveInput(string name)
        {
            _configs.TryRemove(name, out _);
            _states.TryRemove(name, out _);
            _lastOnTimeUpdate.TryRemove(name, out _);
        }

        /// <summary>获取配置。</summary>
        public DigitalInputConfig? GetConfig(string name) =>
            _configs.TryGetValue(name, out var config) ? config : null;

        /// <summary>获取所有配置。</summary>
        public List<DigitalInputConfig> GetAllConfigs() => _configs.Values.ToList();

        /// <summary>获取状态。</summary>
        public DigitalInputState? GetState(string name) =>
            _states.TryGetValue(name, out var state) ? state : null;

        /// <summary>获取所有状态。</summary>
        public List<(string Name, DigitalInputState State)> GetAllStates() =>
            _states.Select(kv => (kv.Key, kv.Value)).ToList();

        /// <summary>获取当前报警列表。</summary>
        public List<(string Name, DigitalAlarmState State)> GetActiveAlarms() =>
            _states.Where(kv => kv.Value.IsAlarming)
                   .Select(kv => (kv.Key, kv.Value.AlarmState)).ToList();

        // ═══════════════════════════════════════════
        //  数据更新
        // ═══════════════════════════════════════════

        /// <summary>
        /// 更新一个开关量输入。自动执行完整的处理管线：
        /// 取反 → 去抖 → 边沿检测 → 脉冲计数 → ON时间 → 报警 → 事件。
        /// </summary>
        /// <param name="name">标签名。</param>
        /// <param name="rawValue">原始值。</param>
        /// <param name="timestamp">时间戳（默认当前时间）。</param>
        public void Update(string name, bool rawValue, DateTime? timestamp = null)
        {
            if (!_configs.TryGetValue(name, out var config)) return;
            if (!_states.TryGetValue(name, out var state)) return;

            var now = timestamp ?? DateTime.Now;

            // Step 1: 保存原始值
            state.RawValue = rawValue;

            // Step 2: 取反
            bool value = config.Invert ? !rawValue : rawValue;

            // Step 3: 去抖
            if (config.DebounceMs > 0 && state.LastUpdateTime != DateTime.MinValue)
            {
                if (value != state.Value)
                {
                    // 值发生了变化，检查是否在去抖窗口内
                    if (state.DebounceStartTime == DateTime.MinValue)
                    {
                        // 开始去抖
                        state.PendingValue = value;
                        state.DebounceStartTime = now;
                        return; // 等待去抖完成
                    }
                    else if ((now - state.DebounceStartTime).TotalMilliseconds < config.DebounceMs)
                    {
                        // 还在去抖窗口内
                        state.PendingValue = value;
                        return;
                    }
                    else
                    {
                        // 去抖完成，确认变化
                        state.DebounceStartTime = DateTime.MinValue;
                    }
                }
                else
                {
                    // 值变回去了，取消去抖
                    state.DebounceStartTime = DateTime.MinValue;
                }
            }

            // Step 4: 检测变化
            if (value == state.Value)
            {
                // 没有变化，更新时间
                UpdateOnTime(config, state, now);
                state.LastUpdateTime = now;
                return;
            }

            // Step 5: 记录变化
            bool oldValue = state.Value;
            state.LastValue = oldValue;
            state.Value = value;
            state.ChangeCount++;

            // Step 6: 计算持续时间
            if (state.LastChangeTime != DateTime.MinValue)
            {
                state.LastDuration = (now - state.LastChangeTime).TotalSeconds;
                // 累计 ON/OFF 时间
                if (oldValue)
                    state.TotalOnTime += state.LastDuration;
                else
                    state.TotalOffTime += state.LastDuration;
            }
            state.LastChangeTime = now;

            // Step 7: 脉冲计数（上升沿）
            if (config.EnablePulseCount && value && !oldValue)
            {
                state.PulseCount++;
            }

            // Step 8: 边沿检测
            EdgeType? edge = null;
            if (value && !oldValue) edge = EdgeType.Rising;
            else if (!value && oldValue) edge = EdgeType.Falling;

            if (edge.HasValue)
            {
                // 触发状态变化事件
                OnStateChanged?.Invoke(this, new DigitalChangeEventArgs
                {
                    TagName = name,
                    OldValue = oldValue,
                    NewValue = value,
                    Edge = edge.Value,
                    Timestamp = now,
                    Duration = state.LastDuration,
                    PulseCount = state.PulseCount
                });

                // 触发边沿事件（如果配置了边沿检测）
                if (config.EnableEdgeDetection)
                {
                    if (config.EdgeType == EdgeType.Both ||
                        (config.EdgeType == EdgeType.Rising && edge == EdgeType.Rising) ||
                        (config.EdgeType == EdgeType.Falling && edge == EdgeType.Falling))
                    {
                        OnEdge?.Invoke(this, new DigitalChangeEventArgs
                        {
                            TagName = name,
                            OldValue = oldValue,
                            NewValue = value,
                            Edge = edge.Value,
                            Timestamp = now,
                            Duration = state.LastDuration,
                            PulseCount = state.PulseCount
                        });
                    }
                }
            }

            // Step 9: 报警状态机
            if (config.EnableAlarm)
            {
                UpdateAlarmState(config, state, value, now);
            }

            // Step 10: 更新时间
            UpdateOnTime(config, state, now);
            state.LastUpdateTime = now;
        }

        /// <summary>批量更新多个开关量。</summary>
        public void UpdateBatch(Dictionary<string, bool> values, DateTime? timestamp = null)
        {
            foreach (var kv in values)
                Update(kv.Key, kv.Value, timestamp);
        }

        /// <summary>确认报警。</summary>
        public bool AcknowledgeAlarm(string name)
        {
            if (!_states.TryGetValue(name, out var state)) return false;
            if (state.AlarmState == DigitalAlarmState.Alarming)
            {
                state.AlarmState = DigitalAlarmState.Acknowledged;
                return true;
            }
            if (state.AlarmState == DigitalAlarmState.Returned)
            {
                state.AlarmState = DigitalAlarmState.Normal;
                return true;
            }
            return false;
        }

        /// <summary>确认所有报警。</summary>
        public int AcknowledgeAllAlarms()
        {
            int count = 0;
            foreach (var kv in _states)
            {
                if (AcknowledgeAlarm(kv.Key)) count++;
            }
            return count;
        }

        /// <summary>重置脉冲计数。</summary>
        public void ResetPulseCount(string name)
        {
            if (_states.TryGetValue(name, out var state))
                state.PulseCount = 0;
        }

        /// <summary>重置所有计数器。</summary>
        public void ResetAllCounters(string name)
        {
            if (_states.TryGetValue(name, out var state))
            {
                state.PulseCount = 0;
                state.TotalOnTime = 0;
                state.TotalOffTime = 0;
                state.ChangeCount = 0;
            }
        }

        // ═══════════════════════════════════════════
        //  处理管线
        // ═══════════════════════════════════════════

        /// <summary>更新 ON/OFF 时间累计。</summary>
        private void UpdateOnTime(DigitalInputConfig config, DigitalInputState state, DateTime now)
        {
            if (!config.EnableOnTimeTracking) return;
            if (state.LastChangeTime == DateTime.MinValue) return;

            var lastUpdate = _lastOnTimeUpdate.GetOrAdd(config.Name, state.LastChangeTime);
            double elapsed = (now - lastUpdate).TotalSeconds;
            if (elapsed <= 0) return;

            if (state.Value)
                state.TotalOnTime += elapsed;
            else
                state.TotalOffTime += elapsed;

            _lastOnTimeUpdate[config.Name] = now;
        }

        /// <summary>更新报警状态机。</summary>
        private void UpdateAlarmState(DigitalInputConfig config, DigitalInputState state, bool value, DateTime now)
        {
            bool shouldAlarm = config.AlarmOnTrue ? value : !value;
            var oldState = state.AlarmState;

            switch (state.AlarmState)
            {
                case DigitalAlarmState.Normal:
                    if (shouldAlarm)
                    {
                        state.AlarmState = DigitalAlarmState.Alarming;
                        state.AlarmMessage = config.AlarmMessage ?? $"{config.Name}: 报警";
                        OnAlarm?.Invoke(this, new DigitalAlarmEventArgs
                        {
                            TagName = config.Name,
                            State = DigitalAlarmState.Alarming,
                            CurrentValue = value,
                            Message = state.AlarmMessage,
                            Timestamp = now
                        });
                    }
                    break;

                case DigitalAlarmState.Alarming:
                    if (!shouldAlarm)
                    {
                        state.AlarmState = DigitalAlarmState.Returned;
                        OnAlarmReturn?.Invoke(this, new DigitalAlarmEventArgs
                        {
                            TagName = config.Name,
                            State = DigitalAlarmState.Returned,
                            CurrentValue = value,
                            Message = $"{config.Name}: 报警恢复",
                            Timestamp = now
                        });
                    }
                    break;

                case DigitalAlarmState.Acknowledged:
                    if (!shouldAlarm)
                    {
                        state.AlarmState = DigitalAlarmState.Normal;
                        OnAlarmReturn?.Invoke(this, new DigitalAlarmEventArgs
                        {
                            TagName = config.Name,
                            State = DigitalAlarmState.Returned,
                            CurrentValue = value,
                            Message = $"{config.Name}: 报警恢复",
                            Timestamp = now
                        });
                    }
                    break;

                case DigitalAlarmState.Returned:
                    if (shouldAlarm)
                    {
                        state.AlarmState = DigitalAlarmState.Alarming;
                        state.AlarmMessage = config.AlarmMessage ?? $"{config.Name}: 报警";
                        OnAlarm?.Invoke(this, new DigitalAlarmEventArgs
                        {
                            TagName = config.Name,
                            State = DigitalAlarmState.Alarming,
                            CurrentValue = value,
                            Message = state.AlarmMessage,
                            Timestamp = now
                        });
                    }
                    break;
            }
        }

        // ═══════════════════════════════════════════
        //  诊断
        // ═══════════════════════════════════════════

        /// <summary>获取诊断摘要。</summary>
        public string GetDiagnosticSummary()
        {
            int total = _configs.Count;
            int on = _states.Count(s => s.Value.Value);
            int off = total - on;
            int alarming = _states.Count(s => s.Value.IsAlarming);
            long totalPulses = _states.Sum(s => s.Value.PulseCount);
            long totalChanges = _states.Sum(s => s.Value.ChangeCount);

            return $"总计: {total}, ON: {on}, OFF: {off}, 报警: {alarming}, 脉冲: {totalPulses}, 变化: {totalChanges}";
        }

        /// <summary>获取详细状态报告。</summary>
        public string GetDetailedReport()
        {
            var lines = new List<string>();
            lines.Add("=== 开关量状态报告 ===");
            lines.Add($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            lines.Add("");

            foreach (var kv in _configs.OrderBy(k => k.Key))
            {
                var config = kv.Value;
                var state = _states.TryGetValue(kv.Key, out var s) ? s : null;
                if (state == null) continue;

                lines.Add($"[{kv.Key}]");
                lines.Add($"  值: {(state.Value ? "ON" : "OFF")} (原始: {(state.RawValue ? "ON" : "OFF")}{(config.Invert ? ", 取反" : "")})");
                lines.Add($"  变化次数: {state.ChangeCount}");
                lines.Add($"  脉冲计数: {state.PulseCount}");
                lines.Add($"  当前持续: {state.CurrentDuration:F1}秒");
                lines.Add($"  上次变化持续: {state.LastDuration:F1}秒");
                if (config.EnableOnTimeTracking)
                {
                    lines.Add($"  ON时间: {FormatTimeSpan(state.TotalOnTime)}");
                    lines.Add($"  OFF时间: {FormatTimeSpan(state.TotalOffTime)}");
                }
                if (config.EnableAlarm)
                {
                    lines.Add($"  报警状态: {state.AlarmState}");
                    if (!string.IsNullOrEmpty(state.AlarmMessage))
                        lines.Add($"  报警消息: {state.AlarmMessage}");
                }
                lines.Add($"  最后更新: {(state.LastUpdateTime == DateTime.MinValue ? "从未" : state.LastUpdateTime.ToString("HH:mm:ss.fff"))}");
                lines.Add("");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string FormatTimeSpan(double seconds)
        {
            if (seconds < 60) return $"{seconds:F1}秒";
            if (seconds < 3600) return $"{seconds / 60:F1}分钟";
            if (seconds < 86400) return $"{seconds / 3600:F1}小时";
            return $"{seconds / 86400:F1}天";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _configs.Clear();
            _states.Clear();
            _lastOnTimeUpdate.Clear();
        }
    }
}
