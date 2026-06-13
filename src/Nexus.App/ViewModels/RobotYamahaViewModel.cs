using Nexus.Robot.Yamaha;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotYamahaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.75";
    [ObservableProperty] private int _port = 10000;

    public override string ProtocolName => "Yamaha Robot";
    public override string AddressHint => "e.g. IO_STATUS";

    private YamahaRcxClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new YamahaRcxClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Robot.Yamaha;

// 创建客户端
var client = new YamahaRcxClient(""192.168.1.75"", 10000);
client.Connect();

// 读取
var result = client.ReadInt16(""IO_STATUS"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""IO_STATUS"", (short)123);

client.Disconnect();";
}
