using Nexus.BoschRexroth;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class BoschRexrothViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.1";
    [ObservableProperty] private int _port = 44818;
    [ObservableProperty] private byte _slot = 0;

    public override string ProtocolName => "Bosch ctrlX CIP";
    public override string AddressHint => "e.g. MyTag, Program:Main.Tag, DINT_Array[0]";

    public override string SampleCode => @"using Nexus.BoschRexroth;

// 创建客户端
var client = new BoschCtrlxClient(""192.168.1.1"", 44818, 0);
client.Connect();

// 读取
var result = client.ReadInt16(""MyTag"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""MyTag"", (short)123);

client.Disconnect();";

    private BoschCtrlxClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new BoschCtrlxClient(IpAddress, Port, Slot);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
