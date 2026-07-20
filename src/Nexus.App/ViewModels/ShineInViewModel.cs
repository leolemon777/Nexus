using System;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexus;
using Nexus.ShineIn;
using IOParity = System.IO.Ports.Parity;
using IOStopBits = System.IO.Ports.StopBits;

namespace Nexus.App.ViewModels;

public partial class ShineInViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _portName = "COM1";
    [ObservableProperty] private int _baudRate = 57600;
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private string _parity = "Even";
    [ObservableProperty] private string _stopBits = "One";
    [ObservableProperty] private int _timeout = 3000;
    [ObservableProperty] private string[] _availablePorts = new[] { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8" };

    public int[] BaudRates { get; } = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
    public int[] DataBitsOptions { get; } = { 7, 8 };
    public string[] ParityValues { get; } = { "None", "Odd", "Even", "Mark", "Space" };
    public string[] StopBitValues { get; } = { "One", "Two", "OnePointFive" };

    public override string ProtocolName => "ShineIn Light";
    public override string AddressHint => "Channel number (1-8)";

    private SerialPort? _serialPort;
    private ShineInLightSourceController? _client;

    public ShineInViewModel()
    {
        Address = "1";
        DataType = "Int16";
    }

    protected override OperateResult DoConnect()
    {
        DoDisconnect();

        var serial = new SerialPort(
            PortName,
            BaudRate,
            ParseParity(Parity),
            DataBits,
            ParseStopBits(StopBits))
        {
            ReadTimeout = Timeout,
            WriteTimeout = Timeout,
        };

        var adapter = new SystemSerialPortAdapter(serial);
        var client = new ShineInLightSourceController(adapter, Timeout);

        var result = client.Connect();
        if (!result.IsSuccess)
        {
            client.Dispose();
            serial.Dispose();
            return result;
        }

        _serialPort = serial;
        _client = client;
        return result;
    }

    protected override void DoDisconnect()
    {
        if (_client != null)
        {
            try { _client.Disconnect(); } catch { }
            _client.Dispose();
            _client = null;
        }
        _serialPort?.Dispose();
        _serialPort = null;
    }

    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.ShineIn;
using System.IO.Ports;

// 创建串口连接（默认 57600, 8, Even, One）
var serial = new SerialPort(""COM1"", 57600, Parity.Even, 8, StopBits.One);
serial.Open();
var client = new ShineInLightSourceController(new SystemSerialPortAdapter(serial), 3000);

// 读通道 1 的光源参数
var read = client.Read(1);
if (read.IsSuccess)
    Console.WriteLine(read.Content);

// 设置通道 1 亮度
client.SetBrightness(1, 0xFF);
// 关闭通道 1
client.TurnOff(1);

client.Disconnect();
serial.Close();";

    private static IOParity ParseParity(string value)
        => Enum.TryParse(value, true, out IOParity parsed) ? parsed : IOParity.None;

    private static IOStopBits ParseStopBits(string value)
        => Enum.TryParse(value, true, out IOStopBits parsed) ? parsed : IOStopBits.One;
}
