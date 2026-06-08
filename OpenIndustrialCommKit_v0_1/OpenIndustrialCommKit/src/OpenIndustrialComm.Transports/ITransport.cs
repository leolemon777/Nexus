namespace OpenIndustrialComm.Transports;

public interface ITransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}

public sealed record TransportOptions(
    TimeSpan ConnectTimeout,
    TimeSpan ReadTimeout,
    TimeSpan WriteTimeout,
    int ReceiveBufferSize = 8192,
    int SendBufferSize = 8192)
{
    public static TransportOptions Default { get; } = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3));
}
