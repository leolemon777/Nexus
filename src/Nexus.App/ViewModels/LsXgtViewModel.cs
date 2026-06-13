using Nexus.LsElectric;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class LsXgtViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 2004;

    public override string ProtocolName => "LS XGT";
    public override string AddressHint => "e.g. D100, M100, P100, K100";

    public override string SampleCode => @"using Nexus.LsElectric;

// 创建客户端
var client = new LsXgtClient(""192.168.1.1"", 2004);
client.Connect();

// 读取
var result = client.ReadInt16(""D100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""D100"", (short)123);

client.Disconnect();";

    private LsXgtClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new LsXgtClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
