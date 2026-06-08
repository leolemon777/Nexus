namespace OpenIndustrialComm.Core;

public interface IDeviceClient : IAsyncDisposable
{
    ProtocolDescriptor Descriptor { get; }
    DeviceEndpoint Endpoint { get; }
    bool IsConnected { get; }

    Task<OperationResult> ConnectAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IReadWriteDeviceClient : IDeviceClient
{
    Task<OperationResult<T>> ReadAsync<T>(string address, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyDictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default);
    Task<OperationResult> WriteAsync<T>(string address, T value, CancellationToken cancellationToken = default);
}

public interface ISubscribeDeviceClient : IDeviceClient
{
    IAsyncEnumerable<DataPointValue<T>> SubscribeAsync<T>(string address, TimeSpan samplingInterval, CancellationToken cancellationToken = default);
}

public interface IRawFrameClient : IDeviceClient
{
    Task<OperationResult<byte[]>> SendReceiveAsync(byte[] request, CancellationToken cancellationToken = default);
}
