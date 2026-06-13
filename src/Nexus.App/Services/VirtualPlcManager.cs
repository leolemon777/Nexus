using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.AllenBradley;
using Nexus.Beckhoff;
using Nexus.Delta;
using Nexus.Dnp3;
using Nexus.Fanuc;
using Nexus.Fatek;
using Nexus.Fuji;
using Nexus.GeSrtp;
using Nexus.Iec104;
using Nexus.Iec61850;
using Nexus.Inovance;
using Nexus.Keyence;
using Nexus.Kuka;
using Nexus.LsElectric;
using Nexus.Mitsubishi;
using Nexus.Modbus;
using Nexus.Omron;
using Nexus.Panasonic;
using Nexus.Rkc;
using Nexus.Robot.Efort;
using Nexus.Robot.Fanuc;
using Nexus.Robot.Kuka;
using Nexus.Robot.Staubli;
using Nexus.Robot.Ur;
using Nexus.Robot.Yamaha;
using Nexus.Robot.Yaskawa;
using Nexus.Schneider;
using Nexus.Secs;
using Nexus.Siemens;
using Nexus.Toledo;
using Nexus.VirtualPlc;
using Nexus.Xinje;
using Nexus.Yaskawa;
using Nexus.Yokogawa;

namespace Nexus.App.Services
{
    /// <summary>
    /// 虚拟 PLC 管理器 — 统一管理所有协议虚拟服务器的生命周期。
    /// 提供 Start/Stop/Status 管理，支持多协议并行运行。
    /// </summary>
    public sealed partial class VirtualPlcManager : ObservableObject, IDisposable
    {
        public ObservableCollection<VirtualPlcEntry> Entries { get; } = new();

        /// <summary>可用场景列表。</summary>
        public ObservableCollection<ScenarioScript> AvailableScenarios { get; } = new();

        /// <summary>共享虚拟 PLC 内存（供场景预设使用）。</summary>
        public VirtualPlcMemory SharedMemory { get; } = new VirtualPlcMemory();

        [ObservableProperty] private int _runningCount;
        [ObservableProperty] private string _selectedScenarioName = "";

        public VirtualPlcManager()
        {
            RegisterAll();
            LoadScenarios();
        }

        internal void OnEntryStateChanged() => UpdateCount();

        /// <summary>
        /// 注册所有可用的虚拟服务器。使用高位端口避免与真实设备端口冲突。
        /// </summary>
        private void RegisterAll()
        {
            Register("Modbus TCP",        "127.0.0.1", 10502, port => new ModbusVirtualServer(port));
            Register("Siemens FetchWrite", "127.0.0.1", 10720, port => new SiemensFetchWriteVirtualServer(port));
            Register("Mitsubishi MC3E",   "127.0.0.1", 15007, port => new Mc3EVirtuServer(port));
            Register("Mitsubishi A1E",    "127.0.0.1", 15008, port => new MelsecA1EVirtualServer(port));
            Register("Omron FINS",        "127.0.0.1", 19600, port => new FinsVirtualServer(port));
            Register("Omron HostLink",    "127.0.0.1", 19601, port => new OmronHostLinkVirtualServer(port));
            Register("AB CIP",            "127.0.0.1", 44818, port => new CipVirtualServer(port));
            Register("AB PCCC",           "127.0.0.1", 44819, port => new PcccVirtualServer(port));
            Register("Yaskawa Memobus",   "127.0.0.1", 10503, port => new MemobusVirtualServer(port));
            Register("Yokogawa",          "127.0.0.1", 10504, port => new YokogawaVirtualServer(port));
            Register("Fatek",             "127.0.0.1", 15000, port => new FatekVirtualServer(port));
            Register("Fuji",              "127.0.0.1", 19000, port => new FujiVirtualServer(port));
            Register("GE SRTP",           "127.0.0.1", 18245, port => new GeSrtpVirtualServer(port));
            Register("Xinje",             "127.0.0.1", 15021, port => new XinjeVirtualServer(port));
            Register("Delta DVP",         "127.0.0.1", 15020, port => new DeltaDvpVirtualServer(port));
            Register("LS XGT",            "127.0.0.1", 20040, port => new LsXgtVirtualServer(port));
            Register("Keyence KV",        "127.0.0.1", 15022, port => new KeyenceKvVirtualServer(port));
            Register("KUKA EKI",          "127.0.0.1", 54601, port => new KukaEkiVirtualServer(port));
            Register("FANUC FOCAS",       "127.0.0.1", 81930, port => new FanucFocasVirtualServer(port));
            Register("Beckhoff ADS",      "127.0.0.1", 49001, port => new BeckhoffAdsVirtualServer(port));
            Register("IEC 104",           "127.0.0.1", 22404, port => new Iec104VirtualServer(port));
            Register("Staubli VAL3",      "127.0.0.1", 59001, port => new StaubliVirtualServer(port));
            Register("UR Robot",          "127.0.0.1", 30004, port => new UrVirtualServer(port));
            Register("Siemens S7",        "127.0.0.1", 10201, port => new SiemensS7VirtualPlc(SiemensPLCS.S7_1200, port));
            Register("Panasonic FP",      "127.0.0.1", 19094, port => new PanasonicVirtualServer(port));
            Register("DNP3",              "127.0.0.1", 20000, port => new Dnp3VirtualServer(port));
            Register("IEC 61850",         "127.0.0.1", 10201, port => new Iec61850VirtualServer(port));
            Register("Inovance Easy",     "127.0.0.1", 15023, port => new InovanceEasyVirtualServer(port));
            Register("Schneider",         "127.0.0.1", 50203, port => new SchneiderVirtualServer(port));
            Register("RKC Temperature",   "127.0.0.1", 15024, port => new RkcTemperatureVirtualServer(port));
            Register("Toledo Scale",      "127.0.0.1", 15025, port => new ToledoVirtualServer(port));
            Register("Robot Efort",       "127.0.0.1", 60001, port => new EfortVirtualServer(port));
            Register("Robot Fanuc",       "127.0.0.1", 60002, port => new FanucRobotVirtualServer(port));
            Register("Robot KUKA",        "127.0.0.1", 60003, port => new KukaTcpVirtualServer(port));
            Register("Robot Yamaha",      "127.0.0.1", 60004, port => new YamahaRcxVirtualServer(port));
            Register("Robot Yaskawa",     "127.0.0.1", 60005, port => new Yrc1000VirtualServer(port));
            Register("SECS HSMS",        "127.0.0.1", 5000,  port => new SecsHsmsVirtualServer(port));
        }

