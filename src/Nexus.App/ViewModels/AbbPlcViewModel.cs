using Nexus.AbbPlc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class AbbPlcViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.100";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "ABB AC500";
    public override string AddressHint => "e.g. %MW100, %IW0, %M10";

    public override string SampleCode => @"using Nexus.AbbPlc;

var client = new AbbPlcClient(""192.168.1.100"");
client.Connect();
var result = client.ReadInt16(""%MW100"");
if (result.IsSuccess)
    Console.WriteLine(result.Content);
client.Write(""%MW100"", (short)123);
client.Disconnect();";

    private AbbPlcClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new AbbPlcClient(IpAddress, Port, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
