using Nexus.Siemens.WebApi;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class SiemensWebApiViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 80;
    [ObservableProperty] private string _userName = "admin";
    [ObservableProperty] private string _password = "";

    public override string ProtocolName => "Siemens Web API";
    public override string AddressHint => "DB1.DBW0, M0.0, etc.";

    public override string SampleCode => @"using Nexus.Siemens.WebApi;

// 创建客户端 (S7-1200/1500 内置 Web API,默认端口 80)
var client = new SiemensWebApiClient(""192.168.1.1"", 80, ""admin"", """");
client.Connect();

// 读取
var result = client.ReadInt16(""DB1.DBW0"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 读取布尔
var bit = client.ReadBool(""M0.0"");

// 写入
client.Write(""DB1.DBW0"", (short)123);

client.Disconnect();";

    private SiemensWebApiClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new SiemensWebApiClient(IpAddress, Port, UserName, Password);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
