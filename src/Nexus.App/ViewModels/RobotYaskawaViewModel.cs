using Nexus.Robot.Yaskawa;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotYaskawaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.74";
    [ObservableProperty] private int _port = 80;

    public override string ProtocolName => "Yaskawa Robot";
    public override string AddressHint => "e.g. IO_IN_0";

    private Yrc1000Client? _client;

    protected override OperateResult DoConnect()
    {
        _client = new Yrc1000Client(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
