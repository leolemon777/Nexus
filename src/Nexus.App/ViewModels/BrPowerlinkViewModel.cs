using Nexus.BrPowerlink;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class BrPowerlinkViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 34962;

    public override string ProtocolName => "B&R POWERLINK";
    public override string AddressHint => "e.g. 1.6000.0, 6000.0x00, 0x6000.1";

    public override string SampleCode => @"using Nexus.BrPowerlink;

// 创建客户端
var client = new BrPowerlinkClient(""192.168.1.1"", 34962);
client.Connect();

// 读取
var result = client.ReadInt16(""1.6000.0"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""1.6000.0"", (short)123);

client.Disconnect();";

    private BrPowerlinkClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new BrPowerlinkClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
