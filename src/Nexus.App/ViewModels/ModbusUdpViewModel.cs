using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.App.Services;
using Nexus.Modbus;

namespace Nexus.App.ViewModels;

public partial class ModbusUdpViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "127.0.0.1";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _slaveId = 1;
    [ObservableProperty] private int _timeout = 3000;

    private ModbusUdpClient? _client;

    public ModbusUdpViewModel(PacketRecorderService packetRecorder)
        : base(packetRecorder, "modbus-udp", ModbusPacketTransport.Udp)
    {
        Address = "0";
        DataType = "Int16";
    }

    public override string ProtocolName => "Modbus UDP";
    public override string AddressHint => "e.g. 0, 100, 40001, 00001";

    protected override OperateResult DoConnect()
    {
        DoDisconnect();

        var client = new ModbusUdpClient(IpAddress, Port, SlaveId, Timeout);
        AttachClientEvents(client);
        var result = client.Connect();
        if (!result.IsSuccess)
        {
            DetachClientEvents(client);
            client.Dispose();
            return result;
        }

        _client = client;
        return result;
    }

    protected override void DoDisconnect()
    {
        var client = _client;
        if (client == null) return;

        DetachClientEvents(client);
        try { client.Disconnect(); } catch { }
        client.Dispose();
        _client = null;
    }

    protected override IReadWriteDevice? GetClient() => _client;

    private void AttachClientEvents(ModbusUdpClient client)
    {
        client.OnMessageSent += OnMessageSent;
        client.OnMessageReceived += OnMessageReceived;
        client.OnError += OnClientError;
    }

    private void DetachClientEvents(ModbusUdpClient client)
    {
        client.OnMessageSent -= OnMessageSent;
        client.OnMessageReceived -= OnMessageReceived;
        client.OnError -= OnClientError;
    }

    private void OnMessageSent(object? sender, string frame) => RecordPacket("TX", frame, ModbusPacketDirection.Request);
    private void OnMessageReceived(object? sender, string frame) => RecordPacket("RX", frame, ModbusPacketDirection.Response);
    private void OnClientError(object? sender, string message) => AppendLog("[ERR] " + message);
}
