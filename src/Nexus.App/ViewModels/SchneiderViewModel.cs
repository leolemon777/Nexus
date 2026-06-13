using Nexus.Schneider;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class SchneiderViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.40";
    [ObservableProperty] private int _port = 502;

    public override string ProtocolName => "Schneider Modicon";
    public override string AddressHint => "e.g. %MW0";

    private SchneiderModiconClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new SchneiderModiconClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
