using Nexus.Hitachi;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class HitachiViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.200";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "Hitachi EH-150";
    public override string AddressHint => "e.g. D100, X0, Y10, M100, T0, C0";

    public override string SampleCode => @"using Nexus.Hitachi;

// 创建客户端
var client = new HitachiClient(""192.168.1.200"", 502, 1);
client.Connect();

// 读取
var result = client.ReadInt16(""D100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""D100"", (short)123);

client.Disconnect();";

    private HitachiClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new HitachiClient(IpAddress, Port, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
