using Nexus.Yaskawa;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class YaskawaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.50";
    [ObservableProperty] private int _port = 502;

    public override string ProtocolName => "YASKAWA Memobus TCP";
    public override string AddressHint => "e.g. 100, x=3;100, M100, G200";

    public override string SampleCode => @"using Nexus.Yaskawa;

// 创建客户端
var client = new MemobusClient(""192.168.1.50"", 502);
client.Connect();

// 读取
var result = client.ReadInt16(""D100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""D100"", (short)123);

client.Disconnect();";

    private MemobusClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new MemobusClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
