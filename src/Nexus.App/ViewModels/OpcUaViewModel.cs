using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.OpcUa;

namespace Nexus.App.ViewModels;

public partial class OpcUaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "127.0.0.1";
    [ObservableProperty] private int _port = 4840;

    public override string ProtocolName => "OPC UA";
    public override string AddressHint => "e.g. ns=2;s=Temperature, ns=3;i=1001";

    private OpcUaClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new OpcUaClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
