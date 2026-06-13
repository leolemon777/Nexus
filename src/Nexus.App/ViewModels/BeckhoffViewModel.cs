using Nexus.Beckhoff;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class BeckhoffViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 48898;
    [ObservableProperty] private string _targetNetId = "192.168.1.1.1.1";
    [ObservableProperty] private ushort _targetPort = 851;

    public override string ProtocolName => "Beckhoff ADS";
    public override string AddressHint => "e.g. MAIN.myVar, 0x4010:0x0";

    public override string SampleCode => @"using Nexus.Beckhoff;

// 创建客户端
var client = new BeckhoffAdsClient(""192.168.1.1"", 48898);
client.TargetNetId = ""192.168.1.1.1.1"";
client.TargetPort = 851;
client.Connect();

// 读取
var result = client.ReadInt16(""MAIN.myVar"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""MAIN.myVar"", (short)123);

client.Disconnect();";

    private BeckhoffAdsClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new BeckhoffAdsClient(IpAddress, Port);
        _client.TargetNetId = TargetNetId;
        _client.TargetPort = TargetPort;
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
