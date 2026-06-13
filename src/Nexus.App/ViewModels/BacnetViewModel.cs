using Nexus.Bacnet;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class BacnetViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.53";
    [ObservableProperty] private int _port = 47808;

    public override string ProtocolName => "BACnet/IP";
    public override string AddressHint => "e.g. analogInput:1";

    private BacnetIpClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new BacnetIpClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
