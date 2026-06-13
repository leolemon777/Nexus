using Nexus.Robot.Ur;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotUrViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.73";
    [ObservableProperty] private int _port = 30002;

    public override string ProtocolName => "UR Robot";
    public override string AddressHint => "e.g. D100";

    private UrClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new UrClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
