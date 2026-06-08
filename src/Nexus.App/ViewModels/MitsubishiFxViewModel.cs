using CommunityToolkit.Mvvm.ComponentModel;
using Nexus;
using Nexus.Mitsubishi;

namespace Nexus.App.ViewModels;

public partial class MitsubishiFxViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _comPort = "COM1";
    [ObservableProperty] private int _baudRate = 9600;

    public override string ProtocolName => "Mitsubishi FX Serial";
    public override string AddressHint => "e.g. D100, M100, Y0, X0";

    public string[] BaudRates { get; } = { "9600", "19200", "38400", "57600", "115200" };

    private System.IO.Ports.SerialPort? _serial;
    private FxSerialClient? _client;

    protected override OperateResult DoConnect()
    {
        _serial = new System.IO.Ports.SerialPort(ComPort, BaudRate, System.IO.Ports.Parity.Even, 7, System.IO.Ports.StopBits.One);
        _serial.Open();
        _client = new FxSerialClient(new SerialPortAdapter(_serial));
        return OperateResult.Success(); // FX 编程口通常不需要显式 Connect，直接收发
    }

    protected override void DoDisconnect()
    {
        _client?.Dispose(); _client = null;
        _serial?.Close(); _serial?.Dispose(); _serial = null;
    }

    protected override IReadWriteDevice? GetClient() => _client;
}
