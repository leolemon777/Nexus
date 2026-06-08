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
}
