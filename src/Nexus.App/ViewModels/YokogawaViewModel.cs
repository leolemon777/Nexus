using Nexus.Yokogawa;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class YokogawaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 8000;

    public override string ProtocolName => "Yokogawa Vnet/IP";
    public override string AddressHint => "e.g. X001, Y000, A001, D001";

    private YokogawaClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new YokogawaClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
