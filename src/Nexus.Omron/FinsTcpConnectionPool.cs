using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Omron
{
    /// <summary>
    /// Omron FINS TCP 连接池 — 复用已完成 FINS 握手的持久连接。
    /// </summary>
    public sealed class FinsTcpConnectionPool : IDisposable
    {
        private readonly ConnectionPool<FinsTcpClient> _pool;
        private readonly string _key;
        private readonly string _ip;
        private readonly int _port;
        private readonly int _timeout;
        private readonly byte _sna;
        private readonly byte _sa2;
        private readonly byte _dna;
        private readonly byte _da2;
        private readonly Endianness _byteOrder;
        private readonly FinsStringEncoding _stringEncoding;
        private readonly ILogger _logger;

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        public FinsTcpConnectionPool(
            string ip,
            int port = 9600,
            int timeout = 5000,
            byte sna = 0x00,
            byte sa2 = 0x00,
            byte dna = 0x00,
            byte da2 = 0x00,
            Endianness byteOrder = Endianness.BigEndian,
            FinsStringEncoding stringEncoding = FinsStringEncoding.Ascii,
            int maxPoolSize = 5,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null,
            ILogger? logger = null)
        {
            _ip = ip ?? throw new ArgumentNullException(nameof(ip));
            _port = port;
            _timeout = timeout;
            _sna = sna;
            _sa2 = sa2;
            _dna = dna;
            _da2 = da2;
            _byteOrder = byteOrder;
            _stringEncoding = stringEncoding;
            _logger = logger ?? NullLogger.Instance;
            _key = $"{_ip}:{_port}:{_sna}:{_sa2}:{_dna}:{_da2}:{_byteOrder}:{_stringEncoding}";
            _pool = new ConnectionPool<FinsTcpClient>(
                CreateClient,
                maxPoolSize,
                idleTimeout,
                cleanupInterval);
        }

        public int ActiveCount => _pool.ActiveCount;
        public int IdleCount => _pool.IdleCount;

        public OperateResult<T> Execute<T>(Func<FinsTcpClient, OperateResult<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            FinsTcpClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Omron FINS TCP 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public OperateResult Execute(Func<FinsTcpClient, OperateResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            FinsTcpClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Omron FINS TCP 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public async Task<OperateResult<T>> ExecuteAsync<T>(
            Func<FinsTcpClient, Task<OperateResult<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            FinsTcpClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Omron FINS TCP 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public async Task<OperateResult> ExecuteAsync(
            Func<FinsTcpClient, Task<OperateResult>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            FinsTcpClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Omron FINS TCP 连接池操作失败: {ex.Message}");
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

        public OperateResult Run() => Execute(c => c.Run());
        public OperateResult Stop() => Execute(c => c.Stop());
        public OperateResult<byte> ReadCpuStatus() => Execute(c => c.ReadCpuStatus());
        public OperateResult<byte[]> ReadCpuUnitData() => Execute(c => c.ReadCpuUnitData());
        public OperateResult<string> ReadPlcModel() => Execute(c => c.ReadPlcModel());
        public OperateResult<DateTime> ReadCpuTime() => Execute(c => c.ReadCpuTime());
        public OperateResult WriteCpuTime(DateTime time) => Execute(c => c.WriteCpuTime(time));

        public void Clear() => _pool.Clear();
        public void Dispose() => _pool.Dispose();

        private FinsTcpClient CreateClient()
        {
            var client = new FinsTcpClient(_ip, _port, _timeout)
            {
                SNA = _sna,
                SA2 = _sa2,
                DNA = _dna,
                DA2 = _da2,
                ByteOrder = _byteOrder,
                StringEncoding = _stringEncoding
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
