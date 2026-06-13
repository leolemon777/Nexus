using Nexus.Keyence;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class KeyenceViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 3000;
    [ObservableProperty] private byte _station = 0;

    public override string ProtocolName => "Keyence KV";
    public override string AddressHint => "e.g. DM100, WR200, MR10";

    public override string SampleCode => @"using Nexus.Keyence;

// 创建客户端
var client = new KeyenceKvClient(""192.168.1.1"", 3000, 0);
client.Connect();

// 读取
var result = client.ReadInt16(""DM100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""DM100"", (short)123);

client.Disconnect();";

    private KeyenceKvClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new KeyenceKvClient(IpAddress, Port, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
