using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.GeSrtp;

namespace Nexus.App.ViewModels;

public partial class GeSrtpViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 18245;

    public override string ProtocolName => "GE SRTP";
    public override string AddressHint => "e.g. R100, AI10, AQ10, %I10, %Q10, %M10, %T10";

    private GeSrtpClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new GeSrtpClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
