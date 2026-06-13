using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Kuka;

namespace Nexus.App.ViewModels;

public partial class KukaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 54600;

    public override string ProtocolName => "KUKA EKI";
    public override string AddressHint => "e.g. $POS_ACT, $OV_PRO, $TIMER[1], $FLAG[1]";

    public override string SampleCode => @"using Nexus.Kuka;

// 创建客户端
var client = new KukaEkiClient(""192.168.1.1"", 54600);
client.Connect();

// 读取
var result = client.ReadString(""$POS_ACT"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

client.Disconnect();";

    private KukaEkiClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new KukaEkiClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
