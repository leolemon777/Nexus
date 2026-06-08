using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Siemens;

namespace Nexus.App.ViewModels;

public partial class SiemensViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.110";
    [ObservableProperty] private int _port = 102;
    [ObservableProperty] private int _rack = 0;
    [ObservableProperty] private int _slot = 1;

    public override string ProtocolName => "Siemens S7";
    public override string AddressHint => "e.g. DB1.DBW100, DB1.DBD200";

    public string[] ModelPresets { get; } = { "S7-1200", "S7-1500", "S7-300", "S7-200Smart", "S7-200" };

    private SiemensS7Client? _client;

    protected override OperateResult DoConnect()
    {
        _client = new SiemensS7Client(SiemensPLCS.S7_1200, IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
