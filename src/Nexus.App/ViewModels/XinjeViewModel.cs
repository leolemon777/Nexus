using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Xinje;

namespace Nexus.App.ViewModels;

public partial class XinjeViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "信捷 Xinje";
    public override string AddressHint => "e.g. D100, HD100, SD100, Y0, X0, M100, T0, C100, S100";

    public override string SampleCode => @"using Nexus.Xinje;

// 创建客户端
var client = new XinjeClient(""192.168.1.1"", 502, 1);
client.Connect();

// 读取
var result = client.ReadInt16(""D100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""D100"", (short)123);

client.Disconnect();";

    private XinjeClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new XinjeClient(IpAddress, Port, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
