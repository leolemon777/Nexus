using Nexus.Robot.Efort;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotEfortViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.70";
    [ObservableProperty] private int _port = 8080;

    public override string ProtocolName => "Efort Robot";
    public override string AddressHint => "e.g. joint_pos";

    private EfortClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new EfortClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
