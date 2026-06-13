using Nexus.Modbus;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class EurothermViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _comPort = "COM1";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private byte _slaveId = 1;

    public override string ProtocolName => "Eurotherm 2400/2500 (Modbus RTU)";
    public override string AddressHint => "e.g. 1 (PV), 2 (SP), 130 (Setpoint)";

    public string[] BaudRates { get; } = { "9600", "19200", "38400", "57600", "115200" };

    private System.IO.Ports.SerialPort? _serial;
    private ModbusRtuClient? _client;

    protected override OperateResult DoConnect()
    {
        _serial = new System.IO.Ports.SerialPort(ComPort, BaudRate, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
        _serial.Open();
        _client = new ModbusRtuClient(new SystemSerialPortAdapter(_serial), SlaveId);
        return _client.Connect();
    }
    protected override void DoDisconnect()
    {
        _client?.Disconnect(); _client?.Dispose(); _client = null;
        _serial?.Close(); _serial?.Dispose(); _serial = null;
    }
    protected override IReadWriteDevice? GetClient() => _client;

    public override string SampleCode => @"using Nexus.Modbus;
using System.IO.Ports;

// 创建串口 (Eurotherm 2400/2500 使用 Modbus RTU)
var serial = new SerialPort(""COM1"", 9600, Parity.None, 8, StopBits.One);
serial.Open();
var client = new ModbusRtuClient(new SystemSerialPortAdapter(serial), 1);

// 读取 (地址 1 = PV)
var result = client.ReadInt16(""1"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 写入 (地址 2 = SP)
client.Write(""2"", (short)123);

client.Disconnect();
serial.Close();";
}
