using Nexus.Robot.Kuka;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotKukaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.72";
    [ObservableProperty] private int _port = 7000;

    public override string ProtocolName => "KUKA Robot";
    public override string AddressHint => "e.g. $POS_ACT";

    private KukaVarProxyClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new KukaVarProxyClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Robot.Kuka;

// 创建客户端
var client = new KukaVarProxyClient(""192.168.1.72"", 7000);
client.Connect();

// 读取
var result = client.ReadInt16(""$POS_ACT"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""$POS_ACT"", (short)123);

client.Disconnect();";
}
