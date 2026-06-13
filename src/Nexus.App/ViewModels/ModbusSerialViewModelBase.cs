using System;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus;
using Nexus.App.Services;
using Nexus.Modbus;
using IOParity = System.IO.Ports.Parity;
using IOStopBits = System.IO.Ports.StopBits;

namespace Nexus.App.ViewModels;

public abstract partial class ModbusSerialViewModelBase : ProtocolViewModelBase
{
    [ObservableProperty] private string _portName = "COM1";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private string _parity = "None";
    [ObservableProperty] private string _stopBits = "One";
    [ObservableProperty] private byte _slaveId = 1;
    [ObservableProperty] private int _timeout = 3000;
    [ObservableProperty] private bool _dtrEnable;
    [ObservableProperty] private bool _rtsEnable;
    [ObservableProperty] private string[] _availablePorts = new[] { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8" };

    private SerialPort? _serialPort;
    private SerialDeviceBase? _client;

    public int[] BaudRates { get; } = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
    public int[] DataBitsOptions { get; } = { 7, 8 };
    public string[] ParityValues { get; } = { "None", "Odd", "Even", "Mark", "Space" };
    public string[] StopBitValues { get; } = { "One", "Two", "OnePointFive" };

    private readonly PacketRecorderService? _serialPacketRecorder;

    protected ModbusSerialViewModelBase()
    {
        Address = "0";
        DataType = "Int16";
    }

    protected ModbusSerialViewModelBase(PacketRecorderService packetRecorder)
        : base(packetRecorder, "modbus-rtu", ModbusPacketTransport.Rtu)
    {
        _serialPacketRecorder = packetRecorder;
        Address = "0";
        DataType = "Int16";
    }

    public override string AddressHint => "e.g. 0, 100, 40001, 00001";

    protected abstract SerialDeviceBase CreateClient(ISerialPort port, byte station, int timeout);

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
            DtrEnable = DtrEnable,
            RtsEnable = RtsEnable
        };

        var adapter = new SystemSerialPortAdapter(serial);
        var client = CreateClient(adapter, SlaveId, Timeout);
        AttachClientEvents(client);

        var result = client.Connect();
        if (!result.IsSuccess)
        {
            DetachClientEvents(client);
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
        var client = _client;
        if (client != null)
        {
            DetachClientEvents(client);
            try { client.Disconnect(); } catch { }
            client.Dispose();
            _client = null;
        }

        _serialPort?.Dispose();
        _serialPort = null;
    }

    protected override IReadWriteDevice? GetClient() => _client;

        [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        try
        {
            var ports = await Task.Run(() =>
            {
                var p = SerialPort.GetPortNames();
                Array.Sort(p, StringComparer.OrdinalIgnoreCase);
                return p;
            }).ConfigureAwait(true);
            AvailablePorts = ports.Length > 0 ? ports : new[] { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8" };
            if (AvailablePorts.Length > 0 && (string.IsNullOrWhiteSpace(PortName) || !System.Array.Exists(AvailablePorts, p => p == PortName)))
                PortName = AvailablePorts[0];
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] 刷新串口失败: " + ex.Message);
        }
    }
    private void AttachClientEvents(SerialDeviceBase client)
    {
        client.OnMessageSent += OnMessageSent;
        client.OnMessageReceived += OnMessageReceived;
        client.OnError += OnClientError;
    }

    private void DetachClientEvents(SerialDeviceBase client)
    {
        client.OnMessageSent -= OnMessageSent;
        client.OnMessageReceived -= OnMessageReceived;
        client.OnError -= OnClientError;
    }

    private void OnMessageSent(object? sender, string frame) => RecordPacket("TX", frame, ModbusPacketDirection.Request);
    private void OnMessageReceived(object? sender, string frame) => RecordPacket("RX", frame, ModbusPacketDirection.Response);
    private void OnClientError(object? sender, string message) => AppendLog("[ERR] " + message);

    private static IOParity ParseParity(string value)
    {
        return Enum.TryParse(value, true, out IOParity parsed) ? parsed : IOParity.None;
    }

    private static IOStopBits ParseStopBits(string value)
    {
        return Enum.TryParse(value, true, out IOStopBits parsed) ? parsed : IOStopBits.One;
    }
}
