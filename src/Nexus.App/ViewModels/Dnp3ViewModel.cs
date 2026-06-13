using Nexus.Dnp3;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class Dnp3ViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.50";
    [ObservableProperty] private int _port = 20000;

    public override string ProtocolName => "DNP3";
    public override string AddressHint => "e.g. Group30Var1:0";

    private Dnp3Client? _client;

    protected override OperateResult DoConnect()
    {
        _client = new Dnp3Client(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
