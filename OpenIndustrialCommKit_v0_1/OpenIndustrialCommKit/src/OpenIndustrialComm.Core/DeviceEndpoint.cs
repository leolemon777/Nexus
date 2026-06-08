namespace OpenIndustrialComm.Core;

public enum EndpointScheme
{
    Tcp,
    Udp,
    Serial,
    Tls,
    WebSocket,
    Http,
    Can,
    Ble,
    Usb,
    Custom
}

public sealed record DeviceEndpoint(
    EndpointScheme Scheme,
    string HostOrPort,
    int? Port = null,
    IReadOnlyDictionary<string, string>? Options = null)
{
    public static DeviceEndpoint Tcp(string host, int port) => new(EndpointScheme.Tcp, host, port);

    public static DeviceEndpoint Serial(string portName, int baudRate, string parity = "None", int dataBits = 8, int stopBits = 1) =>
        new(EndpointScheme.Serial, portName, null, new Dictionary<string, string>
        {
            ["baudRate"] = baudRate.ToString(),
            ["parity"] = parity,
            ["dataBits"] = dataBits.ToString(),
            ["stopBits"] = stopBits.ToString()
        });
}
