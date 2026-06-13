using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Beckhoff
{
    /// <summary>
    /// Beckhoff ADS 连接池 — 复用 ADS TCP 连接，适合高频变量轮询和批量读写。
    /// </summary>
    public sealed class BeckhoffAdsConnectionPool : IDisposable
    {
        private readonly ConnectionPool<BeckhoffAdsClient> _pool;
        private readonly string _key;
        private readonly string _ipAddress;
        private readonly int _port;
        private readonly int _timeout;
        private readonly string _localNetId;
        private readonly ushort _localPort;
        private readonly string? _targetNetId;
        private readonly ushort _targetPort;
        private readonly ILogger _logger;

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        public BeckhoffAdsConnectionPool(
            string ipAddress,
            int port = 48898,
            int timeout = 5000,
            string localNetId = "127.0.0.1.1.1",
            ushort localPort = 32768,
            string? targetNetId = null,
            ushort targetPort = 851,
            int maxPoolSize = 5,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null,
            ILogger? logger = null)
        {
            _ipAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            _port = port;
            _timeout = timeout;
            _localNetId = localNetId ?? throw new ArgumentNullException(nameof(localNetId));
            _localPort = localPort;
            _targetNetId = targetNetId;
            _targetPort = targetPort;
            _logger = logger ?? NullLogger.Instance;
            _key = $"{_ipAddress}:{_port}:{_timeout}:{_localNetId}:{_localPort}:{_targetNetId}:{_targetPort}";
            _pool = new ConnectionPool<BeckhoffAdsClient>(
                CreateClient,
                maxPoolSize,
                idleTimeout,
                cleanupInterval);
        }

        public int ActiveCount => _pool.ActiveCount;
        public int IdleCount => _pool.IdleCount;

        public OperateResult<T> Execute<T>(Func<BeckhoffAdsClient, OperateResult<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            BeckhoffAdsClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Beckhoff ADS 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public OperateResult Execute(Func<BeckhoffAdsClient, OperateResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            BeckhoffAdsClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Beckhoff ADS 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public async Task<OperateResult<T>> ExecuteAsync<T>(
            Func<BeckhoffAdsClient, Task<OperateResult<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            BeckhoffAdsClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Beckhoff ADS 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public async Task<OperateResult> ExecuteAsync(
            Func<BeckhoffAdsClient, Task<OperateResult>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            BeckhoffAdsClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Beckhoff ADS 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public OperateResult<bool> ReadBool(string address) => Execute(c => c.ReadBool(address));
        public OperateResult<short> ReadInt16(string address) => Execute(c => c.ReadInt16(address));
        public OperateResult<ushort> ReadUInt16(string address) => Execute(c => c.ReadUInt16(address));
        public OperateResult<int> ReadInt32(string address) => Execute(c => c.ReadInt32(address));
        public OperateResult<uint> ReadUInt32(string address) => Execute(c => c.ReadUInt32(address));
        public OperateResult<long> ReadInt64(string address) => Execute(c => c.ReadInt64(address));
        public OperateResult<ulong> ReadUInt64(string address) => Execute(c => c.ReadUInt64(address));
        public OperateResult<float> ReadFloat(string address) => Execute(c => c.ReadFloat(address));
        public OperateResult<double> ReadDouble(string address) => Execute(c => c.ReadDouble(address));
        public OperateResult<string> ReadString(string address, ushort length) => Execute(c => c.ReadString(address, length));
        public OperateResult<byte[]> ReadBytes(string address, ushort length) => Execute(c => c.ReadBytes(address, length));

        public OperateResult Write(string address, bool value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, short value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, ushort value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, int value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, uint value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, long value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, ulong value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, float value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, double value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, string value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, byte[] data) => Execute(c => c.Write(address, data));

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
            => Execute(c => c.BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
            => Execute(c => c.RandomRead(addresses));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
            => Execute(c => c.BatchWrite(items));

        public OperateResult<AdsDeviceInfo> ReadDeviceInfo() => Execute(c => c.ReadDeviceInfo());
        public OperateResult<AdsState> ReadState() => Execute(c => c.ReadState());
        public OperateResult WriteControl(ushort adsState, ushort deviceState, byte[] deviceData)
            => Execute(c => c.WriteControl(adsState, deviceState, deviceData));
        public OperateResult Run() => Execute(c => c.Run());
        public OperateResult Stop() => Execute(c => c.Stop());

        public void Clear() => _pool.Clear();
        public void Dispose() => _pool.Dispose();

        private BeckhoffAdsClient CreateClient()
        {
            var client = new BeckhoffAdsClient(_ipAddress, _port, _timeout)
            {
                LocalNetId = _localNetId,
                LocalPort = _localPort,
                TargetPort = _targetPort
            };
            if (_targetNetId != null && _targetNetId.Length > 0)
                client.TargetNetId = _targetNetId;

            client.OnMessageSent += Client_OnMessageSent;
            client.OnMessageReceived += Client_OnMessageReceived;
            client.OnError += Client_OnError;
            client.SetLogger(_logger);
            return client;
        }

        private void Client_OnMessageSent(object? sender, string message) => OnMessageSent?.Invoke(this, message);
        private void Client_OnMessageReceived(object? sender, string message) => OnMessageReceived?.Invoke(this, message);
        private void Client_OnError(object? sender, string message) => OnError?.Invoke(this, message);
    }
}
