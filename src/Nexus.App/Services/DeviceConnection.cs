using System;
using Nexus;

namespace Nexus.App.Services;

/// <summary>
/// Represents a named device connection for multi-device monitoring.
/// </summary>
public sealed class DeviceConnection : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public IReadWriteDevice Device { get; }
    public bool IsConnected => Device.IsConnected;
    public DateTime? ConnectedAt { get; private set; }

    public DeviceConnection(IReadWriteDevice device, string name, string protocol, string address)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Name = name;
        Protocol = protocol;
        Address = address;
    }

    public OperateResult Connect()
    {
        var result = Device.Connect();
        if (result.IsSuccess) ConnectedAt = DateTime.Now;
        return result;
    }

    public void Disconnect()
    {
        Device.Disconnect();
        ConnectedAt = null;
    }

    public void Dispose()
    {
        try { Device.Disconnect(); } catch { }
        try { Device.Dispose(); } catch { }
    }
}
