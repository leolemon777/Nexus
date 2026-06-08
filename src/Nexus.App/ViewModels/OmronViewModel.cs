using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Omron;

namespace Nexus.App.ViewModels;

public partial class OmronViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 9600;

    public override string ProtocolName => "Omron FINS-TCP";
    public override string AddressHint => "e.g. D100, CIO100, W100, H100";

    private FinsTcpClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new FinsTcpClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
