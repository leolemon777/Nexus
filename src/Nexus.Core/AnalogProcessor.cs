using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Nexus
{
    /// <summary>
    /// 信号质量状态。
    /// </summary>
    public enum SignalQuality
    {
        /// <summary>正常。</summary>
        Good,
        /// <summary>不确定（接近量程边界）。</summary>
        Uncertain,
        /// <summary>坏（传感器故障、超量程、数据过期）。</summary>
        Bad
    }

    /// <summary>
    /// 模拟量报警类型。
    /// </summary>
    public enum AlarmLevel
    {
        None,
        LowLow,
        Low,
        High,
        HighHigh
    }

    /// <summary>
    /// 模拟量输入配置。
    /// </summary>
    public sealed class AnalogInputConfig
    {
        /// <summary>标签名。</summary>
        public string Name { get; set; } = "";

        /// <summary>原始值量程下限。</summary>
        public double RawMin { get; set; } = 0;

        /// <summary>原始值量程上限。</summary>
        public double RawMax { get; set; } = 65535;

        /// <summary>工程值量程下限。</summary>
        public double EngMin { get; set; } = 0;

        /// <summary>工程值量程上限。</summary>
        public double EngMax { get; set; } = 100;

        /// <summary>工程单位。</summary>
        public string Unit { get; set; } = "";

        /// <summary>小数位数。</summary>
        public int DecimalPlaces { get; set; } = 2;

        /// <summary>死区（在此范围内的变化不触发更新）。</summary>
        public double Deadband { get; set; } = 0;

        /// <summary>滤波系数 (0-1)。0=不滤波，1=完全使用旧值。推荐 0.1-0.3。</summary>
        public double FilterFactor { get; set; } = 0;

        /// <summary>高高报警阈值。</summary>
        public double? HighHighLimit { get; set; }

        /// <summary>高报警阈值。</summary>
        public double? HighLimit { get; set; }

        /// <summary>低报警阈值。</summary>
        public double? LowLimit { get; set; }

        /// <summary>低低报警阈值。</summary>
        public double? LowLowLimit { get; set; }

        /// <summary>报警死区（防止报警在阈值附近抖动）。</summary>
        public double AlarmDeadband { get; set; } = 0;

        /// <summary>数据过期时间（毫秒）。超过此时间未更新则标记为 Bad。</summary>
        public int StaleTimeoutMs { get; set; } = 10000;

        /// <summary>是否启用累计（如流量累计）。</summary>
        public bool EnableTotalization { get; set; }

        /// <summary>累计单位（如 m³, L, kWh）。</summary>
        public string TotalUnit { get; set; } = "";

        /// <summary>是否启用峰值追踪。</summary>
        public bool EnablePeakTracking { get; set; }

        /// <summary>峰值追踪窗口（秒）。0=全局。</summary>
        public int PeakWindowSeconds { get; set; } = 0;

        /// <summary>多点校准表（原始值→工程值映射）。为空时使用线性插值。</summary>
        public List<(double Raw, double Eng)>? CalibrationTable { get; set; }
    }

    /// <summary>
    /// 模拟量输入状态。
    /// </summary>
    public sealed class AnalogInputState
    {
        /// <summary>原始值（直接从设备读取）。</summary>
        public double RawValue { get; set; }

        /// <summary>滤波后的原始值。</summary>
        public double FilteredRaw { get; set; }

        /// <summary>缩放后的工程值。</summary>
        public double EngValue { get; set; }

        /// <summary>格式化后的显示值。</summary>
        public string DisplayValue { get; set; } = "";

        /// <summary>信号质量。</summary>
        public SignalQuality Quality { get; set; } = SignalQuality.Good;

        /// <summary>质量原因。</summary>
        public string QualityReason { get; set; } = "";

        /// <summary>当前报警级别。</summary>
        public AlarmLevel Alarm { get; set; } = AlarmLevel.None;

        /// <summary>报警消息。</summary>
        public string AlarmMessage { get; set; } = "";

        /// <summary>累计值。</summary>
        public double Total { get; set; }

        /// <summary>峰值（最大值）。</summary>
        public double Peak { get; set; } = double.MinValue;

        /// <summary>谷值（最小值）。</summary>
        public double Valley { get; set; } = double.MaxValue;

        /// <summary>最后更新时间。</summary>
        public DateTime LastUpdateTime { get; set; } = DateTime.MinValue;

        /// <summary>变化率（每秒）。</summary>
        public double RateOfChange { get; set; }

        /// <summary>是否过期。</summary>
        public bool IsStale => Quality == SignalQuality.Bad && QualityReason == "数据过期";

        /// <summary>是否在报警状态。</summary>
        public bool IsAlarming => Alarm != AlarmLevel.None;
    }

    /// <summary>
    /// 模拟量报警事件参数。
    /// </summary>
    public sealed class AnalogAlarmEventArgs : EventArgs
    {
        public string TagName { get; set; } = "";
        public AlarmLevel Level { get; set; }
        public double Value { get; set; }
        public double Threshold { get; set; }
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool IsAcknowledged { get; set; }
    }

    /// <summary>
    /// 模拟量处理器 — 工业级模拟量输入处理引擎。
    /// <para>功能: 线性/多点缩放、EWMA滤波、死区、报警限、信号质量、过期检测、峰值/谷值追踪、累计、变化率。</para>
    /// </summary>
    public sealed class AnalogProcessor : IDisposable
    {
        private readonly ConcurrentDictionary<string, AnalogInputConfig> _configs = new();
        private readonly ConcurrentDictionary<string, AnalogInputState> _states = new();
        private readonly ConcurrentDictionary<string, AlarmLevel> _activeAlarms = new();
        private readonly ConcurrentDictionary<string, DateTime> _peakResetTime = new();
        private DateTime _lastTotalUpdate = DateTime.UtcNow;
        private bool _disposed;

        /// <summary>报警事件。</summary>
        public event EventHandler<AnalogAlarmEventArgs>? OnAlarm;

        /// <summary>数据变化事件。</summary>
        public event EventHandler<(string Name, double EngValue, SignalQuality Quality)>? OnValueChanged;

        /// <summary>信号质量变化事件。</summary>
        public event EventHandler<(string Name, SignalQuality OldQuality, SignalQuality NewQuality)>? OnQualityChanged;

        // ═══════════════════════════════════════════
        //  配置管理
        // ═══════════════════════════════════════════

        /// <summary>添加模拟量输入配置。</summary>
        public void AddInput(AnalogInputConfig config)
        {
            _configs[config.Name] = config;
            _states[config.Name] = new AnalogInputState { LastUpdateTime = DateTime.MinValue };
            if (config.EnablePeakTracking)
                _peakResetTime[config.Name] = DateTime.UtcNow;
        }

        /// <summary>移除模拟量输入。</summary>
        public void RemoveInput(string name)
        {
            _configs.TryRemove(name, out _);
            _states.TryRemove(name, out _);
            _activeAlarms.TryRemove(name, out _);
            _peakResetTime.TryRemove(name, out _);
        }

        /// <summary>获取配置。</summary>
        public AnalogInputConfig? GetConfig(string name) =>
            _configs.TryGetValue(name, out var config) ? config : null;

        /// <summary>获取所有配置。</summary>
        public List<AnalogInputConfig> GetAllConfigs() => _configs.Values.ToList();

        /// <summary>获取状态。</summary>
        public AnalogInputState? GetState(string name) =>
            _states.TryGetValue(name, out var state) ? state : null;

        /// <summary>获取所有状态。</summary>
        public List<(string Name, AnalogInputState State)> GetAllStates() =>
            _states.Select(kv => (kv.Key, kv.Value)).ToList();

        /// <summary>获取当前报警列表。</summary>
        public List<(string Name, AlarmLevel Level)> GetActiveAlarms() =>
            _activeAlarms.Where(kv => kv.Value != AlarmLevel.None)
                         .Select(kv => (kv.Key, kv.Value)).ToList();

        // ═══════════════════════════════════════════
        //  数据更新
        // ═══════════════════════════════════════════

        /// <summary>
        /// 更新一个模拟量输入的原始值。自动执行完整的处理管线：
        /// 滤波 → 缩放 → 死区 → 报警 → 质量 → 峰值/累计 → 事件。
        /// </summary>
        /// <param name="name">标签名。</param>
        /// <param name="rawValue">原始值。</param>
        /// <param name="timestamp">时间戳（默认当前时间）。</param>
        public void Update(string name, double rawValue, DateTime? timestamp = null)
        {
            if (!_configs.TryGetValue(name, out var config)) return;
            if (!_states.TryGetValue(name, out var state)) return;

            var now = timestamp ?? DateTime.Now;
            double oldEng = state.EngValue;
            var oldQuality = state.Quality;

            // Step 1: 滤波
            state.RawValue = rawValue;
            state.FilteredRaw = ApplyFilter(config, state, rawValue);

            // Step 2: 缩放 (线性或多点)
            double scaled = ApplyScaling(config, state.FilteredRaw);

            // Step 3: 格式化
            state.EngValue = Math.Round(scaled, config.DecimalPlaces);
            state.DisplayValue = state.EngValue.ToString($"F{config.DecimalPlaces}") + (string.IsNullOrEmpty(config.Unit) ? "" : $" {config.Unit}");

            // Step 4: 死区检查
            if (config.Deadband > 0 && Math.Abs(state.EngValue - oldEng) < config.Deadband)
            {
                // 变化量在死区内，不触发事件（但仍然更新内部状态）
                state.LastUpdateTime = now;
                UpdateTotalization(config, state, now);
                return;
            }

            // Step 5: 信号质量
            var newQuality = EvaluateQuality(config, state, now);
            if (newQuality != oldQuality)
            {
                state.Quality = newQuality;
                OnQualityChanged?.Invoke(this, (name, oldQuality, newQuality));
            }

            // Step 6: 变化率
            if (state.LastUpdateTime != DateTime.MinValue)
            {
                double timeDelta = (now - state.LastUpdateTime).TotalSeconds;
                if (timeDelta > 0.001)
                    state.RateOfChange = (state.EngValue - oldEng) / timeDelta;
            }
            state.LastUpdateTime = now;

            // Step 7: 峰值/谷值追踪
            if (config.EnablePeakTracking)
            {
                UpdatePeakValley(config, state, now);
            }

            // Step 8: 累计
            if (config.EnableTotalization)
            {
                UpdateTotalization(config, state, now);
            }

            // Step 9: 报警检查
            var oldAlarm = state.Alarm;
            var newAlarm = EvaluateAlarm(config, state);
            if (newAlarm != oldAlarm)
            {
                state.Alarm = newAlarm;
                _activeAlarms[name] = newAlarm;
                RaiseAlarm(name, config, state, newAlarm, now);
            }

            // Step 10: 触发变化事件
            if (Math.Abs(state.EngValue - oldEng) > 1e-10)
            {
                OnValueChanged?.Invoke(this, (name, state.EngValue, state.Quality));
            }
        }

        /// <summary>批量更新多个模拟量输入。</summary>
        public void UpdateBatch(Dictionary<string, double> values, DateTime? timestamp = null)
        {
            foreach (var kv in values)
                Update(kv.Key, kv.Value, timestamp);
        }

        /// <summary>重置一个标签的峰值/谷值。</summary>
        public void ResetPeakValley(string name)
        {
            if (_states.TryGetValue(name, out var state))
            {
                state.Peak = double.MinValue;
                state.Valley = double.MaxValue;
                if (_peakResetTime.ContainsKey(name))
                    _peakResetTime[name] = DateTime.UtcNow;
            }
        }

        /// <summary>重置累计值。</summary>
        public void ResetTotal(string name)
        {
            if (_states.TryGetValue(name, out var state))
                state.Total = 0;
        }

        // ═══════════════════════════════════════════
        //  处理管线
        // ═══════════════════════════════════════════

        /// <summary>一阶低通滤波 (EWMA)。</summary>
        private double ApplyFilter(AnalogInputConfig config, AnalogInputState state, double raw)
        {
            if (config.FilterFactor <= 0) return raw;
            if (state.LastUpdateTime == DateTime.MinValue) return raw;
            // EWMA: filtered = α * raw + (1-α) * old
            double alpha = 1.0 - config.FilterFactor;
            return alpha * raw + config.FilterFactor * state.FilteredRaw;
        }

        /// <summary>线性缩放或多点校准。</summary>
        private double ApplyScaling(AnalogInputConfig config, double raw)
        {
            // 多点校准表
            if (config.CalibrationTable != null && config.CalibrationTable.Count >= 2)
            {
                return InterpolateTable(config.CalibrationTable, raw);
            }

            // 线性缩放: eng = (raw - rawMin) / (rawMax - rawMin) * (engMax - engMin) + engMin
            double rawRange = config.RawMax - config.RawMin;
            if (Math.Abs(rawRange) < 1e-10) return config.EngMin;

            double engRange = config.EngMax - config.EngMin;
            return (raw - config.RawMin) / rawRange * engRange + config.EngMin;
        }

        /// <summary>多点线性插值。</summary>
        private double InterpolateTable(List<(double Raw, double Eng)> table, double raw)
        {
            // 表必须按 Raw 升序排列
            if (raw <= table[0].Raw) return table[0].Eng;
            if (raw >= table[table.Count - 1].Raw) return table[table.Count - 1].Eng;

            for (int i = 0; i < table.Count - 1; i++)
            {
                if (raw >= table[i].Raw && raw <= table[i + 1].Raw)
                {
                    double t = (raw - table[i].Raw) / (table[i + 1].Raw - table[i].Raw);
                    return table[i].Eng + t * (table[i + 1].Eng - table[i].Eng);
                }
            }

            return table[table.Count - 1].Eng;
        }

        /// <summary>评估信号质量。</summary>
        private SignalQuality EvaluateQuality(AnalogInputConfig config, AnalogInputState state, DateTime now)
        {
            // 检查过期
            if (state.LastUpdateTime != DateTime.MinValue)
            {
                double elapsed = (now - state.LastUpdateTime).TotalMilliseconds;
                if (elapsed > config.StaleTimeoutMs)
                {
                    state.QualityReason = "数据过期";
                    return SignalQuality.Bad;
                }
            }

            // 检查超量程
            if (state.EngValue < config.EngMin || state.EngValue > config.EngMax)
            {
                state.QualityReason = "超出量程";
                return SignalQuality.Bad;
            }

            // 检查接近量程边界 (10% 以内)
            double range = config.EngMax - config.EngMin;
            if (range > 0)
            {
                double margin = range * 0.1;
                if (state.EngValue < config.EngMin + margin || state.EngValue > config.EngMax - margin)
                {
                    state.QualityReason = "接近量程边界";
                    return SignalQuality.Uncertain;
                }
            }

            state.QualityReason = "";
            return SignalQuality.Good;
        }

        /// <summary>评估报警状态（带死区）。</summary>
        private AlarmLevel EvaluateAlarm(AnalogInputConfig config, AnalogInputState state)
        {
            double val = state.EngValue;
            double db = config.AlarmDeadband;

            // 高高报警
            if (config.HighHighLimit.HasValue)
            {
                if (val >= config.HighHighLimit.Value) return AlarmLevel.HighHigh;
            }

            // 高报警
            if (config.HighLimit.HasValue)
            {
                if (val >= config.HighLimit.Value)
                {
                    // 如果已经在高高报警，不要降级到高报警
                    if (state.Alarm == AlarmLevel.HighHigh && config.HighHighLimit.HasValue &&
                        val >= config.HighHighLimit.Value - db)
                        return AlarmLevel.HighHigh;
                    return AlarmLevel.High;
                }
            }

            // 低报警
            if (config.LowLimit.HasValue)
            {
                if (val <= config.LowLimit.Value)
                {
                    if (state.Alarm == AlarmLevel.LowLow && config.LowLowLimit.HasValue &&
                        val <= config.LowLowLimit.Value + db)
                        return AlarmLevel.LowLow;
                    return AlarmLevel.Low;
                }
            }

            // 低低报警
            if (config.LowLowLimit.HasValue)
            {
                if (val <= config.LowLowLimit.Value) return AlarmLevel.LowLow;
            }

            return AlarmLevel.None;
        }

        /// <summary>更新峰值/谷值。</summary>
        private void UpdatePeakValley(AnalogInputConfig config, AnalogInputState state, DateTime now)
        {
            // 检查是否需要重置窗口
            if (config.PeakWindowSeconds > 0 && _peakResetTime.TryGetValue(config.Name, out var resetTime))
            {
                if ((now - resetTime).TotalSeconds > config.PeakWindowSeconds)
                {
                    state.Peak = state.EngValue;
                    state.Valley = state.EngValue;
                    _peakResetTime[config.Name] = now;
                    return;
                }
            }

            if (state.EngValue > state.Peak) state.Peak = state.EngValue;
            if (state.EngValue < state.Valley) state.Valley = state.EngValue;
        }

        /// <summary>更新累计值（梯形积分）。</summary>
        private void UpdateTotalization(AnalogInputConfig config, AnalogInputState state, DateTime now)
        {
            if (state.LastUpdateTime == DateTime.MinValue) return;

            double timeDelta = (now - state.LastUpdateTime).TotalHours; // 小时
            if (timeDelta <= 0) return;

            // 梯形积分: total += (old + new) / 2 * dt
            double avg = (state.EngValue + state.EngValue) / 2; // 简化为当前值
            state.Total += avg * timeDelta;
        }

        /// <summary>触发报警事件。</summary>
        private void RaiseAlarm(string name, AnalogInputConfig config, AnalogInputState state, AlarmLevel level, DateTime timestamp)
        {
            if (level == AlarmLevel.None) return;

            string levelStr = level switch
            {
                AlarmLevel.HighHigh => "高高",
                AlarmLevel.High => "高",
                AlarmLevel.Low => "低",
                AlarmLevel.LowLow => "低低",
                _ => ""
            };

            double threshold = level switch
            {
                AlarmLevel.HighHigh => config.HighHighLimit ?? 0,
                AlarmLevel.High => config.HighLimit ?? 0,
                AlarmLevel.Low => config.LowLimit ?? 0,
                AlarmLevel.LowLow => config.LowLowLimit ?? 0,
                _ => 0
            };

            state.AlarmMessage = $"{name}: {levelStr}报警 ({state.EngValue:F2} {config.Unit})";

            OnAlarm?.Invoke(this, new AnalogAlarmEventArgs
            {
                TagName = name,
                Level = level,
                Value = state.EngValue,
                Threshold = threshold,
                Message = state.AlarmMessage,
                Timestamp = timestamp
            });
        }

        // ═══════════════════════════════════════════
        //  诊断
        // ═══════════════════════════════════════════

        /// <summary>获取诊断摘要。</summary>
        public string GetDiagnosticSummary()
        {
            int total = _configs.Count;
            int good = _states.Count(s => s.Value.Quality == SignalQuality.Good);
            int uncertain = _states.Count(s => s.Value.Quality == SignalQuality.Uncertain);
            int bad = _states.Count(s => s.Value.Quality == SignalQuality.Bad);
            int alarming = _activeAlarms.Count(a => a.Value != AlarmLevel.None);

            return $"总计: {total}, 正常: {good}, 不确定: {uncertain}, 故障: {bad}, 报警: {alarming}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _configs.Clear();
            _states.Clear();
            _activeAlarms.Clear();
            _peakResetTime.Clear();
        }
    }
}
