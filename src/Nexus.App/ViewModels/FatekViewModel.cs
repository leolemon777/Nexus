using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Fatek;

namespace Nexus.App.ViewModels;

public partial class FatekViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 5000;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "Fatek FBs";
    public override string AddressHint => "e.g. D100, R0, Y0, X0, M100, T0, C0";

    private FatekClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new FatekClient(IpAddress, Port, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
