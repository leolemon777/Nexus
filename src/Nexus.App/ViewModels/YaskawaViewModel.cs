using Nexus.Yaskawa;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class YaskawaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.50";
    [ObservableProperty] private int _port = 502;

    public override string ProtocolName => "YASKAWA Memobus TCP";
    public override string AddressHint => "e.g. 100, x=3;100, M100, G200";

    private MemobusClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new MemobusClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
