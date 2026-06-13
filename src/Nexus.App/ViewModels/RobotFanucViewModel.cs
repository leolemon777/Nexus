using Nexus.Robot.Fanuc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotFanucViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.71";
    [ObservableProperty] private int _port = 6000;

    public override string ProtocolName => "FANUC Robot";
    public override string AddressHint => "e.g. R[1]";

    private FanucRobotClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new FanucRobotClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
