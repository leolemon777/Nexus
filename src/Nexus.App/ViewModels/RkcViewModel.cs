using Nexus.Rkc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RkcViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.60";
    [ObservableProperty] private int _port = 10001;

    public override string ProtocolName => "RKC Temperature";
    public override string AddressHint => "e.g. M1";

    private RkcTemperatureClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new RkcTemperatureClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
