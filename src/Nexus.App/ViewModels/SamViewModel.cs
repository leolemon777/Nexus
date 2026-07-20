using System;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexus;
using Nexus.Sam;
using IOParity = System.IO.Ports.Parity;
using IOStopBits = System.IO.Ports.StopBits;

namespace Nexus.App.ViewModels;

public partial class SamViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _portName = "COM1";
    [ObservableProperty] private int _baudRate = 115200;
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private string _parity = "None";
    [ObservableProperty] private string _stopBits = "One";
    [ObservableProperty] private int _timeout = 5000;
    [ObservableProperty] private string[] _availablePorts = new[] { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8" };

    public int[] BaudRates { get; } = { 9600, 19200, 38400, 57600, 115200 };
    public int[] DataBitsOptions { get; } = { 7, 8 };
    public string[] ParityValues { get; } = { "None", "Odd", "Even", "Mark", "Space" };
    public string[] StopBitValues { get; } = { "One", "Two", "OnePointFive" };

    public override string ProtocolName => "SAM ID Card";
    public override string AddressHint => "N/A (generic address; use Search/Select/ReadCard in code)";

    private SerialPort? _serialPort;
    private SamSerialClient? _client;

    public SamViewModel()
    {
        Address = "0";
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
        var client = new SamSerialClient(adapter, Timeout);

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

    public override string SampleCode => @"using Nexus.Sam;
using System.IO.Ports;

// 创建串口连接
var serial = new SerialPort(""COM1"", 115200, Parity.None, 8, StopBits.One);
serial.Open();
var client = new SamSerialClient(new SystemSerialPortAdapter(serial), 5000);

// 寻卡 -> 选卡 -> 读卡
if (client.SearchCard().IsSuccess
    && client.SelectCard().IsSuccess)
{
    var card = client.ReadCard();
    if (card.IsSuccess)
        Console.WriteLine($""姓名: {card.Content.Name}, 身份证号: {card.Content.IdNumber}"");
}

client.Disconnect();
serial.Close();";

    private static IOParity ParseParity(string value)
        => Enum.TryParse(value, true, out IOParity parsed) ? parsed : IOParity.None;

    private static IOStopBits ParseStopBits(string value)
        => Enum.TryParse(value, true, out IOStopBits parsed) ? parsed : IOStopBits.One;
}
