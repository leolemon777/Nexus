using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Siemens;

namespace Nexus.App.ViewModels;

public partial class SiemensPpiViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _comPort = "COM1";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private byte _station = 2;

    public override string ProtocolName => "Siemens PPI (S7-200)";
    public override string AddressHint => "e.g. V100, M10, Q0.0, I1.0";

    public string[] BaudRates { get; } = { "9600", "19200", "187500" };

    private System.IO.Ports.SerialPort? _serial;
    private SiemensPpiClient? _client;

    protected override OperateResult DoConnect()
    {
        _serial = new System.IO.Ports.SerialPort(ComPort, BaudRate, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
        _serial.Open();
        _client = new SiemensPpiClient(new SerialPortAdapter(_serial)) { SlaveAddress = Station };
        return _client.Connect();
    }

    protected override void DoDisconnect()
    {
        _client?.Disconnect(); _client?.Dispose(); _client = null;
        _serial?.Close(); _serial?.Dispose(); _serial = null;
    }

    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Siemens;
using System.IO.Ports;

// 创建串口连接
var serial = new SerialPort(""COM1"", 9600, Parity.None, 8, StopBits.One);
serial.Open();
var client = new SiemensPpiClient(new SerialPortAdapter(serial)) { SlaveAddress = 2 };

// 读取
var result = client.ReadInt16(""V100"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入
client.Write(""V100"", (short)123);

client.Disconnect();
serial.Close();";
}

// 简单的 SerialPort 适配器，实现 ISerialPort 接口
public class SerialPortAdapter : ISerialPort
{
    private readonly System.IO.Ports.SerialPort _port;
    public SerialPortAdapter(System.IO.Ports.SerialPort port) => _port = port;

    public string PortName { get => _port.PortName; set => _port.PortName = value; }
    public int BaudRate { get => _port.BaudRate; set => _port.BaudRate = value; }
    public int DataBits { get => _port.DataBits; set => _port.DataBits = value; }
    public StopBits StopBits { get => (StopBits)(int)_port.StopBits; set => _port.StopBits = (System.IO.Ports.StopBits)(int)value; }
    public Parity Parity { get => (Parity)(int)_port.Parity; set => _port.Parity = (System.IO.Ports.Parity)(int)value; }
    public int ReadTimeout { get => _port.ReadTimeout; set => _port.ReadTimeout = value; }
    public int WriteTimeout { get => _port.WriteTimeout; set => _port.WriteTimeout = value; }
    public bool IsOpen => _port.IsOpen;
    public bool DtrEnable { get => _port.DtrEnable; set => _port.DtrEnable = value; }
    public bool RtsEnable { get => _port.RtsEnable; set => _port.RtsEnable = value; }

    public void Open() => _port.Open();
    public void Close() => _port.Close();
    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);
    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);
    public void Dispose() => _port.Dispose();
}