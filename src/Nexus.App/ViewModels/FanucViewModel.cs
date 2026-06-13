using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Fanuc;

namespace Nexus.App.ViewModels;

public partial class FanucViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 8193;

    public override string ProtocolName => "FANUC FOCAS";
    public override string AddressHint => "e.g. D100, R0, G0, F0, A0, C0, T0, K0";

    public override string SampleCode => @"using Nexus.Fanuc;

// 创建客户端
var client = new FanucClient(""192.168.1.1"", 8193);
client.Connect();

// 读取
var result = client.ReadInt16(""R1"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""R1"", (short)123);

client.Disconnect();";

    private FanucClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new FanucClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
