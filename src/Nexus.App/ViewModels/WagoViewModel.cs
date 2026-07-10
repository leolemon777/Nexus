using Nexus.Wago;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class WagoViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.17";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "WAGO PLC";
    public override string AddressHint => "e.g. %MW100, %IW0, %QX0, %IX0";

    public override string SampleCode => @"using Nexus.Wago;

var client = new WagoPlcClient(""192.168.1.17"");
client.Connect();
var result = client.ReadInt16(""%MW100"");
if (result.IsSuccess)
    Console.WriteLine(result.Content);
client.Write(""%MW100"", (short)123);
client.Disconnect();";

    private WagoPlcClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new WagoPlcClient(IpAddress, Port, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
