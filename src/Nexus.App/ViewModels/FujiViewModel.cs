using Nexus.Fuji;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class FujiViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _comPort = "COM1";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private byte _station = 1;

    public override string ProtocolName => "Fuji SPH/SPB";
    public override string AddressHint => "e.g. D100, M100, X100, Y100, T100, C100";

    public string[] BaudRates { get; } = { "9600", "19200", "38400", "57600", "115200" };

    private System.IO.Ports.SerialPort? _serial;
    private FujiSphClient? _client;

    protected override OperateResult DoConnect()
    {
        _serial = new System.IO.Ports.SerialPort(ComPort, BaudRate, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
        _serial.Open();
        _client = new FujiSphClient(_serial.BaseStream, Station);
        return _client.Connect();
    }
    protected override void DoDisconnect()
    {
        _client?.Disconnect(); _client?.Dispose(); _client = null;
        _serial?.Close(); _serial?.Dispose(); _serial = null;
    }
    protected override IReadWriteDevice? GetClient() => _client;
}
