using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Mitsubishi;

namespace Nexus.App.ViewModels;

public partial class MitsubishiViewModel : ProtocolViewModelBase
{
    [ObservableProperty] private string _ipAddress = "192.168.1.10";
    [ObservableProperty] private int _port = 6000;

    public override string ProtocolName => "Mitsubishi MC";
    public override string AddressHint => "e.g. D100, M100, X0, Y10";

    public string[] ModelPresets { get; } = { "Q Series", "L Series", "FX5U", "iQ-R" };

    private Mc3EBinaryClient? _client;

    protected override OperateResult DoConnect()
    {
        _client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, IpAddress, Port);
        return _client.Connect();
    }
    protected override void DoDisconnect() { _client?.Disconnect(); _client?.Dispose(); _client = null; }
    protected override IReadWriteDevice? GetClient() => _client;
}
