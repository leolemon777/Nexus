using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Siemens;

namespace Nexus.App.ViewModels;

public partial class FetchWriteViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.110";
    [ObservableProperty] private int _port = 102;
    [ObservableProperty] private int _timeout = 5000;

    public override string ProtocolName => "Siemens Fetch/Write";
    public override string AddressHint => "e.g. I100, Q100, M100, DB1.100, T100, C100";

    private SiemensFetchWriteClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new SiemensFetchWriteClient(IpAddress, Port, Timeout);
        return _client.Connect();
    }

    protected override void DoDisconnect()
    {
        _client?.Disconnect(); _client?.Dispose(); _client = null;
    }

    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Siemens;

// 创建客户端
var client = new SiemensFetchWriteClient(""192.168.1.110"", 102);
client.Connect();

// 读取
var result = client.ReadInt16(""DB1.100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""DB1.100"", (short)123);

client.Disconnect();";
}
