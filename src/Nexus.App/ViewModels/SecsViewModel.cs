using Nexus.Secs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class SecsViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.54";
    [ObservableProperty] private int _port = 5000;

    public override string ProtocolName => "SECS HSMS";
    public override string AddressHint => "e.g. S1F1";

    private SecsHsmsClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new SecsHsmsClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
