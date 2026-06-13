using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Siemens;

namespace Nexus.App.ViewModels;

public partial class FetchWriteViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.110";
    [ObservableProperty] private int _port = 102;
    [ObservableProperty] private int _timeout = 5000;

    public override string ProtocolName => "Siemens Fetch/Write";
    public override string AddressHint => "e.g. I100, Q100, M100, DB1.100, T100, C100";

    private SiemensFetchWriteClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new SiemensFetchWriteClient(IpAddress, Port, Timeout);
        return _client.Connect();
    }

    protected override void DoDisconnect()
    {
        _client?.Disconnect(); _client?.Dispose(); _client = null;
    }

    protected override IReadWriteDevice? GetClient() => _client;
}
