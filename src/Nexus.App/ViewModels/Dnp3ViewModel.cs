using Nexus.Dnp3;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class Dnp3ViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.50";
    [ObservableProperty] private int _port = 20000;

    public override string ProtocolName => "DNP3";
    public override string AddressHint => "e.g. Group30Var1:0";

    public override string SampleCode => @"using Nexus.Dnp3;

// 创建客户端
var client = new Dnp3Client(""192.168.1.50"", 20000);
client.Connect();

// 读取
var result = client.ReadInt16(""Group30Var1:0"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

client.Disconnect();";

    private Dnp3Client? _client;

    protected override OperateResult DoConnect()
    {
        _client = new Dnp3Client(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
