using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Mitsubishi;

namespace Nexus.App.ViewModels;

public partial class MitsubishiViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.10";
    [ObservableProperty] private int _port = 6000;
    [ObservableProperty] private int _timeout = 5000;
    [ObservableProperty] private string _selectedTransport = TransportMc3EBinaryTcp;
    [ObservableProperty] private string _selectedModel = ModelQna3E;
    [ObservableProperty] private string _selectedByteOrder = nameof(Endianness.BigEndian);
    [ObservableProperty] private byte _networkNo = 0x00;
    [ObservableProperty] private byte _pcNo = 0xFF;
    [ObservableProperty] private ushort _destinationStationNo = 0x00;
    [ObservableProperty] private byte _waitTimeUnit = 0x00;
    [ObservableProperty] private byte _plcNumber = 0xFF;

    private const string TransportMc3EBinaryTcp = "MC 3E Binary TCP";
    private const string TransportMc3EAsciiTcp = "MC 3E ASCII TCP";
    private const string TransportMc3EUdpBinary = "MC 3E Binary UDP";
    private const string TransportMc3EUdpAscii = "MC 3E ASCII UDP";
    private const string TransportA1ETcp = "A1E Binary TCP";

    private const string ModelQna3E = "Q/L/iQ-R (QnA 3E)";
    private const string ModelA3E = "A Series 3E";
    private const string ModelFx3U = "FX3U";
    private const string ModelFx5U = "FX5U";

    public override string ProtocolName => "Mitsubishi MC / A1E";
    public override string AddressHint => "MC: D100, M100, X0, Y10, W1A, ZR100; A1E: D100, M100, X0, Y10, TS0, CN0";

    public string[] TransportPresets { get; } =
    {
        TransportMc3EBinaryTcp,
        TransportMc3EAsciiTcp,
        TransportMc3EUdpBinary,
        TransportMc3EUdpAscii,
        TransportA1ETcp
    };

    public string[] ModelPresets { get; } =
    {
        ModelQna3E,
        ModelA3E,
        ModelFx3U,
        ModelFx5U
    };

    public string[] ByteOrderPresets { get; } =
    {
        nameof(Endianness.BigEndian),
        nameof(Endianness.LittleEndian),
        nameof(Endianness.MidBigEndian),
        nameof(Endianness.MidLittleEndian)
    };

    private IReadWriteDevice? _client;

    protected override OperateResult DoConnect()
    {
        DoDisconnect();

        var client = CreateClient();
        AttachClientEvents(client);

        var result = client.Connect();
        if (!result.IsSuccess)
        {
            DetachClientEvents(client);
            client.Dispose();
            return result;
        }

        _client = client;
        AppendLog("[CFG] " + SelectedTransport + ", " + SelectedModel + ", " + IpAddress + ":" + Port);
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

    partial void OnSelectedTransportChanged(string value)
    {
        if (value == TransportA1ETcp)
        {
            if (Port == 5007 || Port == 6000) Port = 5551;
        }
        else
        {
            if (Port == 5551) Port = 6000;
        }
    }

    private IReadWriteDevice CreateClient()
    {
        var byteOrder = ParseByteOrder(SelectedByteOrder);
        var model = ParseModel(SelectedModel);

        switch (SelectedTransport)
        {
            case TransportMc3EAsciiTcp:
                var ascii = new Mc3EAsciiClient(model, IpAddress, Port, Timeout)
                {
                    NetworkNo = NetworkNo,
                    PcNo = PcNo,
                    DestinationStationNo = DestinationStationNo,
                    WaitTimeUnit = WaitTimeUnit,
                    ByteOrder = byteOrder
                };
                return ascii;

            case TransportMc3EUdpBinary:
            case TransportMc3EUdpAscii:
                var udp = new Mc3EUdpClient(model, IpAddress, Port, Timeout)
                {
                    UseAscii = SelectedTransport == TransportMc3EUdpAscii,
                    NetworkNo = NetworkNo,
                    PcNo = PcNo,
                    DestinationStationNo = DestinationStationNo,
                    WaitTimeUnit = WaitTimeUnit,
                    ByteOrder = byteOrder
                };
                return udp;

            case TransportA1ETcp:
                return new MelsecA1EClient(IpAddress, Port, Timeout)
                {
                    PLCNumber = PlcNumber
                };

            default:
                var binary = new Mc3EBinaryClient(model, IpAddress, Port, Timeout)
                {
                    NetworkNo = NetworkNo,
                    PcNo = PcNo,
                    DestinationStationNo = DestinationStationNo,
                    WaitTimeUnit = WaitTimeUnit,
                    ByteOrder = byteOrder
                };
                return binary;
        }
    }

    private static MitsubishiModel ParseModel(string model)
    {
        return model switch
        {
            ModelA3E => MitsubishiModel.A_3E,
            ModelFx3U => MitsubishiModel.FX_3U,
            ModelFx5U => MitsubishiModel.FX_5U,
            _ => MitsubishiModel.Qna_3E
        };
    }

    private static Endianness ParseByteOrder(string value)
    {
        return Enum.TryParse(value, out Endianness order) ? order : Endianness.BigEndian;
    }

    private void AttachClientEvents(IReadWriteDevice client)
    {
        if (client is TcpDeviceBase tcp)
        {
            tcp.OnMessageSent += OnMessageSent;
            tcp.OnMessageReceived += OnMessageReceived;
            tcp.OnError += OnClientError;
        }
        else if (client is UdpDeviceBase udp)
        {
            udp.OnMessageSent += OnMessageSent;
            udp.OnMessageReceived += OnMessageReceived;
            udp.OnError += OnClientError;
        }
    }

    private void DetachClientEvents(IReadWriteDevice client)
    {
        if (client is TcpDeviceBase tcp)
        {
            tcp.OnMessageSent -= OnMessageSent;
            tcp.OnMessageReceived -= OnMessageReceived;
            tcp.OnError -= OnClientError;
        }
        else if (client is UdpDeviceBase udp)
        {
            udp.OnMessageSent -= OnMessageSent;
            udp.OnMessageReceived -= OnMessageReceived;
            udp.OnError -= OnClientError;
        }
    }

    private void OnMessageSent(object? sender, string frame) => AppendLog("[TX] " + frame);
    private void OnMessageReceived(object? sender, string frame) => AppendLog("[RX] " + frame);
    private void OnClientError(object? sender, string message) => AppendLog("[ERR] " + message);
}
