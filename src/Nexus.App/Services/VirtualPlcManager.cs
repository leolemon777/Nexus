using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.AllenBradley;
using Nexus.Delta;
using Nexus.Fanuc;
using Nexus.Fatek;
using Nexus.Fuji;
using Nexus.GeSrtp;
using Nexus.Keyence;
using Nexus.Kuka;
using Nexus.LsElectric;
using Nexus.Mitsubishi;
using Nexus.Modbus;
using Nexus.Omron;
using Nexus.Siemens;
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

        [ObservableProperty] private int _runningCount;

        public VirtualPlcManager()
        {
            RegisterAll();
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
