using Nexus.Rkc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class RkcViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.60";
    [ObservableProperty] private int _port = 10001;

    public override string ProtocolName => "RKC Temperature";
    public override string AddressHint => "e.g. M1";

    private RkcTemperatureClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new RkcTemperatureClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Rkc;

// 创建客户端
var client = new RkcTemperatureClient(""192.168.1.60"", 10001);
client.Connect();

// 读取
var result = client.ReadInt16(""01"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""01"", (short)123);

client.Disconnect();";
}
