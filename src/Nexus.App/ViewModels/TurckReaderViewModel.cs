using Nexus.Turck;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class TurckReaderViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.100";
    [ObservableProperty] private int _port = 10000;

    public override string ProtocolName => "Turck RFID";
    public override string AddressHint => "Block number (0-255)";

    public override string SampleCode => @"using Nexus.Turck;

// 创建客户端
var client = new TurckReaderClient(""192.168.1.100"", 10000);
client.Connect();

// 读 UID
var uid = client.ReadUid();
if (uid.IsSuccess)
    Console.WriteLine($""UID: {uid.Content}"");

// 读数据块 (从块 0 开始读 4 块)
var rd = client.ReadBlocks(0, 4);
if (rd.IsSuccess)
    Console.WriteLine(BitConverter.ToString(rd.Content));

// 写数据块
client.WriteBlocks(0, new byte[] { 0x01, 0x02, 0x03, 0x04 });

client.Disconnect();";

    private TurckReaderClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new TurckReaderClient(IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
