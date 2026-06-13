using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.GeSrtp;

namespace Nexus.App.ViewModels;

public partial class GeSrtpViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 18245;

    public override string ProtocolName => "GE SRTP";
    public override string AddressHint => "e.g. R100, AI10, AQ10, %I10, %Q10, %M10, %T10";

    public override string SampleCode => @"using Nexus.GeSrtp;

// 创建客户端
var client = new GeSrtpClient(""192.168.1.1"", 18245);
client.Connect();

// 读取
var result = client.ReadInt16(""R100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""R100"", (short)123);

client.Disconnect();";

    private GeSrtpClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new GeSrtpClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
