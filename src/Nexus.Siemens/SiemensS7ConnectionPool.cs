using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Siemens
{
    /// <summary>
    /// Siemens S7 连接池 — 复用已完成 COTP/S7 握手的持久连接。
    /// </summary>
    public sealed class SiemensS7ConnectionPool : IDisposable
    {
        private readonly ConnectionPool<SiemensS7Client> _pool;
        private readonly string _key;
        private readonly SiemensPLCS _plcType;
        private readonly string _ip;
        private readonly int _port;
        private readonly int _timeout;
        private readonly byte? _rack;
        private readonly byte? _slot;
        private readonly byte _connectionType;
        private readonly Endianness _byteOrder;
        private readonly ILogger _logger;

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        public SiemensS7ConnectionPool(
            SiemensPLCS plcType,
            string ip,
            int port = 102,
            int timeout = 5000,
            byte? rack = null,
            byte? slot = null,
            byte connectionType = 0x03,
            Endianness byteOrder = Endianness.BigEndian,
            int maxPoolSize = 5,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null,
            ILogger? logger = null)
        {
            _plcType = plcType;
            _ip = ip ?? throw new ArgumentNullException(nameof(ip));
            _port = port;
            _timeout = timeout;
            _rack = rack;
            _slot = slot;
            _connectionType = connectionType;
            _byteOrder = byteOrder;
            _logger = logger ?? NullLogger.Instance;
            _key = $"{_plcType}:{_ip}:{_port}:{_rack}:{_slot}:{_connectionType}:{_byteOrder}";
            _pool = new ConnectionPool<SiemensS7Client>(
                CreateClient,
                maxPoolSize,
                idleTimeout,
                cleanupInterval);
        }

        public int ActiveCount => _pool.ActiveCount;
        public int IdleCount => _pool.IdleCount;

        public OperateResult<T> Execute<T>(Func<SiemensS7Client, OperateResult<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SiemensS7Client? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Siemens S7 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public OperateResult Execute(Func<SiemensS7Client, OperateResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SiemensS7Client? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Siemens S7 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public async Task<OperateResult<T>> ExecuteAsync<T>(
            Func<SiemensS7Client, Task<OperateResult<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SiemensS7Client? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Siemens S7 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public async Task<OperateResult> ExecuteAsync(
            Func<SiemensS7Client, Task<OperateResult>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SiemensS7Client? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Siemens S7 连接池操作失败: {ex.Message}");
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

        public void Clear() => _pool.Clear();
        public void Dispose() => _pool.Dispose();

        private SiemensS7Client CreateClient()
        {
            var client = new SiemensS7Client(_plcType, _ip, _port, _timeout)
            {
                ConnectionType = _connectionType,
                ByteOrder = _byteOrder
            };
            if (_rack.HasValue) client.Rack = _rack.Value;
            if (_slot.HasValue) client.Slot = _slot.Value;
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
