using Nexus.Iec104;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class Iec104ViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.51";
    [ObservableProperty] private int _port = 2404;

    public override string ProtocolName => "IEC 104";
    public override string AddressHint => "e.g. ASDU";

    private Iec104Client? _client;

    protected override OperateResult DoConnect()
    {
        _client = new Iec104Client(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
