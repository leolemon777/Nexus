using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Omron
{
    /// <summary>
    /// Omron HostLink 连接池 — 复用以 CR 分帧的持久 TCP 连接。
    /// </summary>
    public sealed class OmronHostLinkConnectionPool : IDisposable
    {
        private readonly ConnectionPool<OmronHostLinkClient> _pool;
        private readonly string _key;
        private readonly string _ip;
        private readonly int _port;
        private readonly int _timeout;
        private readonly byte _unitNumber;
        private readonly byte _icf;
        private readonly byte _da2;
        private readonly byte _sa2;
        private readonly byte _sid;
        private readonly byte _responseWaitTime;
        private readonly int _readSplits;
        private readonly ILogger _logger;

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        public OmronHostLinkConnectionPool(
            string ip,
            int port = 9600,
            int timeout = 5000,
            byte unitNumber = 0,
            byte icf = 0x00,
            byte da2 = 0x00,
            byte sa2 = 0x00,
            byte sid = 0x00,
            byte responseWaitTime = (byte)'0',
            int readSplits = 260,
            int maxPoolSize = 5,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null,
            ILogger? logger = null)
        {
            _ip = ip ?? throw new ArgumentNullException(nameof(ip));
            _port = port;
            _timeout = timeout;
            _unitNumber = unitNumber;
            _icf = icf;
            _da2 = da2;
            _sa2 = sa2;
            _sid = sid;
            _responseWaitTime = responseWaitTime;
            _readSplits = readSplits;
            _logger = logger ?? NullLogger.Instance;
            _key = $"{_ip}:{_port}:{_timeout}:{_unitNumber}:{_icf}:{_da2}:{_sa2}:{_sid}:{_responseWaitTime}:{_readSplits}";
            _pool = new ConnectionPool<OmronHostLinkClient>(
                CreateClient,
                maxPoolSize,
                idleTimeout,
                cleanupInterval);
        }

        public int ActiveCount => _pool.ActiveCount;
        public int IdleCount => _pool.IdleCount;

        public OperateResult<T> Execute<T>(Func<OmronHostLinkClient, OperateResult<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            OmronHostLinkClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Omron HostLink 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public OperateResult Execute(Func<OmronHostLinkClient, OperateResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            OmronHostLinkClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Omron HostLink 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public async Task<OperateResult<T>> ExecuteAsync<T>(
            Func<OmronHostLinkClient, Task<OperateResult<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            OmronHostLinkClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Omron HostLink 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public async Task<OperateResult> ExecuteAsync(
            Func<OmronHostLinkClient, Task<OperateResult>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            OmronHostLinkClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Omron HostLink 连接池操作失败: {ex.Message}");
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

        public void Clear() => _pool.Clear();

        public void Dispose() => _pool.Dispose();

        private OmronHostLinkClient CreateClient()
        {
            var client = new OmronHostLinkClient(_ip, _port, _timeout)
            {
                UnitNumber = _unitNumber,
                ICF = _icf,
                DA2 = _da2,
                SA2 = _sa2,
                SID = _sid,
                ResponseWaitTime = _responseWaitTime,
                ReadSplits = _readSplits
            };
            client.OnMessageSent += Client_OnMessageSent;
            client.OnMessageReceived += Client_OnMessageReceived;
            client.OnError += Client_OnError;
            client.SetPersistentConnection();
            client.SetLogger(_logger);
            return client;
        }

        private void Client_OnMessageSent(object? sender, string hex) => OnMessageSent?.Invoke(this, hex);
        private void Client_OnMessageReceived(object? sender, string hex) => OnMessageReceived?.Invoke(this, hex);
        private void Client_OnError(object? sender, string message) => OnError?.Invoke(this, message);
    }
}
