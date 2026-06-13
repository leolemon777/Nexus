using Nexus.Robot.Yaskawa;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RobotYaskawaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.74";
    [ObservableProperty] private int _port = 80;

    public override string ProtocolName => "Yaskawa Robot";
    public override string AddressHint => "e.g. IO_IN_0";

    private Yrc1000Client? _client;

    protected override OperateResult DoConnect()
    {
        _client = new Yrc1000Client(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Robot.Yaskawa;

// 创建客户端
var client = new Yrc1000Client(""192.168.1.74"", 80);
client.Connect();

// 读取
var result = client.ReadInt16(""IO_IN_0"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""IO_IN_0"", (short)123);

client.Disconnect();";
}
