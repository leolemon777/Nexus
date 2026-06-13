using System;
using System.Collections.Generic;

namespace Nexus.VirtualPlc
{
    /// <summary>
    /// 场景脚本 — 预定义的虚拟 PLC 数据模式，用于模拟真实 PLC 行为。
    /// <para>内置场景: 温度传感器、电机控制、传送带模拟等。</para>
    /// <para>支持 PID 模拟、随机游走、正弦波等高级规则动作。</para>
    /// </summary>
    public class ScenarioScript
    {
        /// <summary>场景名称。</summary>
        public string Name { get; set; } = "";

        /// <summary>场景描述。</summary>
        public string Description { get; set; } = "";

        /// <summary>初始寄存器值列表。</summary>
        public List<RegisterPreset> RegisterPresets { get; set; } = new List<RegisterPreset>();

        /// <summary>初始线圈值列表。</summary>
        public List<CoilPreset> CoilPresets { get; set; } = new List<CoilPreset>();

        /// <summary>关联的规则列表。</summary>
        public List<ScenarioRule> Rules { get; set; } = new List<ScenarioRule>();

        /// <summary>将场景应用到虚拟 PLC 内存。</summary>
        public void Apply(VirtualPlcMemory memory)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));

            memory.Clear();

            foreach (var preset in CoilPresets)
                memory.SetBool(preset.Address, preset.Value);

            foreach (var preset in RegisterPresets)
                memory.SetInt16(preset.Address, preset.Value);
        }
    }

    /// <summary>寄存器预设值。</summary>
    public class RegisterPreset
    {
        /// <summary>寄存器地址。</summary>
        public int Address { get; set; }

        /// <summary>预设值。</summary>
        public short Value { get; set; }

        /// <summary>注释。</summary>
        public string Comment { get; set; } = "";
    }

    /// <summary>线圈预设值。</summary>
    public class CoilPreset
    {
        /// <summary>线圈地址。</summary>
        public int Address { get; set; }

        /// <summary>预设值。</summary>
        public bool Value { get; set; }

        /// <summary>注释。</summary>
        public string Comment { get; set; } = "";
    }

    /// <summary>
    /// 场景规则 — 当地址变化时触发预设动作。
    /// </summary>
    public class ScenarioRule
    {
        /// <summary>规则名称。</summary>
        public string Name { get; set; } = "";

        /// <summary>监视地址。</summary>
        public int WatchAddress { get; set; }

        /// <summary>触发条件值。</summary>
        public int TriggerValue { get; set; }

        /// <summary>触发后写入的地址。</summary>
        public int TargetAddress { get; set; }

        /// <summary>触发后写入的值。</summary>
        public short TargetValue { get; set; }

        /// <summary>是否为 Bool 类型目标。</summary>
        public bool IsBoolTarget { get; set; }

        /// <summary>延迟（毫秒），0 表示立即。</summary>
        public int DelayMs { get; set; }

        /// <summary>
        /// 高级动作表达式。
        /// <para>支持格式: "pid(target, setpoint, kp, ki, kd)", "random_walk(min, max, step)", "sine(amplitude, period_ms, offset)"</para>
        /// </summary>
        public string Action { get; set; } = "";

        public void Execute(VirtualPlcMemory memory)
        {
            if (!string.IsNullOrEmpty(Action))
            {
                ExecuteAdvancedAction(memory);
                return;
            }

            if (IsBoolTarget)
                memory.SetBool(TargetAddress, TargetValue != 0);
            else
                memory.SetInt16(TargetAddress, TargetValue);
        }

        private void ExecuteAdvancedAction(VirtualPlcMemory memory)
        {
            if (Action.StartsWith("pid("))
                ExecutePid(memory);
            else if (Action.StartsWith("random_walk("))
                ExecuteRandomWalk(memory);
            else if (Action.StartsWith("sine("))
                ExecuteSine(memory);
        }

        private void ExecutePid(VirtualPlcMemory memory)
        {
            var args = ParseActionArgs(5, 5);
            if (args == null) return;

            int targetAddr = (int)args[0];
            int setpointAddr = (int)args[1];
            int kpAddr = (int)args[2];
            int kiAddr = (int)args[3];
            int kdAddr = (int)args[4];

            double setpoint = memory.GetInt16(setpointAddr);
            double processVar = memory.GetInt16(targetAddr);
            double kp = memory.GetInt16(kpAddr) / 10.0;
            double ki = memory.GetInt16(kiAddr) / 10.0;
            double kd = memory.GetInt16(kdAddr) / 10.0;

            double error = setpoint - processVar;
            double output = kp * error;
            double newProcessVar = processVar + output * 0.1;
            memory.SetInt16(targetAddr, (short)newProcessVar);
        }

        private void ExecuteRandomWalk(VirtualPlcMemory memory)
        {
            var args = ParseActionArgs(3);
            if (args == null) return;

            int min = (int)args[0];
            int max = (int)args[1];
            int step = (int)args[2];

            short current = memory.GetInt16(TargetAddress);
            var rng = new Random();
            int delta = rng.Next(-step, step + 1);
            int newValue = Math.Max(min, Math.Min(max, current + delta));
            memory.SetInt16(TargetAddress, (short)newValue);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _sineStartTicks = new();

        private void ExecuteSine(VirtualPlcMemory memory)
        {
            var args = ParseActionArgs(3);
            if (args == null) return;

            double amplitude = args[0];
            double periodMs = args[1];
            double offset = args[2];

            long startTick;
            int key = TargetAddress;
            if (!_sineStartTicks.TryGetValue(key, out startTick))
            {
                startTick = DateTime.Now.Ticks;
                _sineStartTicks.TryAdd(key, startTick);
            }

            double elapsedMs = (DateTime.Now.Ticks - startTick) / 10000.0;
            double angle = 2.0 * Math.PI * elapsedMs / periodMs;
            double value = offset + amplitude * Math.Sin(angle);
            memory.SetInt16(TargetAddress, (short)value);
        }

        private double[]? ParseActionArgs(int expectedCount)
        {
            return ParseActionArgs(expectedCount, expectedCount);
        }

        private double[]? ParseActionArgs(int minCount, int maxCount)
        {
            int start = Action.IndexOf('(');
            int end = Action.IndexOf(')');
            if (start < 0 || end < 0 || end <= start) return null;

            var parts = Action.Substring(start + 1, end - start - 1).Split(',');
            if (parts.Length < minCount || parts.Length > maxCount) return null;

            var result = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i].Trim(), out result[i]))
                    return null;
            }
            return result;
        }
    }

    // ── 内置场景工厂 ──────────────────────────────

    /// <summary>
    /// 内置场景工厂 — 提供常用的 PLC 模拟场景。
    /// </summary>
    public static class BuiltInScenarios
    {
        /// <summary>温度传感器场景: D0=250 (25.0°C), D1=报警阈值, D2=当前状态。</summary>
        public static ScenarioScript TemperatureSensor()
        {
            return new ScenarioScript
            {
                Name = "温度传感器",
                Description = "模拟温度传感器: D0=当前温度(×10), D1=报警阈值, M0=报警输出",
                RegisterPresets = new List<RegisterPreset>
                {
                    new RegisterPreset { Address = 0, Value = 250, Comment = "当前温度 25.0°C" },
                    new RegisterPreset { Address = 1, Value = 500, Comment = "报警阈值 50.0°C" },
                    new RegisterPreset { Address = 2, Value = 0, Comment = "温度状态 0=正常" }
                },
                CoilPresets = new List<CoilPreset>
                {
                    new CoilPreset { Address = 0, Value = false, Comment = "报警输出" }
                },
                Rules = new List<ScenarioRule>
                {
                    new ScenarioRule
                    {
                        Name = "高温报警",
                        WatchAddress = 0,
                        TriggerValue = -1, // any change
                        TargetAddress = 0,
                        TargetValue = 0,
                        IsBoolTarget = true,
                        DelayMs = 0
                    }
                }
            };
        }

        /// <summary>电机控制场景: M0=启动, M1=停止, D10=速度设定, D11=当前速度, M2=运行状态。</summary>
        public static ScenarioScript MotorControl()
        {
            return new ScenarioScript
            {
                Name = "电机控制",
                Description = "模拟电机控制: M0=启动, M1=停止, D10=目标转速, D11=当前转速, M2=运行中",
                RegisterPresets = new List<RegisterPreset>
                {
                    new RegisterPreset { Address = 10, Value = 1500, Comment = "目标转速 RPM" },
                    new RegisterPreset { Address = 11, Value = 0, Comment = "当前转速 RPM" },
                    new RegisterPreset { Address = 12, Value = 0, Comment = "运行时间(秒)" }
                },
                CoilPresets = new List<CoilPreset>
                {
                    new CoilPreset { Address = 0, Value = false, Comment = "启动命令" },
                    new CoilPreset { Address = 1, Value = false, Comment = "停止命令" },
                    new CoilPreset { Address = 2, Value = false, Comment = "运行状态" }
                }
            };
        }

        /// <summary>传送带场景: D20=速度, D21=计件, M3=运行, M4=传感器。</summary>
        public static ScenarioScript ConveyorBelt()
        {
            return new ScenarioScript
            {
                Name = "传送带",
                Description = "模拟传送带: D20=速度(Hz), D21=计件数, M3=运行, M4=光电传感器",
                RegisterPresets = new List<RegisterPreset>
                {
                    new RegisterPreset { Address = 20, Value = 50, Comment = "变频器速度 50Hz" },
                    new RegisterPreset { Address = 21, Value = 0, Comment = "产品计数" },
                    new RegisterPreset { Address = 22, Value = 100, Comment = "目标产量" }
                },
                CoilPresets = new List<CoilPreset>
                {
                    new CoilPreset { Address = 3, Value = false, Comment = "传送带运行" },
                    new CoilPreset { Address = 4, Value = false, Comment = "光电传感器" }
                }
            };
        }

        /// <summary>空白场景。</summary>
        public static ScenarioScript Blank()
        {
            return new ScenarioScript
            {
                Name = "空白",
                Description = "空 PLC 内存，无预设数据"
            };
        }

        /// <summary>PID 温控场景: 模拟 PID 控制器调节温度。</summary>
        public static ScenarioScript PidTemperature()
        {
            return new ScenarioScript
            {
                Name = "PID温控",
                Description = "模拟 PID 温度控制: D0=当前温度, D1=设定值, D2=输出, D3=Kp, D4=Ki, D5=Kd",
                RegisterPresets = new List<RegisterPreset>
                {
                    new RegisterPreset { Address = 0, Value = 200, Comment = "当前温度 20.0°C" },
                    new RegisterPreset { Address = 1, Value = 250, Comment = "设定温度 25.0°C" },
                    new RegisterPreset { Address = 2, Value = 0, Comment = "PID 输出" },
                    new RegisterPreset { Address = 3, Value = 50, Comment = "Kp = 5.0" },
                    new RegisterPreset { Address = 4, Value = 10, Comment = "Ki = 1.0" },
                    new RegisterPreset { Address = 5, Value = 20, Comment = "Kd = 2.0" }
                },
                Rules = new List<ScenarioRule>
                {
                    new ScenarioRule
                    {
                        Name = "PID计算",
                        WatchAddress = -1, // timer-driven
                        TriggerValue = -1,
                        TargetAddress = 2,
                        TargetValue = 0,
                        Action = "pid(0, 1, 3, 4, 5)"
                    }
                }
            };
        }

        /// <summary>随机游走场景: 模拟随机波动数据。</summary>
        public static ScenarioScript RandomWalkSensor()
        {
            return new ScenarioScript
            {
                Name = "随机游走",
                Description = "模拟随机波动: D10=当前值, 在 100-500 之间随机游走",
                RegisterPresets = new List<RegisterPreset>
                {
                    new RegisterPreset { Address = 10, Value = 300, Comment = "初始值" }
                },
                Rules = new List<ScenarioRule>
                {
                    new ScenarioRule
                    {
                        Name = "随机游走",
                        WatchAddress = -1,
                        TriggerValue = -1,
                        TargetAddress = 10,
                        TargetValue = 0,
                        Action = "random_walk(100, 500, 5)"
                    }
                }
            };
        }

        /// <summary>正弦波场景: 模拟周期性信号。</summary>
        public static ScenarioScript SineWaveSensor()
        {
            return new ScenarioScript
            {
                Name = "正弦波",
                Description = "模拟正弦波信号: D20=输出值, 振幅 100, 周期 5000ms, 偏移 300",
                RegisterPresets = new List<RegisterPreset>
                {
                    new RegisterPreset { Address = 20, Value = 300, Comment = "正弦波输出" }
                },
                Rules = new List<ScenarioRule>
                {
                    new ScenarioRule
                    {
                        Name = "正弦波",
                        WatchAddress = -1,
                        TriggerValue = -1,
                        TargetAddress = 20,
                        TargetValue = 0,
                        Action = "sine(100, 5000, 300)"
                    }
                }
            };
        }
    }

    /// <summary>
    /// JSON 场景定义 — 用于从 JSON 文件加载场景配置。
    /// </summary>
    public class ScenarioDefinition
    {
        /// <summary>场景名称。</summary>
        public string Name { get; set; } = "";

        /// <summary>场景描述。</summary>
        public string Description { get; set; } = "";

        /// <summary>寄存器预设列表。</summary>
        public List<RegisterPreset> Registers { get; set; } = new List<RegisterPreset>();

        /// <summary>线圈预设列表。</summary>
        public List<CoilPreset> Coils { get; set; } = new List<CoilPreset>();

        /// <summary>规则列表。</summary>
        public List<ScenarioRule> Rules { get; set; } = new List<ScenarioRule>();

        /// <summary>转换为 ScenarioScript。</summary>
        public ScenarioScript ToScenarioScript()
        {
            return new ScenarioScript
            {
                Name = Name,
                Description = Description,
                RegisterPresets = Registers,
                CoilPresets = Coils,
                Rules = Rules
            };
        }
    }
}
