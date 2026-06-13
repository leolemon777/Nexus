using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Schneider
{
    /// <summary>
    /// Schneider Modicon 连接池 — 复用持久 Modbus TCP 连接，降低高频读写和批量操作的建连成本。
    /// </summary>
    public sealed class SchneiderConnectionPool : IDisposable
    {
        private readonly ConnectionPool<SchneiderModiconClient> _pool;
        private readonly string _key;
        private readonly string _ip;
        private readonly int _port;
        private readonly int _timeout;
        private readonly byte _slaveId;
        private readonly Endianness _byteOrder;
        private readonly ILogger _logger;

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        public SchneiderConnectionPool(
            string ip,
            int port = 502,
            int timeout = 5000,
            byte slaveId = 1,
            Endianness byteOrder = Endianness.BigEndian,
            int maxPoolSize = 5,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null,
            ILogger? logger = null)
        {
            _ip = ip ?? throw new ArgumentNullException(nameof(ip));
            _port = port;
            _timeout = timeout;
            _slaveId = slaveId;
            _byteOrder = byteOrder;
            _logger = logger ?? NullLogger.Instance;
            _key = $"{_ip}:{_port}:{_timeout}:{_slaveId}:{_byteOrder}";
            _pool = new ConnectionPool<SchneiderModiconClient>(
                CreateClient,
                maxPoolSize,
                idleTimeout,
                cleanupInterval);
        }

        public int ActiveCount => _pool.ActiveCount;
        public int IdleCount => _pool.IdleCount;

        public OperateResult<T> Execute<T>(Func<SchneiderModiconClient, OperateResult<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SchneiderModiconClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Schneider Modicon 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public OperateResult Execute(Func<SchneiderModiconClient, OperateResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SchneiderModiconClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Schneider Modicon 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public async Task<OperateResult<T>> ExecuteAsync<T>(
            Func<SchneiderModiconClient, Task<OperateResult<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SchneiderModiconClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Schneider Modicon 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public async Task<OperateResult> ExecuteAsync(
            Func<SchneiderModiconClient, Task<OperateResult>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SchneiderModiconClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Schneider Modicon 连接池操作失败: {ex.Message}");
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

        private SchneiderModiconClient CreateClient()
        {
            var client = new SchneiderModiconClient(_ip, _port, _timeout)
            {
                SlaveId = _slaveId,
                ByteOrder = _byteOrder
            };
            client.OnMessageSent += Client_OnMessageSent;
            client.OnMessageReceived += Client_OnMessageReceived;
            client.OnError += Client_OnError;
            client.SetPersistentConnection();
            client.SetLogger(_logger);
            return client;
        }

        private void Client_OnMessageSent(object? sender, string message) => OnMessageSent?.Invoke(this, message);
        private void Client_OnMessageReceived(object? sender, string message) => OnMessageReceived?.Invoke(this, message);
        private void Client_OnError(object? sender, string message) => OnError?.Invoke(this, message);
    }
}
