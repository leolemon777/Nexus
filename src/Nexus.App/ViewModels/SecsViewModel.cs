using Nexus.Secs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class SecsViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.54";
    [ObservableProperty] private int _port = 5000;

    public override string ProtocolName => "SECS HSMS";
    public override string AddressHint => "e.g. S1F1";

    private SecsHsmsClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new SecsHsmsClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Secs;

// 创建客户端
var client = new SecsHsmsClient(""192.168.1.54"", 5000);
client.Connect();

// 读取
var result = client.ReadInt16(""S1F1"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""S1F1"", (short)123);

client.Disconnect();";
}