        public void Register(string protocol, string defaultHost, int defaultPort, Func<int, IDisposable> startFunc)
        {
            var entry = new VirtualPlcEntry(protocol, defaultHost, defaultPort, startFunc, OnEntryStateChanged);
            Entries.Add(entry);
        }

        [RelayCommand]
        public void StartAll()
        {
            foreach (var entry in Entries)
            {
                if (!entry.IsRunning)
                    entry.Start();
            }
            UpdateCount();
        }

        [RelayCommand]
        public void StopAll()
        {
            foreach (var entry in Entries)
            {
                if (entry.IsRunning)
                    entry.Stop();
            }
            UpdateCount();
        }

        private void UpdateCount()
        {
            int count = 0;
            foreach (var entry in Entries)
            {
                if (entry.IsRunning) count++;
            }
            RunningCount = count;
        }

        private void LoadScenarios()
        {
            AvailableScenarios.Add(BuiltInScenarios.Blank());
            AvailableScenarios.Add(BuiltInScenarios.TemperatureSensor());
            AvailableScenarios.Add(BuiltInScenarios.MotorControl());
            AvailableScenarios.Add(BuiltInScenarios.ConveyorBelt());
            SelectedScenarioName = AvailableScenarios[0].Name;
        }

        [RelayCommand]
        public void ApplyScenario(string scenarioName)
        {
            foreach (var s in AvailableScenarios)
            {
                if (s.Name == scenarioName)
                {
                    s.Apply(SharedMemory);
                    SelectedScenarioName = scenarioName;
                    return;
                }
            }
        }

        public void Dispose()
        {
            StopAll();
        }
    }

    /// <summary>
    /// 单个虚拟 PLC 条目 — 管理 Start/Stop 和端口。
    /// </summary>
    public sealed partial class VirtualPlcEntry : ObservableObject, IDisposable
    {
        private readonly Func<int, IDisposable> _startFunc;
        private IDisposable? _server;
        private readonly Action? _onStateChanged;

        public string Protocol { get; }
        public string DefaultHost { get; }
        public int DefaultPort { get; }

        [ObservableProperty] private int _port;
        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private string _status = "Stopped";

        public VirtualPlcEntry(string protocol, string defaultHost, int defaultPort, Func<int, IDisposable> startFunc, Action? onStateChanged = null)
        {
            Protocol = protocol;
            DefaultHost = defaultHost;
            DefaultPort = defaultPort;
            Port = defaultPort;
            _startFunc = startFunc;
            _onStateChanged = onStateChanged;
        }

        [RelayCommand]
        public void Start()
        {
            Stop();
            try
            {
                _server = _startFunc(Port);
                IsRunning = true;
                Status = $"Running :{Port}";
            }
            catch (Exception ex)
            {
                Status = $"Error: {ex.Message}";
            }
            _onStateChanged?.Invoke();
        }

        [RelayCommand]
        public void Stop()
        {
            if (_server != null)
            {
                try { _server.Dispose(); } catch { }
                _server = null;
            }
            IsRunning = false;
            Status = "Stopped";
            _onStateChanged?.Invoke();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
