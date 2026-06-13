using Nexus.Toledo;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class ToledoViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.61";
    [ObservableProperty] private int _port = 10000;

    public override string ProtocolName => "Toledo Scale";
    public override string AddressHint => "e.g. W";

    private ToledoClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new ToledoClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
