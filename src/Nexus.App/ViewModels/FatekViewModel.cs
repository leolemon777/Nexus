using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Fatek;

namespace Nexus.App.ViewModels;

public partial class FatekViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 5000;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "Fatek FBs";
    public override string AddressHint => "e.g. D100, R0, Y0, X0, M100, T0, C0";

    public override string SampleCode => @"using Nexus.Fatek;

// 创建客户端
var client = new FatekClient(""192.168.1.1"", 5000, 1);
client.Connect();

// 读取
var result = client.ReadInt16(""D100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""D100"", (short)123);

client.Disconnect();";

    private FatekClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new FatekClient(IpAddress, Port, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
