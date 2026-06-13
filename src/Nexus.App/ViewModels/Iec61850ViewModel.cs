using Nexus.Iec61850;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class Iec61850ViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.52";
    [ObservableProperty] private int _port = 102;

    public override string ProtocolName => "IEC 61850";
    public override string AddressHint => "e.g. LD0/LLN0";

    private Iec61850Client? _client;

    protected override OperateResult DoConnect()
    {
        _client = new Iec61850Client(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Iec61850;

// 创建客户端
var client = new Iec61850Client(""192.168.1.52"");
client.Connect();

// 读取
var result = client.ReadInt16(""LD0/LLN0$MX$Temperature"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""LD0/LLN0$MX$Temperature"", (short)123);

client.Disconnect();";
}
