using Nexus.Beckhoff;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class BeckhoffViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 48898;
    [ObservableProperty] private string _targetNetId = "192.168.1.1.1.1";
    [ObservableProperty] private ushort _targetPort = 851;

    public override string ProtocolName => "Beckhoff ADS";
    public override string AddressHint => "e.g. MAIN.myVar, 0x4010:0x0";

    private BeckhoffAdsClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new BeckhoffAdsClient(IpAddress, Port);
        _client.TargetNetId = TargetNetId;
        _client.TargetPort = TargetPort;
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
