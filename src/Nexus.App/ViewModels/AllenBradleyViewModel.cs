using Nexus.AllenBradley;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class AllenBradleyViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 44818;
    [ObservableProperty] private byte _slot = 0;

    public override string ProtocolName => "Allen-Bradley CIP";
    public override string AddressHint => "e.g. Program:MainProgram.Tag1, MyTag[0]";

    private AllenBradleyCipClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new AllenBradleyCipClient(IpAddress, Port, Slot);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
