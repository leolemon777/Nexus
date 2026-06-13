using Nexus.Delta;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class DeltaViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _comPort = "COM1";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "Delta DVP";
    public override string AddressHint => "e.g. D100, Y0, X0, M100, T100, C100";

    public string[] BaudRates { get; } = { "9600", "19200", "38400", "57600", "115200" };

    public override string SampleCode => @"using Nexus.Delta;

// 创建串口客户端
var serial = new System.IO.Ports.SerialPort(""COM1"", 9600, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
serial.Open();
var client = new DeltaDvpClient(serial.BaseStream, 1);
client.Connect();

// 读取
var result = client.ReadInt16(""D100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""D100"", (short)123);

client.Disconnect();
serial.Close();";

    private System.IO.Ports.SerialPort? _serial;
    private DeltaDvpClient? _client;

    protected override OperateResult DoConnect()
    {
        _serial = new System.IO.Ports.SerialPort(ComPort, BaudRate, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
        _serial.Open();
        _client = new DeltaDvpClient(_serial.BaseStream, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect()
    {
        _client?.Disconnect(); _client?.Dispose(); _client = null;
        _serial?.Close(); _serial?.Dispose(); _serial = null;
    }
    protected override IReadWriteDevice? GetClient() => _client;
}
