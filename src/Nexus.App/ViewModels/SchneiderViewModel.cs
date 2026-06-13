using Nexus.Schneider;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class SchneiderViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.40";
    [ObservableProperty] private int _port = 502;

    public override string ProtocolName => "Schneider Modicon";
    public override string AddressHint => "e.g. %MW0";

    public override string SampleCode => @"using Nexus.Schneider;

// 创建客户端
var client = new SchneiderModiconClient(""192.168.1.40"", 502);
client.Connect();

// 读取
var result = client.ReadInt16(""%MW100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""%MW100"", (short)123);

client.Disconnect();";

    private SchneiderModiconClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new SchneiderModiconClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
