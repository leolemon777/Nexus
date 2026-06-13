using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.OpcUa;

namespace Nexus.App.ViewModels;

public partial class OpcUaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "127.0.0.1";
    [ObservableProperty] private int _port = 4840;

    public override string ProtocolName => "OPC UA";
    public override string AddressHint => "e.g. ns=2;s=Temperature, ns=3;i=1001";

    private OpcUaClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new OpcUaClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.OpcUa;

// 创建客户端
var client = new OpcUaClient(""opc.tcp://127.0.0.1:4840"");
client.Connect();

// 读取
var result = client.ReadInt16(""ns=2;s=Temperature"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""ns=2;s=Temperature"", (short)123);

client.Disconnect();";
}
