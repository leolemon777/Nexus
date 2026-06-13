using Nexus.Robot.Staubli;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotStaubliViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.78";
    [ObservableProperty] private int _port = 52530;

    public override string ProtocolName => "Staubli Robot";
    public override string AddressHint => "e.g. joint_pos";

    private StaubliClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new StaubliClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Robot.Staubli;

// 创建客户端
var client = new StaubliClient(""192.168.1.78"", 52530);
client.Connect();

// 读取
var result = client.ReadInt16(""joint_pos"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""joint_pos"", (short)123);

client.Disconnect();";
}
