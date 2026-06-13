using Nexus.Robot.Ur;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotUrViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.73";
    [ObservableProperty] private int _port = 30002;

    public override string ProtocolName => "UR Robot";
    public override string AddressHint => "e.g. D100";

    private UrClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new UrClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Robot.Ur;

// 创建客户端
var client = new UrClient(""192.168.1.73"", 30002);
client.Connect();

// 读取
var result = client.ReadInt16(""output_int_register_0"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""output_int_register_0"", (short)123);

client.Disconnect();";
}
