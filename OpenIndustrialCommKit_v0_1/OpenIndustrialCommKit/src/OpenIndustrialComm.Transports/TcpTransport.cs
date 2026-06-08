using System.Net.Sockets;

namespace OpenIndustrialComm.Transports;

public sealed class TcpTransport : ITransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly TransportOptions _options;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpTransport(string host, int port, TransportOptions? options = null)
    {
        _host = host;
        _port = port;
        _options = options ?? TransportOptions.Default;
    }

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;

        _client = new TcpClient
        {
            ReceiveBufferSize = _options.ReceiveBufferSize,
            SendBufferSize = _options.SendBufferSize,
            NoDelay = true
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.ConnectTimeout);
        await _client.ConnectAsync(_host, _port, timeoutCts.Token).ConfigureAwait(false);
        _stream = _client.GetStream();
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return Task.CompletedTask;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_stream is null) throw new InvalidOperationException("TCP transport is not connected.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.WriteTimeout);
        await _stream.WriteAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
        await _stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_stream is null) throw new InvalidOperationException("TCP transport is not connected.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.ReadTimeout);
        return await _stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }
}
