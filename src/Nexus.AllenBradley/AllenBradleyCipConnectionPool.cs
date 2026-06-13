using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.AllenBradley
{
    /// <summary>
    /// Allen-Bradley EtherNet/IP CIP 连接池 — 复用已注册 ENIP Session 的连接。
    /// </summary>
    public sealed class AllenBradleyCipConnectionPool : IDisposable
    {
        private readonly ConnectionPool<AllenBradleyCipClient> _pool;
        private readonly string _key;
        private readonly string _ipAddress;
        private readonly int _port;
        private readonly byte _slot;
        private readonly int _timeout;
        private readonly int _maxPduSize;
        private readonly Endianness _byteOrder;
        private readonly ILogger _logger;

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        public AllenBradleyCipConnectionPool(
            string ipAddress,
            int port = 44818,
            byte slot = 0,
            int timeout = 5000,
            int maxPduSize = 508,
            Endianness byteOrder = Endianness.LittleEndian,
            int maxPoolSize = 5,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null,
            ILogger? logger = null)
        {
            _ipAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            _port = port;
            _slot = slot;
            _timeout = timeout;
            _maxPduSize = maxPduSize;
            _byteOrder = byteOrder;
            _logger = logger ?? NullLogger.Instance;
            _key = $"{_ipAddress}:{_port}:{_slot}:{_timeout}:{_maxPduSize}:{_byteOrder}";
            _pool = new ConnectionPool<AllenBradleyCipClient>(
                CreateClient,
                maxPoolSize,
                idleTimeout,
                cleanupInterval);
        }

        public int ActiveCount => _pool.ActiveCount;
        public int IdleCount => _pool.IdleCount;

        public OperateResult<T> Execute<T>(Func<AllenBradleyCipClient, OperateResult<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            AllenBradleyCipClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"AllenBradley CIP 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public OperateResult Execute(Func<AllenBradleyCipClient, OperateResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            AllenBradleyCipClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"AllenBradley CIP 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public async Task<OperateResult<T>> ExecuteAsync<T>(
            Func<AllenBradleyCipClient, Task<OperateResult<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            AllenBradleyCipClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"AllenBradley CIP 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public async Task<OperateResult> ExecuteAsync(
            Func<AllenBradleyCipClient, Task<OperateResult>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            AllenBradleyCipClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"AllenBradley CIP 连接池操作失败: {ex.Message}");
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

        public OperateResult<CipIdentity> ReadDeviceIdentity() => Execute(c => c.ReadDeviceIdentity());
        public OperateResult<byte[]> ReadTagFragmented(string tagName, uint offset = 0, uint count = 0)
            => Execute(c => c.ReadTagFragmented(tagName, offset, count));
        public OperateResult WriteTagFragmented(string tagName, byte[] data, uint offset = 0)
            => Execute(c => c.WriteTagFragmented(tagName, data, offset));

        public void Clear() => _pool.Clear();
        public void Dispose() => _pool.Dispose();

        private AllenBradleyCipClient CreateClient()
        {
            var client = new AllenBradleyCipClient(_ipAddress, _port, _slot, _timeout)
            {
                MaxPduSize = _maxPduSize,
                ByteOrder = _byteOrder
            };
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
