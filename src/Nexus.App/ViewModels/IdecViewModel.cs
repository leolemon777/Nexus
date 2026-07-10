using Nexus.Idec;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class IdecViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.5";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _station = 0;

    public override string ProtocolName => "IDEC Computer Link";
    public override string AddressHint => "e.g. D100, M100, X7, Y10";

    public override string SampleCode => @"using Nexus.Idec;

// 创建客户端
var client = new IdecHostLinkClient(""192.168.1.5"", 502, 0);
client.Connect();

// 读取
var result = client.ReadInt16(""D100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""D100"", (short)123);

client.Disconnect();";

    private IdecHostLinkClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new IdecHostLinkClient(IpAddress, Port, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
