using Nexus.Panasonic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class PanasonicViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _comPort = "COM1";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "Panasonic Mewtocol";
    public override string AddressHint => "e.g. DT100, WR100, FL100, SV100";

    public string[] BaudRates { get; } = { "9600", "19200", "38400", "57600", "115200" };

    public override string SampleCode => @"using Nexus.Panasonic;

// 创建串口客户端
var serial = new System.IO.Ports.SerialPort(""COM1"", 9600, System.IO.Ports.Parity.Odd, 8, System.IO.Ports.StopBits.One);
serial.Open();
var client = new PanasonicMewtocolClient(serial.BaseStream, 1);
client.Connect();

// 读取
var result = client.ReadInt16(""DT100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""DT100"", (short)123);

client.Disconnect();
serial.Close();";

    private System.IO.Ports.SerialPort? _serial;
    private PanasonicMewtocolClient? _client;

    protected override OperateResult DoConnect()
    {
        _serial = new System.IO.Ports.SerialPort(ComPort, BaudRate, System.IO.Ports.Parity.Odd, 8, System.IO.Ports.StopBits.One);
        _serial.Open();
        _client = new PanasonicMewtocolClient(_serial.BaseStream, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect()
    {
        _client?.Disconnect(); _client?.Dispose(); _client = null;
        _serial?.Close(); _serial?.Dispose(); _serial = null;
    }
    protected override IReadWriteDevice? GetClient() => _client;
}
