using Nexus.Modbus;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.App.ViewModels;

public partial class InovanceViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.10";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _slaveId = 1;

    public override string ProtocolName => "Inovance H3U/AM (Modbus TCP)";
    public override string AddressHint => "e.g. D100, M100, Y0, X0";

    private ModbusTcpClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new ModbusTcpClient(IpAddress, Port, SlaveId);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
