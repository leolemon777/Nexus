using Nexus.Iec104;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class Iec104ViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.51";
    [ObservableProperty] private int _port = 2404;

    public override string ProtocolName => "IEC 104";
    public override string AddressHint => "e.g. ASDU";

    private Iec104Client? _client;

    protected override OperateResult DoConnect()
    {
        _client = new Iec104Client(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Iec104;

// 创建客户端
var client = new Iec104Client(""192.168.1.51"");
client.Connect();

// 读取
var result = client.ReadInt16(""1"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""1"", (short)123);

client.Disconnect();";
}
