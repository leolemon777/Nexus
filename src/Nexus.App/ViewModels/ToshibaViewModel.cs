using Nexus.Toshiba;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class ToshibaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.10";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "Toshiba V200";
    public override string AddressHint => "e.g. 400101, 00005";

    public override string SampleCode => @"using Nexus.Toshiba;

var client = new ToshibaClient(""192.168.1.10"");
client.Connect();
var result = client.ReadInt16(""400101"");
if (result.IsSuccess)
    Console.WriteLine(result.Content);
client.Write(""400101"", (short)123);
client.Disconnect();";

    private ToshibaClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new ToshibaClient(IpAddress, Port, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
