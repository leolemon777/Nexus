using Nexus.Modbus;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class InovanceViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.10";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _slaveId = 1;

    public override string ProtocolName => "Inovance H3U/AM (Modbus TCP)";
    public override string AddressHint => "e.g. D100, M100, Y0, X0";

    public override string SampleCode => @"using Nexus.Modbus;

// 创建客户端 (汇川 H3U/AM 使用 Modbus TCP)
var client = new ModbusTcpClient(""192.168.1.10"", 502, 1);
client.Connect();

// 读取
var result = client.ReadInt16(""D100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""D100"", (short)123);

client.Disconnect();";

    private ModbusTcpClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new ModbusTcpClient(IpAddress, Port, SlaveId);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
