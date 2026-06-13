using Nexus.Robot.Staubli;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotStaubliViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.78";
    [ObservableProperty] private int _port = 52530;

    public override string ProtocolName => "Staubli Robot";
    public override string AddressHint => "e.g. joint_pos";

    private StaubliClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new StaubliClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
