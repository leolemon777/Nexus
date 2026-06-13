using Nexus.Robot.Fanuc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotFanucViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.71";
    [ObservableProperty] private int _port = 6000;

    public override string ProtocolName => "FANUC Robot";
    public override string AddressHint => "e.g. R[1]";

    private FanucRobotClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new FanucRobotClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Robot.Fanuc;

// 创建客户端
var client = new FanucRobotClient(""192.168.1.71"", 6000);
client.Connect();

// 读取
var result = client.ReadInt16(""R1"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""R1"", (short)123);

client.Disconnect();";
}
