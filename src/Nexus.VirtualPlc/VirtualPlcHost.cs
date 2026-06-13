using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nexus.VirtualPlc
{
    /// <summary>
    /// 虚拟 PLC 宿主 — 整合内存模型、规则引擎和场景管理。
    /// <para>提供统一的 Start/Stop/LoadScenario API，</para>
    /// <para>可被各协议 VirtualServer 引用作为共享内存后端。</para>
    /// </summary>
    public class VirtualPlcHost : IDisposable
    {
        private readonly VirtualPlcMemory _memory;
        private readonly VirtualPlcRuleEngine _engine;
        private ScenarioScript? _currentScenario;
        private bool _disposed;

        /// <summary>共享内存。</summary>
        public VirtualPlcMemory Memory => _memory;

        /// <summary>规则引擎。</summary>
        public VirtualPlcRuleEngine Engine => _engine;

        /// <summary>当前加载的场景。</summary>
        public ScenarioScript? CurrentScenario => _currentScenario;

        /// <summary>宿主名称。</summary>
        public string Name { get; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning { get; private set; }

        public VirtualPlcHost(string name = "VirtualPlc")
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _memory = new VirtualPlcMemory();
            _engine = new VirtualPlcRuleEngine(_memory);
        }

        /// <summary>加载场景并启动。</summary>
        public void LoadScenario(ScenarioScript scenario)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            Stop();

            _currentScenario = scenario;
            scenario.Apply(_memory);
            _engine.ClearRules();
            _engine.LoadFromScenario(scenario);
        }

        /// <summary>从 JSON 文件加载场景定义并应用。</summary>
        public void LoadScenarioFromJson(string jsonPath)
        {
            if (jsonPath == null) throw new ArgumentNullException(nameof(jsonPath));
            if (!File.Exists(jsonPath)) throw new FileNotFoundException("Scenario JSON not found", jsonPath);

            var json = File.ReadAllText(jsonPath, Encoding.UTF8);
            var definition = SimpleJsonParser.ParseScenarioDefinition(json);
            var scenario = definition.ToScenarioScript();
            LoadScenario(scenario);
        }

        /// <summary>从 JSON 字符串加载场景定义并应用。</summary>
        public void LoadScenarioFromJsonString(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var definition = SimpleJsonParser.ParseScenarioDefinition(json);
            var scenario = definition.ToScenarioScript();
            LoadScenario(scenario);
        }

        /// <summary>启动规则引擎。</summary>
        public void Start()
        {
            if (IsRunning) return;
            _engine.Start();
            IsRunning = true;
        }

        /// <summary>停止规则引擎。</summary>
        public void Stop()
        {
            _engine.Stop();
            IsRunning = false;
        }

        /// <summary>重置到场景初始状态。</summary>
        public void Reset()
        {
            if (_currentScenario != null)
            {
                _currentScenario.Apply(_memory);
            }
            else
            {
                _memory.Clear();
            }
        }

        /// <summary>获取内存快照摘要。</summary>
        public VirtualPlcSnapshot GetSnapshot()
        {
            return new VirtualPlcSnapshot
            {
                HostName = Name,
                ScenarioName = _currentScenario?.Name ?? "(无)",
                IsRunning = IsRunning,
                RuleCount = _engine.RuleCount,
                FireCount = _engine.FireCount
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _engine.Dispose();
            _memory.Dispose();
        }
    }

    /// <summary>虚拟 PLC 快照。</summary>
    public class VirtualPlcSnapshot
    {
        /// <summary>宿主名称。</summary>
        public string HostName { get; set; } = "";

        /// <summary>场景名称。</summary>
        public string ScenarioName { get; set; } = "";

        /// <summary>是否运行中。</summary>
        public bool IsRunning { get; set; }

        /// <summary>活跃规则数。</summary>
        public int RuleCount { get; set; }

        /// <summary>规则触发次数。</summary>
        public int FireCount { get; set; }

        public override string ToString()
        {
            return $"[{HostName}] Scenario={ScenarioName}, Running={IsRunning}, Rules={RuleCount}, Fired={FireCount}";
        }
    }
}
